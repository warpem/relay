from pathlib import Path

import einops
import matplotlib.pyplot as plt
import mrcfile
import numpy as np
import typer
from scipy import ndimage

from ._cli import cli
from ..orthoslices.slice import take_slice
from ..figure_export import save_figure


@cli.command(no_args_is_help=True, help="generate an atlas image containing YZ, XZ and XY central slices through a cubic volume")
def map_orthoslice_atlas(
    volume_file: Path = typer.Option(...),
    slice_thickness_px: int = 1,
    output_file: Path = typer.Option(...),
    svg: bool = False,
):
    # read volume into a memory mapped (d, h, w) numpy array
    stream = mrcfile.mmap(volume_file)
    volume = stream.data
    voxel_size = tuple(float(stream.voxel_size[name]) for name in ('x', 'y', 'z'))
    d, h, w = volume.shape[-3:]

    # grab slices
    yz = take_slice(volume, axis='x', thickness=slice_thickness_px)
    xz = take_slice(volume, axis='y', thickness=slice_thickness_px)
    xy = take_slice(volume, axis='z', thickness=slice_thickness_px)
    stream.close()

    # upscale images to (256, 256) with cubic interpolation
    # if edges are shorter than 256px
    if w < 256:
        yz, xz, xy = _resize(yz), _resize(xz), _resize(xy)

    # concatenate images
    slices_image = einops.rearrange([xy, xz, yz], 'b h w -> h (b w)')

    # plot
    fig, ax = plt.subplots(figsize=(9.5, 9.5 / 3))
    ax.imshow(slices_image, cmap='gray', interpolation='sinc', origin='lower')

    # add labels
    # Match regular chart text independently of the source map dimensions.
    fontsize = 10
    x, y = 0.04 * 1/3, 0.88
    ax.text(
        x=x, y=y,
        s="XY", gid="fixed-plane-xy",
        color="white",
        fontsize=fontsize,
        transform=ax.transAxes  # Specify that coordinates are in fractional space
    )

    # Fixed-white vector labels/scales stay readable on the unchanged density
    # image in both themes. MRC voxel sizes are Angstroms; omit invalid metadata.
    for i, extent in enumerate((w * voxel_size[0], w * voxel_size[0], h * voxel_size[1])):
        if not np.isfinite(extent) or extent <= 0:
            continue
        unit = 10 ** np.floor(np.log10(extent / 3))
        length = max(value for value in (unit/2, unit, unit*2, unit*5) if value <= extent/3)
        right = (i + 1) / 3 - .015
        left = right - length / extent / 3
        ax.plot([left, right], [.045, .045], transform=ax.transAxes, color='white',
                linewidth=1.5, gid=f'fixed-scale-{i}')
        ax.text((left+right)/2, .065, f'{length:g} Å', transform=ax.transAxes,
                color='white', ha='center', fontsize=fontsize, gid=f'fixed-scale-label-{i}')

    ax.text(
        x=x + 1/3, y=y,
        s="XZ", gid="fixed-plane-xz",
        color="white",
        fontsize=fontsize,
        transform=ax.transAxes  # Specify that coordinates are in fractional space
    )

    ax.text(
        x=x + 2/3, y=y,
        s="YZ", gid="fixed-plane-yz",
        color="white",
        fontsize=fontsize,
        transform=ax.transAxes  # Specify that coordinates are in fractional space
    )

    # render, ensuring that 1px in input -> 1px in output
    ax.axis('off')
    fig.subplots_adjust(left=0, bottom=0, right=1, top=1)
    h, w = slices_image.shape
    fig.set_size_inches(w=9.5, h=9.5 * h / w)
    save_figure(fig, output_file, svg=svg, bbox_inches='tight', pad_inches=0, dpi=w / 9.5)
    plt.close(fig)


def _resize(image: np.ndarray):
    h, w = image.shape
    zoom_factors = (256 / h, 256 / w)
    # Beam may publish float16 MRC maps; SciPy's spline filter requires float32/64.
    return ndimage.zoom(image.astype(np.float32, copy=False), zoom_factors, order=3)
