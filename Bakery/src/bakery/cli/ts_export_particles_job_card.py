from pathlib import Path

import typer

from ._cli import cli
from ..ts_export_particles.plot_ts_export_particles_job_card import plot_ts_export_particles_job_card


@cli.command(no_args_is_help=True, help="job card for ts_export_particles job types")
def ts_export_particles_job_card(
    mrc_file_1: Path = typer.Option(..., help="First MRC file"),
    mrc_file_2: Path = typer.Option(..., help="Second MRC file"),
    pixel_size: float = typer.Option(..., help="Pixel size in Angstroms"),
    particle_diameter: float = typer.Option(..., help="Particle diameter in Angstroms"),
    output_file: Path = typer.Option(..., help="Output PNG file"),
):
    """
    Generate a job card visualization for ts_export_particles jobs.
    Shows central slices of two MRC files with particle circles overlaid.
    """
    plot_ts_export_particles_job_card(
        mrc_file_1=mrc_file_1,
        mrc_file_2=mrc_file_2,
        pixel_size=pixel_size,
        particle_diameter=particle_diameter,
        output_file=output_file,
    )