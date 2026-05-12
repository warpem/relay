from pathlib import Path

import matplotlib
import mrcfile
import numpy as np
import typer
from matplotlib import pyplot as plt

from ._cli import cli
from ..orthoslices.slice import take_slice


@cli.command(no_args_is_help=True, help="map slice for import map")
def import_map_3d_job_card(
    volume_file: Path = typer.Option(...),
    output_file: Path = typer.Option(...),
):
    # grab volume slice
    slice = take_slice(mrcfile.mmap(volume_file).data, axis='z', thickness=1)

    # plot...
    fig, ax = plt.subplots(ncols=1, figsize=(1, 1))
    draw_z_slice_panel(ax=ax, image=slice)

    # write output file
    dpi = 2 * 144
    fig.tight_layout(pad=0)
    fig.savefig(output_file, dpi=dpi)
    fig.savefig(str(output_file.with_suffix('.pdf')), dpi=dpi)


def draw_z_slice_panel(
    ax: matplotlib.axes.Axes,
    image: np.ndarray
):
    # draw image
    ax.imshow(
        image,
        cmap="gray",
        origin="lower",
        interpolation='sinc',
        interpolation_stage='data'
    )

    # remove axes
    ax.axis('off')
