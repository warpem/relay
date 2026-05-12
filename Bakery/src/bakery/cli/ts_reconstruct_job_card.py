from pathlib import Path
import typer
from matplotlib import pyplot as plt

from ._cli import cli
from ..ts_reconstruct.plot_ts_reconstruct_job_card import plot_ts_reconstruct_job_card


@cli.command(no_args_is_help=True, help="Creates a job card visualization for ts-reconstruct with two PNG files")
def ts_reconstruct_job_card(
    png_file_1: Path = typer.Option(..., help="Path to the first PNG thumbnail file"),
    png_file_2: Path = typer.Option(..., help="Path to the second PNG thumbnail file"),
    output_file: Path = typer.Option(..., help="Path where the output job card image will be saved")
):
    """Generate a job card visualization showing two PNG thumbnail images side by side."""
    
    # Create figure with 2 columns, following import_fs_job_card format
    fig, axs = plt.subplots(ncols=2, figsize=(2, 1))
    
    # Plot the ts-reconstruct data
    plot_ts_reconstruct_job_card(
        ax1=axs[0],
        ax2=axs[1],
        png_file_1=png_file_1,
        png_file_2=png_file_2
    )
    
    # Save with same settings as other job cards
    plt.tight_layout(pad=0.1)
    fig.savefig(output_file, dpi=288)
    fig.savefig(str(output_file.with_suffix('.pdf')), dpi=288)