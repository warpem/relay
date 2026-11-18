from pathlib import Path

import matplotlib.pyplot as plt
import typer

from ._cli import cli
from ..map_preview import read_z_projection, draw_map_preview


@cli.command(no_args_is_help=True, help="z projection of a volume, zoomed to its central two thirds")
def xy_slice(
    volume_file: Path = typer.Option(...),
    slice_thickness_angstroms: float = typer.Option(default=10, help="Legacy option; projections use the full z depth"),
    output_file: Path = typer.Option(...)
):
    projection = read_z_projection(volume_file)
    fig, ax = plt.subplots(nrows=1, ncols=1, figsize=(1, 1))
    draw_map_preview(ax, projection)

    # constrain figure layout to tightly wrap subplots
    plt.tight_layout(pad=0)

    # write output file
    plt.savefig(
        output_file,
        dpi=1200,
    )

    plt.close(fig)
