from pathlib import Path

import einops
import matplotlib.pyplot as plt
import mrcfile
import numpy as np
import typer
from scipy import ndimage
from cmap import Colormap, Color

from ._cli import cli
from ..orthoslices.slice import take_slice


@cli.command(no_args_is_help=True, help="generate an atlas image containing YZ, XZ and XY central slices through a cubic volume containing a mask")
def mask_orthoslice_atlas(
    volume_file: Path = typer.Option(...),
    slice_thickness_px: int = 1,
    binarize: bool = typer.Option(False, help="show only where the mask is greater than or equal to the binarization threshold"),
    binarization_threshold: float = 1,
    output_file: Path = typer.Option(...)
):
    # read volume into a memory mapped (d, h, w) numpy array
    volume = mrcfile.mmap(volume_file).data
    d, h, w = volume.shape[-3:]

    # grab slices
    yz = take_slice(volume, axis='x', thickness=slice_thickness_px, reduction_func=np.max)
    xz = take_slice(volume, axis='y', thickness=slice_thickness_px, reduction_func=np.max)
    xy = take_slice(volume, axis='z', thickness=slice_thickness_px, reduction_func=np.max)

    # binarize
    if binarize:
        yz = (yz >= binarization_threshold).astype(int)
        xz = (xz >= binarization_threshold).astype(int)
        xy = (xy >= binarization_threshold).astype(int)

    # upscale images to (256, 256) with cubic interpolation
    # if edges are shorter than 256px
    if w < 256:
        yz, xz, xy = _resize(yz), _resize(xz), _resize(xy)

    # concatenate images
    slices_image = einops.rearrange([yz, xz, xy], 'b h w -> h (b w)')

    # plot with custom colormap where black is transparent
    cmap = Colormap([(0, 0, 0, 0), (255, 255, 255, 255)]).to_mpl()
    fig, ax = plt.subplots()
    ax.imshow(slices_image, cmap=cmap, interpolation='sinc', origin='lower', vmin=0, vmax=1)

    # render, ensuring that 1px in input -> 1px in output
    ax.axis('off')
    fig.subplots_adjust(left=0, bottom=0, right=1, top=1)
    h, w = slices_image.shape
    fig.set_size_inches(h=h / fig.dpi, w=w / fig.dpi)
    plt.savefig(output_file, bbox_inches='tight', pad_inches=0, dpi=fig.dpi, transparent=True)


def _resize(image: np.ndarray):
    h, w = image.shape
    zoom_factors = (256 / h, 256 / w)
    return ndimage.zoom(image, zoom_factors, order=3)
