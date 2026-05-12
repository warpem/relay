from pathlib import Path
from typing import List, Annotated

import matplotlib
import mrcfile
import numpy as np
import typer
from matplotlib import pyplot as plt

from ._cli import cli


@cli.command(no_args_is_help=True)
def class2d_job_card(
    images_mrcs_file: Annotated[Path, typer.Option(...)],
    image_indices: Annotated[List[int], typer.Option("--idx")],
    image_labels: Annotated[List[str], typer.Option("--label")],
    output_file: Annotated[Path, typer.Option(...)],
):
    # validate CLI
    if len(image_indices) != len(image_labels):
        raise ValueError("please provide same number of labels and image indices")

    # read images
    class2d_images = mrcfile.mmap(images_mrcs_file).data

    # get class images and labels
    idx_subset = np.asarray(image_indices)
    images = class2d_images[idx_subset]
    n_images = len(images)

    # normalize
    idx_nonzero = np.abs(images) > 1e-8
    n_nonzero = np.sum(idx_nonzero)
    normalized_l2_norm = np.linalg.norm(images[idx_nonzero]) / np.sqrt(n_nonzero)
    images = images / normalized_l2_norm

    # setup plot
    n_rows, n_cols = 3, 6
    fig, axs = plt.subplots(figsize=(2, 1), nrows=n_rows, ncols=n_cols)

    for i in range(n_rows):
        for j in range(n_cols):
            idx = (i * n_cols) + j
            if idx < n_images:
                draw_z_slice_panel(
                    ax=axs[i, j],
                    image=images[idx],
                    label=image_labels[idx],
                )
            axs[i, j].axis("off")

    plt.tight_layout(pad=0.1)
    fig.savefig(output_file, dpi=2 * 144, transparent=True)
    fig.savefig(str(output_file.with_suffix('.pdf')), dpi=2 * 144, transparent=True)


def draw_z_slice_panel(
    ax: matplotlib.axes.Axes,
    image: np.ndarray,
    label: str,
):
    # draw image, image==0 at 25% gray
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

    # add label
    fontsize = 4
    ax.text(
        x=0.02, y=0.83,
        s=label,
        color="white",
        fontsize=fontsize,
        transform=ax.transAxes  # Specify that coordinates are in fractional space
    )
