from pathlib import Path
import typer
from matplotlib import pyplot as plt

from ._cli import cli
from ..ts_ctf.plot_ts_ctf_card_view import plot_ts_ctf_card_view


@cli.command(no_args_is_help=True, help="Creates a ts-ctf card view with PNG thumbnail and CTF plot")
def ts_ctf_card_view(
    png_file: Path = typer.Option(..., help="Path to the PNG thumbnail file"),
    tilt_series_xml_file: Path = typer.Option(..., help="Path to the tilt series XML file"),
    output_file: Path = typer.Option(..., help="Path where the output card view image will be saved")
):
    """Generate a card view showing PNG thumbnail and CTF plot side by side."""
    
    # Create figure with 2 columns, following job card format
    fig, axs = plt.subplots(ncols=2, figsize=(2, 1))
    
    # Plot the ts-ctf card view
    plot_ts_ctf_card_view(
        ax1=axs[0],
        ax2=axs[1],
        png_file=png_file,
        tilt_series_xml_file=tilt_series_xml_file
    )
    
    # Save with same settings as other job cards
    plt.tight_layout(pad=0.1)
    fig.savefig(output_file, dpi=288)
    fig.savefig(str(output_file.with_suffix('.pdf')), dpi=288)