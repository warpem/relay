"""Consistent single-view previews of reconstructed 3D maps."""

from pathlib import Path

import mrcfile
import numpy as np
from matplotlib.axes import Axes


def project_z(volume: np.ndarray) -> np.ndarray:
    """Integrate the full z depth, accumulating mode-12 maps in float32."""
    return np.sum(volume, axis=0, dtype=np.float32)


def read_z_projection(path: Path) -> np.ndarray:
    with mrcfile.mmap(path) as volume:
        return project_z(volume.data)


def draw_map_preview(ax: Axes, image: np.ndarray):
    """Use Beam refinement's per-map min/max contrast and zoom to central 2/3.

    Keep the full projection for intensity normalization; zoom using axes limits
    so cropping does not change contrast or the coordinates of overlays.
    """
    artist = ax.imshow(
        image, cmap='gray', origin='lower', interpolation='sinc',
        interpolation_stage='data', vmin=float(np.min(image)),
        vmax=float(np.max(image)),
    )
    left, right, bottom, top = artist.get_extent()
    ax.set_xlim(left + (right - left) / 6, right - (right - left) / 6)
    ax.set_ylim(bottom + (top - bottom) / 6, top - (top - bottom) / 6)
    ax.axis('off')
    return artist
