import matplotlib.axes
from pathlib import Path
import matplotlib.pyplot as plt
from bakery.ctf_utils import draw_ctf_fit_quality_panel


def plot_ts_ctf_card_view(
    ax1: matplotlib.axes.Axes,
    ax2: matplotlib.axes.Axes,
    png_file: Path,
    tilt_series_xml_file: Path
):
    """Plot ts-ctf card view with PNG thumbnail and CTF plot side by side."""
    
    # Load and display the PNG thumbnail
    image = plt.imread(png_file)
    ax1.imshow(image, cmap='gray', aspect='equal', interpolation='sinc')
    ax1.axis('off')
    
    # Draw CTF fit quality panel
    draw_ctf_fit_quality_panel(
        ax=ax2,
        item_xml_file=tilt_series_xml_file
    )