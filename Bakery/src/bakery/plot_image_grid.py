import matplotlib.figure
import numpy as np
import matplotlib.pyplot as plt


def plot_image_grid(
    images: np.ndarray,  # (nrows, ncols, h, w)
    nrows: int,
    ncols: int,
    rendered_sidelength_pixels: int,
    spacing_pixels: int,
) -> matplotlib.figure.Figure:
    h = w = rendered_sidelength_pixels
    dpi = 300  # arbitrary, we are rendering images at a specific sidelength in px

    # Calculate figure size in inches (1 inch = <dpi> pixels)
    fig_width = (ncols * w + (ncols - 1) * spacing_pixels) / dpi
    fig_height = (nrows * h + (nrows - 1) * spacing_pixels) / dpi

    # Create figure and gridspec for plotting into figure
    fig = plt.figure(figsize=(fig_width, fig_height), dpi=dpi)
    grid = plt.GridSpec(nrows, ncols, wspace=spacing_pixels / w, hspace=spacing_pixels / h)

    # remove margins
    plt.subplots_adjust(left=0, right=1, top=1, bottom=0)

    # plot images
    for i in range(nrows):
        for j in range(ncols):
            ax = fig.add_subplot(grid[i, j])
            ax.imshow(images[i, j], interpolation='sinc', cmap='gray')
            ax.axis('off')

    return fig