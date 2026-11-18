from enum import Enum
from functools import lru_cache
from pathlib import Path
from typing import Optional

import einops
import matplotlib.pyplot as plt
import numpy as np
import starfile
import typer
from cmap import Colormap
from scipy.spatial.transform import Rotation
from scipy.sparse import csr_matrix

from ._cli import cli
from ..figure_export import save_figure
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
    svg: bool = False,
    projection_directions_file: Optional[Path] = typer.Option(None),
    class_number: Optional[int] = None,
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
    symmetry = class_symmetry(symmetry, class_number)
    particles = particle_angles(particles_file)
    selected = particles if class_number is None else particles.loc[particles['rlnClassNumber'] == class_number]
    rot = selected['rlnAngleRot'].to_numpy(copy=True)
    tilt = selected['rlnAngleTilt'].to_numpy(copy=True)
    rot, tilt = normalize_orientations(rot, tilt)
    rotation_matrices = rottilt_to_rotation_matrix(rot, tilt) if len(rot) else np.empty((0, 3, 3))
    if symmetry is not None:
        rotation_matrices = symmetrize(rotation_matrices, symmetry=symmetry)
    z_vectors = rotation_matrices[..., :, 2]
    h_to_count = count_particles_per_h3_cell(xyz_to_cell(z_vectors, res=grid_resolution))

    # Grid geometry is independent of particles, classes and iterations.
    h_cells, idx_inv, tilt_grid = projection_grid(grid_resolution)
    h_cell_counts = np.array([h_to_count.get(h, 0) for h in h_cells])

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
            output_file=output_orientation_file,
            svg=svg,
        )

    if output_fourier_sampling_file is not None:
        if projection_directions_file is not None:
            directions = np.load(projection_directions_file, allow_pickle=False)
            if directions.ndim != 2 or directions.shape[1] != 3 or not np.isfinite(directions).all():
                raise ValueError('Projection directions must be a finite N×3 array')
            if not np.allclose(np.linalg.norm(directions, axis=1), 1., atol=1e-3):
                raise ValueError('Projection directions must be unit vectors')
            if symmetry is not None:
                directions = np.einsum('sij,nj->sni', Rotation.create_group(symmetry).as_matrix(),
                                       directions).reshape(-1, 3)
            directions = np.where(directions[:, 2:3] < 0, -directions, directions)
            h_to_count = count_particles_per_h3_cell(xyz_to_cell(directions, res=grid_resolution))
            h_cell_counts = np.array([h_to_count.get(h, 0) for h in h_cells])
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
            output_file=output_fourier_sampling_file,
            svg=svg,
        )


def class_symmetry(symmetry, class_number=None):
    if not symmetry:
        return None
    groups = [group.strip().upper() for group in symmetry.split(',')]
    if len(groups) > 1:
        if class_number is None or not 1 <= class_number <= len(groups):
            raise ValueError('Per-class symmetry requires a matching class number')
        symmetry = groups[class_number - 1]
    else:
        symmetry = groups[0]
    return None if symmetry == 'C1' else symmetry


@lru_cache(maxsize=2)
def _particle_angles(path, mtime, size):
    # Keep only plotting columns after one STAR parse, shared by every class.
    table = starfile.read(path, always_dict=True)['particles']
    return table[[c for c in ('rlnAngleRot', 'rlnAngleTilt', 'rlnClassNumber') if c in table]]


def particle_angles(path):
    path = Path(path).resolve()
    stat = path.stat()
    return _particle_angles(str(path), stat.st_mtime_ns, stat.st_size)


@lru_cache(maxsize=4)
def projection_grid(resolution):
    rot, tilt = generate_rot_grid(1024), generate_tilt_grid(1024)
    cells, inverse = np.unique(rottilt_to_cell(rot, tilt, res=resolution), return_inverse=True)
    return cells, inverse, tilt


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
    output_file: Path,
    svg: bool = False,
):
    fig, ax = plt.subplots(figsize=(3, 3))
    cmap = Colormap(colormap).to_mpl()
    cmap.set_bad(color=(0, 0, 0, 0))
    ax.imshow(image, cmap=cmap, interpolation='nearest', origin='lower', vmin=0)
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
    polar_ax.tick_params(axis='both', which='major', labelsize=10)
    polar_ax.patch.set_alpha(0)

    plt.tight_layout(pad=0.1)
    save_figure(fig, output_file, svg=svg, dpi=512)
    plt.close(fig)


def generate_fourier_sampling_distribution_image(
    h_cells: np.ndarray,
    h_cell_counts: np.ndarray,
    idx_inv: np.ndarray,
    tilt_grid: np.ndarray,
    grid_resolution: int,
) -> np.ndarray:
    transfer = fourier_transfer(tuple(h_cells), grid_resolution)
    counts = transfer @ h_cell_counts
    return np.ma.masked_where(tilt_grid > 90, counts[idx_inv])


@lru_cache(maxsize=4)
def fourier_transfer(cells, resolution):
    """Exact old 500-point great-circle binning, computed once as a sparse operator.

    Each circle contributes once per visited cell, not once per sampled point.
    Dictionary lookups replace repeatedly scanning the entire image-cell array.
    """
    cells = np.asarray(cells)
    lookup = {cell: i for i, cell in enumerate(cells)}
    rotations = cell_to_rotation_matrix(cells)
    theta = np.linspace(0, 2 * np.pi, 500, endpoint=False)
    circle = np.stack([np.cos(theta), np.sin(theta), np.zeros_like(theta)], axis=-1)
    rows, columns = [], []
    for source, rotation in enumerate(rotations):
        visited = np.unique(xyz_to_cell(circle @ rotation.T, res=resolution))
        targets = [lookup[cell] for cell in visited if cell in lookup]
        rows.extend(targets)
        columns.extend([source] * len(targets))
    return csr_matrix((np.ones(len(rows), dtype=np.int8), (rows, columns)), shape=(len(cells), len(cells)))


def plot_fourier_sampling_distribution(image: np.ndarray, colormap, output_file, svg: bool = False):
    fig, ax = plt.subplots(figsize=(3, 3))
    cmap = Colormap(colormap).to_mpl()
    cmap.set_bad(color=(0, 0, 0, 0))
    ax.imshow(image, cmap=cmap, interpolation='nearest', origin='lower', vmin=0)
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
    polar_ax.tick_params(axis='both', which='major', labelsize=10)
    polar_ax.patch.set_alpha(0)

    plt.tight_layout(pad=0.1)
    save_figure(fig, output_file, svg=svg, dpi=512)
    plt.close(fig)
