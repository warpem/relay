from io import BytesIO
from pathlib import Path
from typing import Optional
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


@cli.command(no_args_is_help=True, help="visualize M-refine results with variable number of species")
def m_refine_job_card(
    species_folder: Path = typer.Option(...),
    output_file: Path = typer.Option(...),
    species: str = typer.Option(None, help="Optional: specify a single species name to visualize instead of all species"),
):
    if species:
        # single species specified: find and plot that specific species
        species_path = species_folder / species
        if not species_path.exists() or not species_path.is_dir():
            raise ValueError(f"Species folder '{species}' not found in {species_folder}")
        _plot_single_species(species_path, output_file)
    else:
        # discover species folders
        species_folders = [f for f in species_folder.iterdir() if f.is_dir()]
        species_folders.sort()  # ensure consistent ordering
        
        n_species = len(species_folders)
        
        if n_species == 0:
            raise ValueError("No species folders found")
        
        # limit to 20 species maximum (following class3d-job-card)
        if n_species > 20:
            species_folders = species_folders[:20]
            n_species = 20
        
        if n_species == 1:
            # single species: plot like m-species-job-card (map slice + FSC curves)
            _plot_single_species(species_folders[0], output_file)
        else:
            # multiple species: plot only xy slices with resolution labels
            _plot_multiple_species(species_folders, output_file)


def _plot_single_species(species_folder: Path, output_file: Path):
    """Plot single species like m-species-job-card with map slice and FSC curves."""
    species_name = species_folder.name
    
    # find required files
    volume_file = species_folder / f"{species_name}_denoised.mrc"
    fsc_star_file = species_folder / f"{species_name}_fsc.star"
    species_xml_file = species_folder / f"{species_name}.species"
    
    # validate files exist
    if not volume_file.exists():
        raise FileNotFoundError(f"Volume file not found: {volume_file}")
    if not fsc_star_file.exists():
        raise FileNotFoundError(f"FSC star file not found: {fsc_star_file}")
    if not species_xml_file.exists():
        raise FileNotFoundError(f"Species XML file not found: {species_xml_file}")
    
    # parse star file for FSC curve (single unnamed table)
    star = starfile.read(fsc_star_file)
    
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


def _plot_multiple_species(species_folders: list, output_file: Path):
    """Plot multiple species as xy slices only with resolution labels."""
    n_species = len(species_folders)
    
    # determine grid layout (following class3d-job-card logic)
    if n_species <= 5:
        n_rows, n_cols = 1, n_species
    elif 5 < n_species <= 8:
        n_rows, n_cols = 2, 4
    elif 8 < n_species <= 12:
        n_rows, n_cols = 2, 6
    elif 12 < n_species <= 16:
        n_rows, n_cols = 2, 8
    elif 16 < n_species <= 20:
        n_rows, n_cols = 2, 10
    
    aspect_ratio = n_cols / n_rows
    
    # collect volume slices and resolution values
    volume_slices = []
    resolution_values = []
    species_names = []
    
    for species_folder in species_folders:
        species_name = species_folder.name
        species_names.append(species_name)
        
        # find required files
        volume_file = species_folder / f"{species_name}_denoised.mrc"
        species_xml_file = species_folder / f"{species_name}.species"
        
        # validate files exist
        if not volume_file.exists():
            raise FileNotFoundError(f"Volume file not found: {volume_file}")
        if not species_xml_file.exists():
            raise FileNotFoundError(f"Species XML file not found: {species_xml_file}")
        
        # grab volume slice
        slice = take_slice(mrcfile.mmap(volume_file).data, axis='z', thickness=1)
        volume_slices.append(slice)
        
        # parse XML file for GlobalResolution
        tree = ET.parse(species_xml_file)
        root = tree.getroot()
        global_resolution = None
        for param in root.findall('Param'):
            if param.get('Name') == 'GlobalResolution':
                global_resolution = float(param.get('Value'))
                break
        resolution_values.append(global_resolution)
    
    # stack and normalize volume slices (following class3d-job-card)
    volume_slices = np.stack(volume_slices)
    idx_nonzero = np.abs(volume_slices) > 1e-8
    n_nonzero = np.sum(idx_nonzero)
    normalized_l2_norm = np.linalg.norm(volume_slices[idx_nonzero]) / np.sqrt(n_nonzero)
    volume_slices = volume_slices / normalized_l2_norm
    
    # setup plot
    fig, axs = plt.subplots(nrows=n_rows, ncols=n_cols, figsize=(aspect_ratio, 1))
    
    # ensure axs is a flat array of Axes
    if isinstance(axs, matplotlib.axes.Axes):
        axs = np.array([axs])
    axs = axs.reshape(-1)
    
    # draw each slice
    for i in range(n_rows):
        for j in range(n_cols):
            idx = (i * n_cols) + j
            if idx <= n_species - 1:
                draw_z_slice_panel_with_resolution(
                    ax=axs[idx],
                    image=volume_slices[idx],
                    species_name=species_names[idx],
                    resolution=resolution_values[idx],
                    n_rows=n_rows
                )
            axs[idx].axis('off')
    
    plt.tight_layout(pad=0.1)  # avoids stray white pixels at edges
    fig.savefig(output_file, dpi=288, transparent=True)
    fig.savefig(str(output_file.with_suffix('.pdf')), dpi=288, transparent=True)


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


def draw_z_slice_panel_with_resolution(
    ax: matplotlib.axes.Axes,
    image: np.ndarray,
    species_name: str,
    resolution: float,
    n_rows: int,
):
    # draw image, image==0 at 25% gray (following class3d-job-card)
    std_scale = 5
    ax.imshow(
        image,
        cmap="gray",
        origin="lower",
        interpolation='sinc',
        interpolation_stage='data',
        vmin=-0.25 * std_scale,
        vmax=0.75 * std_scale,
    )
    
    # add species name label (bottom left)
    if n_rows == 1:
        x, y = 0.04, 0.90
    elif n_rows == 2:
        x, y = 0.06, 0.82
    else:
        raise ValueError()
    ax.text(
        x=x, y=y,
        s=species_name,
        color="white",
        fontsize=6,
        transform=ax.transAxes
    )
    
    # add resolution label (bottom right)
    if resolution is not None:
        resolution_text = f'{resolution:.1f}\u2009Å'
    else:
        resolution_text = 'N/A\u2009Å'
    
    ax.text(
        x=0.96, y=0.04,
        s=resolution_text,
        color="white",
        fontsize=6,
        ha='right',
        va='bottom',
        transform=ax.transAxes,
        bbox=dict(boxstyle='round,pad=0.2', facecolor='black', alpha=0.6)
    )


def draw_fsc_panel(ax: matplotlib.axes.Axes, df: pd.DataFrame, global_resolution: float = None):
    fsc_image = _generate_fsc_plot_image(df=df, global_resolution=global_resolution)
    ax.imshow(fsc_image, interpolation='sinc')
    ax.axis('off')


def _draw_fsc_on_axes(ax: matplotlib.axes.Axes, df: pd.DataFrame, global_resolution: Optional[float] = None):
    """Draw M-refine FSC curves directly on the given axes (vector-compatible)."""
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