from pathlib import Path

import matplotlib.axes
import mrcfile
import numpy as np
from matplotlib import pyplot as plt

from ..orthoslices.slice import take_slice
from ..image_utils import process_image_for_visualization


def plot_ts_export_particles_job_card(
    mrc_file_1: Path,
    mrc_file_2: Path,
    pixel_size: float,
    particle_diameter: float,
    output_file: Path,
):
    """
    Create a job card visualization showing central slices of two MRC files
    with particle circles overlaid.
    
    Args:
        mrc_file_1: Path to first MRC file
        mrc_file_2: Path to second MRC file  
        pixel_size: Pixel size in Angstroms
        particle_diameter: Particle diameter in Angstroms
        output_file: Output PNG file path
    """
    # Load and process both MRC files
    with mrcfile.mmap(mrc_file_1) as mrc1:
        # Average the central 50% of XY slices for the first MRC file
        z_size = mrc1.data.shape[0]
        z_start = int(z_size * 0.25)
        z_end = int(z_size * 0.75)
        slice1 = np.mean(mrc1.data[z_start:z_end], axis=0)
    
    with mrcfile.mmap(mrc_file_2) as mrc2:
        slice2 = take_slice(mrc2.data, axis='z', thickness=1)
    
    # Process images for visualization
    image1 = process_image_for_visualization(slice1)
    image2 = process_image_for_visualization(slice2)
    
    # Create figure with two panels
    fig, (ax1, ax2) = plt.subplots(1, 2, figsize=(2, 1))
    
    # Draw both slices with particle circles
    _draw_slice_with_particle_circle(ax1, image1, pixel_size, particle_diameter, mrc_file_1.name)
    _draw_slice_with_particle_circle(ax2, image2, pixel_size, particle_diameter, mrc_file_2.name)
    
    # Save figure
    fig.tight_layout(pad=0.1)
    fig.savefig(output_file, dpi=288)
    fig.savefig(str(output_file.with_suffix('.pdf')), dpi=288)
    plt.close(fig)


def _draw_slice_with_particle_circle(
    ax: matplotlib.axes.Axes,
    image: np.ndarray,
    pixel_size: float,
    particle_diameter: float,
    filename: str,
):
    """
    Draw a slice with a particle circle overlay.
    
    Args:
        ax: Matplotlib axes
        image: 2D image array
        pixel_size: Pixel size in Angstroms
        particle_diameter: Particle diameter in Angstroms
        filename: Filename to display in top left corner
    """
    # Display the image
    ax.imshow(image, cmap='gray', origin='lower', interpolation='sinc')
    
    # Calculate particle radius in pixels
    particle_radius_pixels = (particle_diameter / 2) / pixel_size
    
    # Get image center (1-based indexing, accounting for origin difference)
    h, w = image.shape
    center_x = w / 2 + 1
    center_y = h / 2 - 1
    
    # Draw particle circle
    circle = plt.Circle(
        (center_x, center_y), 
        particle_radius_pixels, 
        fill=False, 
        color='yellow', 
        linewidth=0.5
    )
    ax.add_patch(circle)
    
    # Set axis limits to image bounds
    ax.set_xlim(0, w)
    ax.set_ylim(0, h)
    
    # Add filename label in top left corner
    ax.text(
        0.02, 0.98,
        filename,
        color='white',
        fontsize=4,
        transform=ax.transAxes,
        verticalalignment='top',
        horizontalalignment='left'
    )
    
    # Remove axes
    ax.set_axis_off()