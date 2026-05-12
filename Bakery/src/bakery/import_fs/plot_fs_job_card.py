import numpy as np
import matplotlib.axes
from pathlib import Path
import mrcfile
import tifffile
from skimage.transform import resize
from ..image_utils import process_image_for_visualization


def load_and_average_mrc(file_path: Path) -> np.ndarray:
    """Average an MRC/MRCS stack one frame at a time via memory-mapping."""
    with mrcfile.mmap(file_path, mode='r') as mrc:
        data = mrc.data
        if data.ndim == 2:
            return data.astype(np.float32)
        accumulated = data[0].astype(np.float32)
        for i in range(1, data.shape[0]):
            accumulated += data[i]
        return accumulated / data.shape[0]


def load_and_average_tiff(file_path: Path, max_frames: int | None = None) -> np.ndarray:
    """Average a TIFF/EER stack one frame at a time.

    If max_frames is set, sample that many frames evenly spaced through the file
    (useful for EER which can have thousands of raw frames).
    """
    from tifffile import TiffFile

    with TiffFile(file_path) as tif:
        n_pages = len(tif.pages)

        if n_pages == 1:
            return tifffile.imread(file_path).astype(np.float32)

        if max_frames is not None and n_pages > max_frames:
            selected = [int(round(i * (n_pages - 1) / (max_frames - 1)))
                        for i in range(max_frames)]
        else:
            selected = range(n_pages)

        accumulated = None
        for idx in selected:
            frame = tifffile.imread(file_path, key=idx)
            if accumulated is None:
                accumulated = frame.astype(np.float32)
            else:
                accumulated += frame

    return accumulated / len(selected)


def load_averaged_image(file_path: Path) -> np.ndarray:
    """Load and average a movie file. Returns a 2D fp32 image."""
    ext = file_path.suffix.lower()

    if ext in ('.mrc', '.mrcs'):
        return load_and_average_mrc(file_path)
    elif ext == '.eer':
        return load_and_average_tiff(file_path, max_frames=100)
    elif ext in ('.tif', '.tiff'):
        return load_and_average_tiff(file_path)
    else:
        raise ValueError(f"Unsupported file format: {ext}")


def plot_fs_job_card(
    ax1: matplotlib.axes.Axes,
    ax2: matplotlib.axes.Axes,
    stack_file_1: Path,
    stack_file_2: Path
):
    """Plot frame series job card with two averaged images side by side."""
    avg1 = process_image_for_visualization(load_averaged_image(stack_file_1), target_long_side=256)
    avg2 = process_image_for_visualization(load_averaged_image(stack_file_2), target_long_side=256)

    ax1.imshow(avg1, cmap="gray", origin="lower", vmin=0, vmax=1, aspect='equal', interpolation='sinc')
    ax1.axis('off')

    ax2.imshow(avg2, cmap="gray", origin="lower", vmin=0, vmax=1, aspect='equal', interpolation='sinc')
    ax2.axis('off')