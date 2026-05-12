from pathlib import Path
from typing import List

import matplotlib.axes
import mrcfile
import numpy as np
import typer
from matplotlib import pyplot as plt

from bakery.cli._cli import cli
from bakery.orthoslices.slice import take_slice


@cli.command(no_args_is_help=True)
def class3d_job_card(
    volume_file: List[Path] = typer.Option(...),
    class_number: List[int] = typer.Option(...),
    output_file: Path = typer.Option(...),
):
    # figure out number of rows/cols
    n_volumes = len(volume_file)

    if n_volumes != len(class_number):
        raise ValueError('Number of volumes does not match the number of class numbers provided.')

    if n_volumes > 20:
        volume_file = volume_file[:20]
        n_volumes = 20

    if n_volumes <= 5:
        n_rows, n_cols = 1, n_volumes
    elif 5 < n_volumes <= 8:
        n_rows, n_cols = 2, 4
    elif 8 < n_volumes <= 12:
        n_rows, n_cols = 2, 6
    elif 12 < n_volumes <= 16:
        n_rows, n_cols = 2, 8
    elif 16 < n_volumes <= 20:
        n_rows, n_cols = 2, 10

    aspect_ratio = n_cols / n_rows

    # take volume z slices
    volume_slices = [
        take_slice(mrcfile.mmap(file).data, axis='z', thickness=1)
        for file
        in volume_file
    ]
    volume_slices = np.stack(volume_slices)

    # normalize volume slices
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
            if idx <= n_volumes - 1:
                draw_z_slice_panel(
                    ax=axs[idx],
                    image=volume_slices[idx],
                    label=class_number[idx],
                    n_rows=n_rows
                )
            axs[idx].axis('off')
    plt.tight_layout(pad=0.1)  # avoids stray white pixels at edges
    fig.savefig(output_file, dpi=288, transparent=True)
    fig.savefig(str(output_file.with_suffix('.pdf')), dpi=288, transparent=True)


def draw_z_slice_panel(
    ax: matplotlib.axes.Axes,
    image: np.ndarray,
    label: str,
    n_rows: int,
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
    if n_rows == 1:
        x, y = 0.04, 0.90
    elif n_rows == 2:
        x, y = 0.06, 0.82
    else:
        raise ValueError()
    ax.text(
        x=x, y=y,
        s=label,
        color="white",
        fontsize=6,
        transform=ax.transAxes  # Specify that coordinates are in fractional space
    )
