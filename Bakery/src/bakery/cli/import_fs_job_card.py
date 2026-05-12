from pathlib import Path
import typer
from matplotlib import pyplot as plt

from ._cli import cli
from ..import_fs.plot_fs_job_card import plot_fs_job_card


@cli.command(no_args_is_help=True, help="Creates a job card visualization for imported frame series with two image stacks")
def import_fs_job_card(
    stack_file_1: Path = typer.Option(..., help="Path to the first image stack file (MRC, MRCS, TIF, TIFF, or EER)"),
    stack_file_2: Path = typer.Option(..., help="Path to the second image stack file (MRC, MRCS, TIF, TIFF, or EER)"),
    output_file: Path = typer.Option(..., help="Path where the output job card image will be saved")
):
    """Generate a job card visualization showing averaged images from two frame series stacks side by side."""
    
    # Create figure with 2 columns, following motion_and_ctf_job_card format
    fig, axs = plt.subplots(ncols=2, figsize=(2, 1))
    
    # Plot the frame series data
    plot_fs_job_card(
        ax1=axs[0],
        ax2=axs[1],
        stack_file_1=stack_file_1,
        stack_file_2=stack_file_2
    )
    
    # Save with same settings as motion_and_ctf_job_card
    plt.tight_layout(pad=0.1)
    fig.savefig(output_file, dpi=288)
    fig.savefig(str(output_file.with_suffix('.pdf')), dpi=288)