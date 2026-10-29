"""Read selected particle planes from single-image MRCs or MRC stacks."""

from collections import defaultdict
from collections.abc import Sequence

import mrcfile
import numpy as np


def read_particle_images(image_names: Sequence[str]) -> list[np.ndarray]:
    requests = defaultdict(list)
    for output_index, name in enumerate(image_names):
        index, path = name.split("@", 1)
        requests[path].append((output_index, int(index) - 1))

    images = [None] * len(image_names)
    for path, selections in requests.items():
        with mrcfile.mmap(path, mode="r") as stream:
            data = stream.data
            # mrcfile exposes nz=1 as (height,width), not (1,height,width).
            # Always retain a particle axis before applying STAR's image index.
            if data.ndim == 2:
                data = data[np.newaxis, ...]
            if data.ndim != 3:
                raise ValueError(f"{path}: expected a 2D image or a stack of 2D images")
            for output_index, index in selections:
                if not 0 <= index < len(data):
                    raise ValueError(f"{path}: particle index {index + 1} exceeds stack bounds")
                # Own the selected planes so the mapping can be closed here.
                images[output_index] = np.array(data[index], copy=True)
    return images
