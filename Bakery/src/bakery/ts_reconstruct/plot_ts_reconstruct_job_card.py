import matplotlib.axes
from pathlib import Path
import matplotlib.pyplot as plt


def plot_ts_reconstruct_job_card(
    ax1: matplotlib.axes.Axes,
    ax2: matplotlib.axes.Axes,
    png_file_1: Path,
    png_file_2: Path
):
    """Plot ts-reconstruct job card with two PNG thumbnail images side by side."""
    
    # Load the PNG images
    image1 = plt.imread(png_file_1)
    image2 = plt.imread(png_file_2)
    
    # Display the images as-is (no normalization needed)
    ax1.imshow(image1, cmap='gray', aspect='equal', interpolation='sinc')
    ax1.axis('off')
    
    ax2.imshow(image2, cmap='gray', aspect='equal', interpolation='sinc')
    ax2.axis('off')