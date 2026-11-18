from pathlib import Path

import matplotlib
import numpy as np
import typer
from matplotlib import pyplot as plt

from ._cli import cli
from ..map_preview import read_z_projection, draw_map_preview


@cli.command(no_args_is_help=True, help="map projection for import map")
def import_map_3d_job_card(
    volume_file: Path = typer.Option(...),
    output_file: Path = typer.Option(...),
):
    # grab volume projection
    projection = read_z_projection(volume_file)

    # plot...
    fig, ax = plt.subplots(ncols=1, figsize=(1, 1))
    draw_z_slice_panel(ax=ax, image=projection)

    # write output file
    dpi = 2 * 144
    fig.tight_layout(pad=0)
    fig.savefig(output_file, dpi=dpi)
    fig.savefig(str(output_file.with_suffix('.pdf')), dpi=dpi)
    plt.close(fig)


def draw_z_slice_panel(
    ax: matplotlib.axes.Axes,
    image: np.ndarray
):
    draw_map_preview(ax, image)

    # remove axes
    ax.axis('off')
