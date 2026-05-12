from pathlib import Path
import typer
from matplotlib import pyplot as plt

from ._cli import cli
from ..ts_template_match.plot_ts_template_match_job_card import plot_ts_template_match_job_card


@cli.command(no_args_is_help=True, help="Creates a job card visualization for template matching with tomogram slice and template")
def ts_template_match_job_card(
    tomogram_mrc_file: Path = typer.Option(..., help="Path to the tomogram MRC file"),
    star_file: Path = typer.Option(..., help="Path to the STAR file containing particle coordinates"),
    template_mrc_file: Path = typer.Option(..., help="Path to the template MRC file"),
    particle_diameter_angstroms: float = typer.Option(..., help="Particle diameter in angstroms"),
    output_file: Path = typer.Option(..., help="Path where the output job card image will be saved")
):
    """Generate a job card visualization showing tomogram slice with particles (left) and template with diameter circle (right)."""
    
    # Create figure with 2 columns, following job card format
    fig, axs = plt.subplots(ncols=2, figsize=(2, 1))
    
    # Plot the template matching job card
    plot_ts_template_match_job_card(
        ax1=axs[0],
        ax2=axs[1],
        tomogram_mrc_file=tomogram_mrc_file,
        star_file=star_file,
        template_mrc_file=template_mrc_file,
        particle_diameter_angstroms=particle_diameter_angstroms
    )
    
    # Save with same settings as other job cards
    plt.tight_layout(pad=0.1)
    fig.savefig(output_file, dpi=288)
    fig.savefig(str(output_file.with_suffix('.pdf')), dpi=288)