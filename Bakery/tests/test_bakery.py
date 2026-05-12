import subprocess
from pathlib import Path

import mrcfile
import numpy as np


def test_orthoslices(tmpdir):
    volume_file = Path(tmpdir) / 'volume.mrc'
    image_file = Path(tmpdir) / 'image.png'
    arr = np.random.uniform(size=(32, 32, 32)).astype(np.float16)
    mrcfile.write(volume_file, arr, voxel_size=1)
    completed_process = subprocess.run(
        [
            'bakery',
            'orthoslices',
            '--layout', 'horizontal',
            '--slice-thickness-angstroms', '10',
            '--volume-file', f'{volume_file}',
            '--output-file', f'{image_file}',
        ]
    )
    assert completed_process.returncode == 0
    assert image_file.exists()


def test_xy_slice(tmpdir):
    volume_file = Path(tmpdir) / 'volume.mrc'
    image_file = Path(tmpdir) / 'image.png'
    arr = np.random.uniform(size=(32, 32, 32)).astype(np.float16)
    mrcfile.write(volume_file, arr, voxel_size=1)
    completed_process = subprocess.run(
        [
            'bakery',
            'xy-slice',
            '--slice-thickness-angstroms', '10',
            '--volume-file', f'{volume_file}',
            '--output-file', f'{image_file}',
        ]
    )
    assert completed_process.returncode == 0
    assert image_file.exists()
