from pathlib import Path
import typer
from matplotlib import pyplot as plt

from ._cli import cli
from ..ts_select_particles.plot_ts_select_particles_job_card import plot_ts_select_particles_job_card


@cli.command(no_args_is_help=True, help="Creates a job card visualization for particle selection results with two tomograms and particle positions")
def ts_select_particles_job_card(
    mrc_file_1: Path = typer.Option(..., help="Path to the first tomogram MRC file"),
    star_file_1: Path = typer.Option(..., help="Path to the first STAR file containing particle coordinates"),
    mrc_file_2: Path = typer.Option(..., help="Path to the second tomogram MRC file"),
    star_file_2: Path = typer.Option(..., help="Path to the second STAR file containing particle coordinates"),
    particle_diameter_angstroms: float = typer.Option(..., help="Particle diameter in angstroms"),
    output_file: Path = typer.Option(..., help="Path where the output job card image will be saved")
):
    """Generate a job card visualization showing two tomogram slices with particle positions as yellow circles."""
    
    # Create figure with 2 columns, following job card format
    fig, axs = plt.subplots(ncols=2, figsize=(2, 1))
    
    # Plot the particle selection job card
    plot_ts_select_particles_job_card(
        ax1=axs[0],
        ax2=axs[1],
        mrc_file_1=mrc_file_1,
        star_file_1=star_file_1,
        mrc_file_2=mrc_file_2,
        star_file_2=star_file_2,
        particle_diameter_angstroms=particle_diameter_angstroms
    )
    
    # Save with same settings as other job cards
    plt.tight_layout(pad=0.1)
    fig.savefig(output_file, dpi=288)
    fig.savefig(str(output_file.with_suffix('.pdf')), dpi=288)