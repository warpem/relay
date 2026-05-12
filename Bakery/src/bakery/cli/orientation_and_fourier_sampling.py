from enum import Enum
from pathlib import Path
from typing import Optional

import einops
import matplotlib.pyplot as plt
import numpy as np
import typer
from cmap import Colormap

from ._cli import cli
from ..spherical_hexbin.utils import (
    generate_rot_grid,
    generate_tilt_grid,
    rottilt_to_cell,
    cell_to_rotation_matrix,
    xyz_to_cell, rottilt_to_rotation_matrix, symmetrize, rottilt_from_relion_star_file
)


class InputType(Enum):
    RELION_STAR_FILE = 'relion_star_file'


@cli.command(no_args_is_help=True, help="hexbin plots of particle orientations and Fourier sampling distributions")
def orientation_and_fourier_sampling(
    particles_file: Path = typer.Option(...),
    grid_resolution: int = typer.Option(...),
    symmetry: Optional[str] = typer.Option(None),
    colormap: str = typer.Option(...),
    input_type: InputType = typer.Option(...),
    output_orientation_file: Optional[Path] = typer.Option(None),
    output_fourier_sampling_file: Optional[Path] = typer.Option(None),
):
    """Make hexbin plots of orientation and Fourier sampling distributions.

    Grid resolution is the h3 grid resolution.
    - resolution 0:       122 cells, every   ~20 degrees
    - resolution 1:       842 cells, every  ~7.5 degrees
    - resolution 2:     5,882 cells, every    ~3 degrees
    - resolution 3:    41,162 cells, every    ~1 degrees
    - resolution 4:   288,122 cells, every  ~0.4 degrees
    - resolution 5: 2,016,842 cells, every ~0.17 degrees
    c.f. https://h3geo.org/docs/core-library/restable

    Colormap strings can be any valid name from the cmap catalog.
    - catalog: https://cmap-docs.readthedocs.io/en/stable/catalog/


    Symmetry strings are supplied to scipy.spatial.transform.Rotation.create_group()
    - Must be one of ‘I’, ‘O’, ‘T’, ‘Dn’, ‘Cn’, where n is a positive integer
    c.f. https://docs.scipy.org/doc/scipy/reference/generated/scipy.spatial.transform.Rotation.create_group.html
    """
    # find h3 indices for particle z vectors (and their symmetry mates)
    rot, tilt = parse_input_file(particles_file, input_type)
    rot, tilt = normalize_orientations(rot, tilt)
    rotation_matrices = rottilt_to_rotation_matrix(rot, tilt)
    if symmetry is not None:
        rotation_matrices = symmetrize(rotation_matrices, symmetry=symmetry)
    z_vectors = rotation_matrices[..., :, 2]
    h = xyz_to_cell(z_vectors, res=grid_resolution)
    h_to_count = count_particles_per_h3_cell(h)

    # find mapping between h3 indices and pixels in our output image
    rot_grid, tilt_grid = generate_rot_grid(1024), generate_tilt_grid(1024)
    h_grid = rottilt_to_cell(rot_grid, tilt_grid, res=grid_resolution)
    h_cells, h_cell_counts, idx_inv = find_cell_count_to_image_mapping(h_grid, h_to_count)

    # write outputs
    if output_orientation_file is not None:
        image = generate_orientation_distribution_image(
            h_counts=h_cell_counts,
            idx_inv=idx_inv,
            tilt_grid=tilt_grid
        )
        plot_orientation_distribution(
            image=image,
            colormap=colormap,
            output_file=output_orientation_file
        )

    if output_fourier_sampling_file is not None:
        image = generate_fourier_sampling_distribution_image(
            h_cells=h_cells,
            h_cell_counts=h_cell_counts,
            idx_inv=idx_inv,
            tilt_grid=tilt_grid,
            grid_resolution=grid_resolution
        )
        plot_fourier_sampling_distribution(
            image=image,
            colormap=colormap,
            output_file=output_fourier_sampling_file
        )


def parse_input_file(particles_file: Path, input_type: InputType):
    if input_type == InputType.RELION_STAR_FILE:
        return rottilt_from_relion_star_file(particles_file)
    else:
        raise NotImplementedError(input_type)


def normalize_orientations(
    rot: np.ndarray, tilt: np.ndarray
) -> tuple[np.ndarray, np.ndarray]:
    idx_tilt_gt90 = tilt > 90
    rot[idx_tilt_gt90] += 180
    tilt[idx_tilt_gt90] = 180 - tilt[idx_tilt_gt90]
    rot[rot > 360] -= 360
    return rot, tilt


def count_particles_per_h3_cell(h: np.ndarray) -> dict[str, int]:
    h_unique, counts = np.unique(h, return_counts=True)
    return {h: count for h, count in zip(h_unique, counts)}


def find_cell_count_to_image_mapping(
    h_grid: np.ndarray, h_to_count: dict[str, int]
) -> tuple[np.ndarray, np.ndarray, np.ndarray]:
    h, idx_inv = np.unique(h_grid, return_inverse=True)
    h_counts = [
        h_to_count.get(_h, 0)
        for _h
        in h
    ]
    h_counts = np.array(h_counts)
    return h, h_counts, idx_inv


def generate_orientation_distribution_image(h_counts, idx_inv, tilt_grid):
    image = h_counts[idx_inv]
    image = np.ma.masked_where(condition=tilt_grid > 90, a=image)
    return image


def plot_orientation_distribution(
    image: np.ndarray,
    colormap: str,
    output_file: Path
):
    fig, ax = plt.subplots(figsize=(3, 3))
    cmap = Colormap(colormap).to_mpl()
    cmap.set_bad(color='white')
    ax.imshow(image, cmap=cmap, interpolation='sinc', origin='lower', vmin=0)
    ax.set(xticks=[], xticklabels=[], yticks=[], yticklabels=[])
    for spine in ax.spines.values():
        spine.set_visible(False)

    polar_ax = fig.add_subplot(111, projection='polar')
    polar_ax.set_zorder(1)
    polar_ax.set_rmax(90)
    polar_ax.set_rticks([30, 60, 90])
    polar_ax.set_yticklabels(['30°', '60°', '90°'])
    polar_ax.set_rlabel_position(4)
    polar_ax.grid(alpha=0.5)
    polar_ax.tick_params(axis='both', which='major', labelsize=8)
    polar_ax.patch.set_alpha(0)

    plt.tight_layout(pad=0.1)
    fig.savefig(output_file, dpi=512)


def generate_fourier_sampling_distribution_image(
    h_cells: np.ndarray,
    h_cell_counts: np.ndarray,
    idx_inv: np.ndarray,
    tilt_grid: np.ndarray,
    grid_resolution: int,
) -> np.ndarray:
    idx = np.nonzero(h_cell_counts)
    nonzero_h_cells = h_cells[idx]
    nonzero_h_cell_counts = h_cell_counts[idx]

    theta = np.linspace(start=0, stop=2 * np.pi, num=500, endpoint=False)
    x = np.cos(theta)
    y = np.sin(theta)
    z = np.zeros_like(theta)
    xyz = einops.rearrange([x, y, z], 'xyz b -> b xyz')

    h_cell_counts_fourier_sampling = np.zeros_like(h_cell_counts)

    for h, count in zip(nonzero_h_cells, nonzero_h_cell_counts):
        rotation_matrix = cell_to_rotation_matrix(h)
        rotated_xyz = rotation_matrix @ einops.rearrange(xyz, 'b xyz -> b xyz 1')
        rotated_xyz = einops.rearrange(rotated_xyz, 'b xyz 1 -> b xyz')
        h_rotated = xyz_to_cell(rotated_xyz, res=grid_resolution)
        unique_h_rotated = np.unique(h_rotated)
        idx_in_hemisphere = np.where(np.isin(h_cells, unique_h_rotated))
        h_cell_counts_fourier_sampling[idx_in_hemisphere] += count

    image = h_cell_counts_fourier_sampling[idx_inv]
    image = np.ma.masked_where(condition=tilt_grid > 90, a=image)
    return image


def plot_fourier_sampling_distribution(image: np.ndarray, colormap, output_file):
    fig, ax = plt.subplots(figsize=(3, 3))
    cmap = Colormap(colormap).to_mpl()
    cmap.set_bad(color='white')
    ax.imshow(image, cmap=cmap, interpolation='sinc', origin='lower', vmin=0)
    ax.set(xticks=[], xticklabels=[], yticks=[], yticklabels=[])
    for spine in ax.spines.values():
        spine.set_visible(False)

    polar_ax = fig.add_subplot(111, projection='polar')
    polar_ax.set_zorder(1)
    polar_ax.set_rmax(90)
    polar_ax.set_rticks([30, 60, 90])
    polar_ax.set_yticklabels(['30°', '60°', '90°'])
    polar_ax.set_rlabel_position(4)
    polar_ax.grid(alpha=0.5)
    polar_ax.tick_params(axis='both', which='major', labelsize=8)
    polar_ax.patch.set_alpha(0)

    plt.tight_layout(pad=0.1)
    fig.savefig(output_file, dpi=512)
