import numpy as np
from skimage.transform import resize


def fourier_crop_square_image(
    image: np.ndarray,
    target_sidelength: int
) -> np.ndarray:
    h, w = image.shape
    if h != w:
        raise ValueError('square images only')
    dc_position = w // 2
    dft = np.fft.rfft2(image)
    dft = np.fft.fftshift(dft, axes=(-2))
    hl, hu = dc_position - target_sidelength // 2, dc_position + target_sidelength // 2
    wu = target_sidelength // 2 + 1
    dft_cropped = dft[hl:hu, :wu]
    dft_cropped = np.fft.ifftshift(dft_cropped, axes=(-2))
    image_fourier_cropped = np.fft.irfft2(dft_cropped)
    return image_fourier_cropped


def square_crop(image: np.ndarray) -> tuple[np.ndarray, int, int]:
    h, w = image.shape
    sidelength = min(h, w)
    left = (w - sidelength) // 2
    right = left + sidelength
    bottom = (h - sidelength) // 2
    top = bottom + sidelength
    return image[bottom:top, left:right], bottom, left


def square_crop_rgba(image: np.ndarray) -> tuple[np.ndarray, int, int]:
    h, w, _ = image.shape
    sidelength = min(h, w)
    left = (w - sidelength) // 2
    right = left + sidelength
    bottom = (h - sidelength) // 2
    top = bottom + sidelength
    return image[bottom:top, left:right, :], bottom, left


def normalize_central_50_percent(
    image: np.ndarray
) -> np.ndarray:
    h, w = image.shape
    mh, mw = h // 2, w // 2
    hl, hu = mh - mh // 2, mh + mh // 2
    wl, wu = mw - mw // 2, mw + mw // 2
    central_crop = image[hl:hu, wl:wu]
    mean, std = np.mean(central_crop), np.std(central_crop)
    image = (image - mean) / std
    return image


def process_image_for_visualization(
    image: np.ndarray,
    target_long_side: int = 256
) -> np.ndarray:
    """
    Process image with clipping, downscaling, and normalization while preserving aspect ratio.
    
    This function follows the same processing pipeline as used in import_fs_job_card:
    1. Clip outliers using 1st and 99th percentiles
    2. Downscale to target size on long side with anti-aliasing
    3. Normalize to [0, 1] range using percentiles of downscaled image
    
    Args:
        image: Input image array
        target_long_side: Target size for the long side (default 256)
        
    Returns:
        Processed image normalized to [0, 1] range
    """
    # Clip outliers using percentiles
    p1, p99 = np.percentile(image, [1, 99])
    image_clipped = np.clip(image, p1, p99)
    
    # Downscale to target size on the long side for better contrast
    h, w = image_clipped.shape
    long_side = max(h, w)
    if long_side > target_long_side:
        scale_factor = target_long_side / long_side
        new_h = int(h * scale_factor)
        new_w = int(w * scale_factor)
        image_downscaled = resize(image_clipped, (new_h, new_w), anti_aliasing=True, preserve_range=True)
    else:
        image_downscaled = image_clipped
    
    # Calculate percentiles on downscaled image for final normalization
    p1_ds, p99_ds = np.percentile(image_downscaled, [1, 99])
    
    # Normalize to [0, 1] range
    if p99_ds > p1_ds:
        image_norm = (image_downscaled - p1_ds) / (p99_ds - p1_ds)
    else:
        image_norm = image_downscaled
    
    return image_norm