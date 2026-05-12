from io import BytesIO
from pathlib import Path
import xml.etree.ElementTree as ET

import matplotlib
import mrcfile
import numpy as np
import pandas as pd
import starfile
import typer
from matplotlib import pyplot as plt

from ._cli import cli
from ..orthoslices.slice import take_slice


@cli.command(no_args_is_help=True, help="map slice and FSC curves from M-species refinement")
def m_species_job_card(
    volume_file: Path = typer.Option(...),
    model_star_file: Path = typer.Option(...),
    species_xml_file: Path = typer.Option(...),
    output_file: Path = typer.Option(...),
):
    # parse star file for FSC curve (single unnamed table)
    star = starfile.read(model_star_file)

    # parse XML file for GlobalResolution
    tree = ET.parse(species_xml_file)
    root = tree.getroot()
    global_resolution = None
    for param in root.findall('Param'):
        if param.get('Name') == 'GlobalResolution':
            global_resolution = float(param.get('Value'))
            break

    # grab volume slice
    slice = take_slice(mrcfile.mmap(volume_file).data, axis='z', thickness=1)

    # plot...
    fig, axs = plt.subplots(ncols=2, figsize=(2, 1))
    draw_z_slice_panel(ax=axs[0], image=slice)
    draw_fsc_panel(ax=axs[1], df=star, global_resolution=global_resolution)

    # write output file
    dpi = 2 * 144
    fig.tight_layout(pad=0)
    fig.savefig(output_file, dpi=dpi)
    plt.close(fig)

    # write PDF with vector FSC curves
    pdf_fig, pdf_axs = plt.subplots(ncols=2, figsize=(8, 4))
    draw_z_slice_panel(ax=pdf_axs[0], image=slice)
    _draw_fsc_on_axes(pdf_axs[1], star, global_resolution=global_resolution)
    pdf_fig.tight_layout(pad=0.5)
    pdf_fig.savefig(str(output_file.with_suffix('.pdf')))
    plt.close(pdf_fig)


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


def draw_fsc_panel(ax: matplotlib.axes.Axes, df: pd.DataFrame, global_resolution: float = None):
    fsc_image = _generate_fsc_plot_image(df=df, global_resolution=global_resolution)
    ax.imshow(fsc_image, interpolation='sinc')
    ax.axis('off')


def _draw_fsc_on_axes(ax: matplotlib.axes.Axes, df: pd.DataFrame, global_resolution: float = None):
    """Draw M-species FSC curves directly on the given axes (vector-compatible)."""
    # prepare data
    resolution_inv = 1 / df['wrpResolution']

    # phase randomized
    ax.plot(
        resolution_inv,
        df['wrpFSCRandomized'],
        color='#eea9dd'  # seaborn colorblind pink
    )

    # unmasked
    ax.plot(
        resolution_inv,
        df['wrpFSCUnmasked'],
        color='#be5b1eff'  # seaborn colorblind reddish brown
    )

    # corrected
    ax.plot(
        resolution_inv,
        df['wrpFSCCorrected'],
        color='#3d916a',  # seaborn colorblind dark green
    )

    # add 0.143 threshold line
    ax.axhline(y=0.143, color='red', linestyle='--', alpha=0.7, linewidth=1)

    # add resolution label from XML
    xlim = ax.get_xlim()

    if global_resolution is not None:
        label_text = f'{global_resolution:.1f}\u2009Å'
    else:
        label_text = 'N/A\u2009Å'

    ax.text(
        xlim[1] * 0.95,
        1.0,
        label_text,
        fontsize=18,
        ha='right',
        va='top',
        bbox=dict(boxstyle='round,pad=0.3', facecolor='white', alpha=0.8)
    )

    # set axis limits
    ax.set(
        ylim=[-0.1, 1.1]
    )
    ax.set_xlim(left=0)

    # format ticklabels
    def format_xtick(x, pos):
        if abs(x) < 1e-6:
            label = 'DC'
        else:
            label = f'{1 / x:.2f}'
        return label

    ax.xaxis.set_major_formatter(format_xtick)

    # add grid
    ax.grid(True)


def _generate_fsc_plot_image(df: pd.DataFrame, global_resolution: float = None):
    # render to a file buffer
    with plt.rc_context({'font.size': 12}):
        fig, _ax = plt.subplots(figsize=(4, 4))
        _draw_fsc_on_axes(_ax, df, global_resolution=global_resolution)
        fig.tight_layout(pad=0.2)

        buffer = BytesIO()
        fig.savefig(buffer, format='png', dpi=288)
        plt.close(fig)

    # read buffer into numpy array
    buffer.seek(0)
    fsc_image = plt.imread(buffer, format='png')

    return fsc_image
