from typing import Callable
from math import ceil

import numpy as np


def take_slice(
    volume: np.ndarray,
    axis: str = 'z',
    position: int | None = None,
    thickness: int = 1,
    reduction_func: Callable = np.mean,
) -> np.ndarray:
    axis_name_to_idx = {
        'z': -3,
        'y': -2,
        'x': -1,
    }
    axis = axis_name_to_idx[axis.lower()]
    center = position if isinstance(position, int) else volume.shape[axis] // 2
    lower_bound = ceil(center - thickness / 2)
    upper_bound = ceil(center + thickness / 2)
    idx = list(range(lower_bound, upper_bound))
    thick_slice = np.take(volume, indices=idx, axis=axis)
    return reduction_func(thick_slice, axis=axis)
