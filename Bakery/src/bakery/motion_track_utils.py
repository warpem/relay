import json
from pathlib import Path

import einops
import numpy as np
from scipy.interpolate import RegularGridInterpolator


def parse_motion_grid_from_json(motion_tracks_json: Path) -> np.ndarray:
    # parse JSON
    with open(motion_tracks_json, 'r') as file:
        data = json.load(file)

    # data is stored as a 2D grid of motions in x and y
    # data[cell] = {'x': dx, 'y': dy} where cell is f"{idx_x}_{idx_y}"

    # first figure out grid dimensions
    max_idx_x, max_idx_y = 0, 0
    for key in data.keys():
        x, y = key.split("_")
        if int(x) > max_idx_x:
            max_idx_x = int(x)
        if int(y) > max_idx_y:
            max_idx_y = int(y)

    t, h, w = len(data[key]['x']), max_idx_y + 1, max_idx_x + 1

    # then put data into a numpy array
    motion_grid_data = np.zeros((t, h, w, 2))  # (t, h, w, yx)
    for k, v in data.items():
        x, y = k.split("_")
        x, y = int(x), int(y)
        motion_grid_data[:, y, x, -2] = np.array(data[k]['y'])
        motion_grid_data[:, y, x, -1] = np.array(data[k]['x'])
    return motion_grid_data


def expand_motion_grid(motion_grid_data: np.ndarray) -> np.ndarray:
    """Repeat data to enable cubic interp with <4 points."""
    t, h, w, _ = motion_grid_data.shape

    # cubic interpolation needs at least four points per dimension
    if t < 4:
        motion_grid_data = einops.repeat(motion_grid_data, 't h w yx -> (4 t) h w yx')
    if h < 4:
        motion_grid_data = einops.repeat(motion_grid_data, 't h w yx -> t (4 h) w yx')
    if w < 4:
        motion_grid_data = einops.repeat(motion_grid_data, 't h w yx -> t h (4 w) yx')
    return motion_grid_data


def setup_grid_interpolators(
    motion_grid_data: np.ndarray
) -> tuple[RegularGridInterpolator, RegularGridInterpolator]:
    t, h, w, _ = motion_grid_data.shape
    _t, _y, _x = (
        np.linspace(0, 1, num=t),
        np.linspace(0, 1, num=h),
        np.linspace(0, 1, num=w)
    )
    interpolator_dy = RegularGridInterpolator(
        points=(_t, _y, _x), values=motion_grid_data[..., -2], method="cubic"
    )
    interpolator_dx = RegularGridInterpolator(
        points=(_t, _y, _x), values=motion_grid_data[..., -1], method="cubic"
    )
    return interpolator_dy, interpolator_dx


def evaluate_motion_tracks(
    motion_grid_data: np.ndarray,
    position: np.ndarray,  # (..., 2), yx order, [0, 1] across grid
    n_samples: int
) -> np.ndarray:  # (..., n_samples, 2) yx shifts in angstroms
    interpolator_dy, interpolator_dx = setup_grid_interpolators(motion_grid_data)
    position, ps = einops.pack([position], '* yx')
    b, _ = position.shape
    position_tyx = np.zeros((b, n_samples, 3))
    position_tyx[:, :, 1:] = einops.rearrange(position, 'b yx -> b 1 yx')
    position_tyx[:, :, 0] = einops.rearrange(np.linspace(0, 1, n_samples), 'n -> 1 n')
    position_tyx = einops.rearrange(position_tyx, 'b n tyx -> (b n) tyx')
    dy, dx = interpolator_dy(position_tyx), interpolator_dx(position_tyx)
    tracks = einops.rearrange([dy, dx], 'yx (b n) -> b n yx', b=b, n=n_samples)
    [tracks] = einops.unpack(tracks, packed_shapes=ps, pattern='* n yx')
    return tracks
