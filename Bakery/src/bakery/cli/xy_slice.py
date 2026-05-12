from pathlib import Path
from enum import Enum

import matplotlib.pyplot as plt
import mrcfile
import typer

from ._cli import cli
from ..orthoslices import draw_central_orthoslice


@cli.command(no_args_is_help=True, help="central XY slice through a volume")
def xy_slice(
    volume_file: Path = typer.Option(...),
    slice_thickness_angstroms: float = typer.Option(default=10, help="angstroms"),
    output_file: Path = typer.Option(...)
):
    # read volume into (d, h, w) numpy array
    volume = mrcfile.read(volume_file)

    # setup axes in which to plot
    fig, ax = plt.subplots(nrows=1, ncols=1, figsize=(1, 1))
    ax.axis('off')

    # plot...
    # first calculate thickness in pixels
    with mrcfile.open(volume_file, header_only=True) as mrc:
        angstroms_per_pixel = mrc.voxel_size.x
    thickness_px = slice_thickness_angstroms / angstroms_per_pixel

    # then draw
    draw_central_orthoslice(
        ax=ax,
        volume=volume,
        axis_name='z',
        thickness=thickness_px
    )

    # constrain figure layout to tightly wrap subplots
    plt.tight_layout(pad=0)

    # write output file
    plt.savefig(
        output_file,
        dpi=1200,
    )
