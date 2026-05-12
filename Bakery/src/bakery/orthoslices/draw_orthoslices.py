import numpy as np
from .slice import take_slice
import matplotlib.pyplot


def draw_central_orthoslice(
    ax: matplotlib.pyplot.Axes,
    volume: np.ndarray,
    axis_name: str = 'z',
    position: int | None = None,
    thickness: int = 1
) -> matplotlib.pyplot.Axes:
    orthoslice = take_slice(volume, axis=axis_name, position=position, thickness=thickness)
    ax.imshow(orthoslice, cmap='gray', interpolation='sinc', origin='lower')
    ax.set(xticks=[], xticklabels=[], yticks=[], yticklabels=[])
    return ax
