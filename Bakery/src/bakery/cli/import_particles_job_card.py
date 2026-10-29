from pathlib import Path
from typing import List

import matplotlib
import numpy as np
import pandas as pd
import starfile
import typer
from matplotlib import pyplot as plt

from ..particle_images import read_particle_images
from ._cli import cli


@cli.command(no_args_is_help=True)
def import_particles_job_card(
    particle_star_files: list[Path] = typer.Option(..., "--particle-star-file", help="supply this option multiple times to pass multiple files"),
    output_file: Path = typer.Option(...)
):
    nrows, ncols = 3, 9

    # read rlnImageName from all files and concatenate
    image_name_columns = [
        starfile.read(star, always_dict=True)["particles"]["rlnImageName"]
        for star
        in particle_star_files
    ]
    image_names = pd.concat(image_name_columns)

    # take random subset
    n_particles_in_star_file = len(image_names)
    n_particle_images = min(nrows * ncols, n_particles_in_star_file)
    rng = np.random.default_rng(seed=44)
    subset_idx = rng.choice(n_particles_in_star_file, size=n_particle_images)
    image_names = image_names.iloc[subset_idx]

    particle_image_subset = np.stack(read_particle_images(image_names)).astype(np.float64)

    # normalize
    idx_nonzero = np.abs(particle_image_subset) > 1e-8
    n_nonzero = np.sum(idx_nonzero)
    normalized_l2_norm = np.linalg.norm(particle_image_subset[idx_nonzero]) / np.sqrt(n_nonzero)
    particle_image_subset = particle_image_subset / normalized_l2_norm

    # setup plot
    n_rows, n_cols = 3, 9
    fig, axs = plt.subplots(figsize=(3, 1), nrows=n_rows, ncols=n_cols)

    for i in range(n_rows):
        for j in range(n_cols):
            idx = (i * n_cols) + j
            if idx < n_particle_images:
                draw_image_panel(
                    ax=axs[i, j],
                    image=particle_image_subset[idx],
                )
            axs[i, j].axis("off")

    plt.tight_layout(pad=0.15)
    fig.savefig(output_file, dpi=2 * 144, transparent=True)
    fig.savefig(str(output_file.with_suffix('.pdf')), dpi=2 * 144, transparent=True)


def draw_image_panel(
    ax: matplotlib.axes.Axes,
    image: np.ndarray,
):
    # draw image, image==0 at 25% gray
    # 12/18/24 having weird issues with normalisation/colormapping here so am
    # letting default mpl colormapping take care of this
    std_scale = 2
    ax.imshow(
        image,
        cmap="gray",
        origin="lower",
        interpolation='sinc',
        interpolation_stage='data',
        # vmin=-0.25 * std_scale,
        # vmax=0.75 * std_scale,
    )
