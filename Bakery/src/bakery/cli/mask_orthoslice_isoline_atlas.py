from pathlib import Path

import einops
import matplotlib.pyplot as plt
import mrcfile
import numpy as np
import typer
from scipy import ndimage

from ._cli import cli
from ..orthoslices.slice import take_slice


@cli.command(no_args_is_help=True, help="isolines at 1 and 0.5 of YZ, XZ and XY central slices through a mask")
def mask_orthoslice_isoline_atlas(
    volume_file: Path = typer.Option(...),
    slice_thickness_px: int = 1,
    isoline_threshold: float = 0.01,
    output_file: Path = typer.Option(...),
):
    # read volume into a memory mapped (d, h, w) numpy array
    volume = mrcfile.mmap(volume_file).data
    d, h, w = volume.shape[-3:]

    # grab slices
    yz = take_slice(volume, axis='x', thickness=slice_thickness_px, reduction_func=np.max)
    xz = take_slice(volume, axis='y', thickness=slice_thickness_px, reduction_func=np.max)
    xy = take_slice(volume, axis='z', thickness=slice_thickness_px, reduction_func=np.max)

    # upscale images to (512, 512) with cubic interpolation
    # if edges are shorter than 512px
    if w < 512:
        yz, xz, xy = _resize(yz), _resize(xz), _resize(xy)

    # concatenate mask slices
    slices_image = einops.rearrange([yz, xz, xy], 'b h w -> h (b w)')
    h, w = slices_image.shape

    # plot and render isoline
    fig, ax = plt.subplots()
    contours = ax.contour(slices_image, levels=[isoline_threshold])
    contours.set_linewidth([0.01 * h * (72 / fig.dpi)])
    contours.set_linestyle(['-'])
    contours.set_edgecolor('white')
    _render(output_file, fig=fig, ax=ax, image_h=h, image_w=w, supersample_factor=2)


def _resize(image: np.ndarray):
    h, w = image.shape
    zoom_factors = (512 / h, 512 / w)
    return ndimage.zoom(image, zoom_factors, order=3)


def _render(filename, fig, ax, image_h, image_w, supersample_factor: float):
    ax.axis('off')
    fig.subplots_adjust(left=0, bottom=0, right=1, top=1)
    h = supersample_factor * (image_h / fig.dpi)
    w = supersample_factor * (image_w / fig.dpi)
    fig.set_size_inches(h=h, w=w)
    plt.savefig(
        filename,
        bbox_inches='tight',
        pad_inches=0,
        dpi=fig.dpi,
        transparent=True,
    )
