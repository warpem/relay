from pathlib import Path

import einops
import h3
import numpy as np
import starfile
from scipy.spatial.transform import Rotation as R


def rottilt_from_relion_star_file(particles_file: Path) -> np.ndarray:
    star = starfile.read(particles_file, always_dict=True)
    df = star['particles']
    rot_tilt = df[['rlnAngleRot', 'rlnAngleTilt']].to_numpy()
    rot, tilt = einops.rearrange(rot_tilt, 'b rt -> rt b')
    return rot, tilt


def coordinate_grid(sidelength: int) -> np.ndarray:
    grid = np.meshgrid(np.arange(sidelength), np.arange(sidelength), indexing='ij')
    grid = einops.rearrange([*grid], 'yx h w -> h w yx')
    grid = grid.astype(np.float32)
    grid -= sidelength / 2
    grid /= sidelength / 2
    return grid


def generate_rot_grid(sidelength: int) -> np.ndarray:
    grid = coordinate_grid(sidelength)
    yy, xx = grid[..., 0], grid[..., 1]
    rot = np.arctan2(yy, xx)
    rot = np.rad2deg(rot)
    rot[rot < 0] += 360
    rot = np.clip(rot, a_min=0, a_max=360)
    return rot


def generate_tilt_grid(sidelength: int) -> np.ndarray:
    grid = coordinate_grid(sidelength)
    grid = np.linalg.norm(grid, axis=-1)
    grid *= 90
    return grid


def get_all_h3_at_resolution(resolution: int) -> list[str]:
    """Get h3 cells (their h3 index) at a given resolution.
    Each cell appears once
    - resolution 0:       122 cells, every   ~20 degrees
    - resolution 1:       842 cells, every  ~7.5 degrees
    - resolution 2:     5,882 cells, every    ~3 degrees
    - resolution 3:    41,162 cells, every    ~1 degrees
    - resolution 4:   288,122 cells, every  ~0.4 degrees
    - resolution 5: 2,016,842 cells, every ~0.17 degrees
    c.f. https://h3geo.org/docs/core-library/restable
    """
    res0_cells = h3.get_res0_cells()
    if resolution == 0:
        h = list(res0_cells)
    else:
        h = [h3.cell_to_children(cell, resolution) for cell in res0_cells]
        h = [item for sublist in h for item in sublist]  # flatten
    return h


def latlng_to_xyz(lat: np.ndarray, lng: np.ndarray) -> np.ndarray:
    lat, lng = np.deg2rad(lat), np.deg2rad(lng)

    # Convert geographic coordinates to cartesian
    x = np.cos(lat) * np.cos(lng)
    y = np.cos(lat) * np.sin(lng)
    z = np.sin(lat)
    return einops.rearrange([x, y, z], 'xyz ... -> ... xyz')


def xyz_to_latlng(xyz: np.ndarray):
    lat = np.arcsin(xyz[..., 2])
    lng = np.arctan2(xyz[..., 1], xyz[..., 0])
    return np.rad2deg(lat), np.rad2deg(lng)


def latlng_to_cell(lat: np.ndarray, lng: np.ndarray, res: int) -> np.ndarray:
    lat, ps = einops.pack([lat], '*')
    lng, ps = einops.pack([lng], '*')
    h = [
        h3.latlng_to_cell(_lat, _lng, res=res)
        for _lat, _lng
        in zip(lat, lng)
    ]
    h = np.asarray(h)
    [h] = einops.unpack(h, packed_shapes=ps, pattern='*')
    return h


def cell_to_latlng(h: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    h, ps = einops.pack([np.asarray(h)], '*')
    h = [
        h3.cell_to_latlng(str(_h))
        for _h
        in h
    ]
    lat, lng = einops.rearrange(np.asarray(h), 'b latlng -> latlng b')
    [lat] = einops.unpack(lat, packed_shapes=ps, pattern='*')
    [lng] = einops.unpack(lng, packed_shapes=ps, pattern='*')
    return np.atleast_1d(lat), np.atleast_1d(lng)


def rottilt_to_cell(
    rot: np.ndarray, tilt: np.ndarray, res: int
) -> np.ndarray:
    rotation_matrices = rottilt_to_rotation_matrix(rot, tilt)
    z_vec_xyz = rotation_matrices[..., :, 2]
    lat, lng = xyz_to_latlng(z_vec_xyz)
    h = latlng_to_cell(lat, lng, res=res)
    return h


def cell_to_rottilt(h: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    rotation_matrices = cell_to_rotation_matrix(h)
    rotation_matrices, ps = einops.pack([rotation_matrices], '* i j')
    euler_angles = R.from_matrix(rotation_matrices).as_euler('ZY', degrees=True)
    [euler_angles] = einops.unpack(euler_angles, packed_shapes=ps, pattern='* rt')
    rot, tilt = einops.rearrange(euler_angles, '... rt -> rt ...')
    return rot, tilt


def cell_to_xyz(h: np.ndarray) -> np.ndarray:
    lat, lng = cell_to_latlng(h)
    return latlng_to_xyz(lat, lng)  # (..., xyz)


def xyz_to_cell(xyz: np.ndarray, res: int) -> np.ndarray:
    lat, lng = xyz_to_latlng(xyz)
    return latlng_to_cell(lat, lng, res=res)


def rottilt_to_rotation_matrix(
    rot: np.ndarray, tilt: np.ndarray
) -> np.ndarray:
    rot, ps = einops.pack([rot], '*')
    tilt, ps = einops.pack([tilt], '*')
    euler_angles = einops.rearrange([rot, tilt], 'rt b -> b rt')
    rotation_matrices = R.from_euler(
        seq='ZY', angles=euler_angles, degrees=True
    ).as_matrix()
    [rotation_matrices] = einops.unpack(rotation_matrices, packed_shapes=ps, pattern='* i j')
    return rotation_matrices


def cell_to_rotation_matrix(h: np.ndarray) -> np.ndarray:
    xyz = cell_to_xyz(h)
    xyz, ps = einops.pack([xyz], '* xyz')

    # Initialize rotation matrices
    b = len(xyz)
    rotation_matrices = np.zeros((b, 3, 3))

    # z-axis is the given vector on the unit sphere
    rotation_matrices[:, :, 2] = xyz

    # Create an arbitrary vector that is not aligned with z-axis
    arbitrary_vector = np.array([1, 0, 0])
    arbitrary_vector = einops.repeat(arbitrary_vector, 'xyz -> b xyz', b=b)

    # If z-axis is aligned with the arbitrary vector, use a different one
    # Check for alignment by calculating dot products
    dot_products = einops.einsum(xyz, arbitrary_vector, 'b xyz, b xyz -> b')
    idx_aligned = np.isclose(dot_products, 1.0)
    arbitrary_vector[idx_aligned] = np.array([0, 1, 0])

    # x-axis is orthogonal to z-axis
    rotation_matrices[:, :, 0] = np.cross(arbitrary_vector, xyz)
    rotation_matrices[:, :, 0] /= np.linalg.norm(rotation_matrices[:, :, 0], axis=-1, keepdims=True)

    # y-axis is orthogonal to both x-axis and z-axis
    rotation_matrices[:, :, 1] = np.cross(xyz, rotation_matrices[:, :, 0])

    [rotation_matrices] = einops.unpack(rotation_matrices, packed_shapes=ps, pattern='* i j')
    return rotation_matrices


def symmetrize(rotation_matrices: np.ndarray, symmetry: str) -> np.ndarray:
    symmetry_matrices = R.create_group(symmetry.upper()).as_matrix()
    rotation_matrices = einops.rearrange(rotation_matrices, '... i j -> ... 1 i j')
    result = symmetry_matrices @ rotation_matrices
    result = einops.rearrange(result, '... s i j -> s ... i j')
    return result
