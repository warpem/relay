from pathlib import Path

import einops
import mrcfile
import typer
from matplotlib import pyplot as plt

from ._cli import cli


@cli.command(no_args_is_help=True)
def class2d_image_atlas(
    images_mrcs_file: Path = typer.Option(...),
    output_file: Path = typer.Option(...)
):
    # read images
    class2d_images = mrcfile.read(images_mrcs_file)
    class2d_image_atlas = einops.rearrange(class2d_images, '... h w -> h (... w)')

    # render, ensuring that 1px in source is 1px in rendered output
    fig, ax = plt.subplots()
    ax.imshow(class2d_image_atlas, cmap='gray', interpolation='sinc', origin='lower')
    ax.axis('off')
    fig.subplots_adjust(left=0, bottom=0, right=1, top=1)
    h, w = class2d_image_atlas.shape
    fig.set_size_inches(h=h / fig.dpi, w=w / fig.dpi)
    plt.savefig(output_file, bbox_inches='tight', pad_inches=0, dpi=fig.dpi)
