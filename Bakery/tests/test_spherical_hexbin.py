import tempfile
import unittest
from pathlib import Path

import numpy as np
import pandas as pd
import starfile
from typer.testing import CliRunner

from bakery.cli import cli
from bakery.spherical_hexbin.utils import xyz_to_cell, xyz_to_latlng


class SphericalHexbinTests(unittest.TestCase):
    def test_float32_polar_directions_remain_finite_and_scale_invariant(self):
        # Actual direction that stopped visualization of Beam refinement 612.
        direction = np.array([-4.2770058e-4, 3.5781413e-4, -1.0000001], dtype=np.float32)
        directions = np.stack([direction, -direction, [0., 0., 1.0000001], [0., 0., -1.0000001]])
        with np.errstate(invalid='raise'):
            lat, lng = xyz_to_latlng(directions)
            cells = xyz_to_cell(directions, res=1)
        self.assertTrue(np.isfinite(lat).all())
        self.assertTrue(np.isfinite(lng).all())
        self.assertTrue((np.abs(lat) <= 90).all())
        np.testing.assert_array_equal(cells, xyz_to_cell(directions * 2, res=1))
        np.testing.assert_allclose(lat[2:], [90., -90.])
        expected = directions / np.linalg.norm(directions, axis=-1, keepdims=True)
        np.testing.assert_allclose(lat, np.rad2deg(np.arcsin(expected[:, 2])), atol=1e-8)

    def test_tilt_sampling_cli_renders_float32_polar_directions(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            particles = root / 'particles.star'
            starfile.write({'particles': pd.DataFrame({
                'rlnAngleRot': [0.], 'rlnAngleTilt': [0.], 'rlnClassNumber': [1],
            })}, particles)
            directions = root / 'directions.npy'
            np.save(directions, np.array([
                [-4.2770058e-4, 3.5781413e-4, -1.0000001],
                [0., 0., 1.0000001],
            ], dtype=np.float32))
            # Both single-map and per-class Beam plots use this command.
            for extra in ([], ['--class-number', '1', '--symmetry', 'C2']):
                result = CliRunner().invoke(cli, [
                    'orientation-and-fourier-sampling', '--particles-file', str(particles),
                    '--input-type', 'relion_star_file', '--grid-resolution', '0',
                    '--colormap', 'viridis', '--projection-directions-file', str(directions),
                    '--output-orientation-file', str(root / 'angles.png'),
                    '--output-fourier-sampling-file', str(root / 'sampling.png'), '--svg',
                    *extra,
                ])
                self.assertEqual(result.exit_code, 0, result.output + str(result.exception))
                for name in ('angles', 'sampling'):
                    for suffix in ('.png', '.svg', '.light.svg', '.dark.svg'):
                        self.assertTrue((root / (name + suffix)).is_file())
