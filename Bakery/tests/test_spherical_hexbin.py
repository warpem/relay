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

    def test_cached_fourier_operator_matches_original_circle_membership_exactly(self):
        from bakery.cli.orientation_and_fourier_sampling import projection_grid, generate_fourier_sampling_distribution_image
        from bakery.spherical_hexbin.utils import cell_to_rotation_matrix
        cells, inverse, tilt = projection_grid(0)
        counts = np.zeros(len(cells), dtype=np.int64)
        counts[::7] = np.arange(len(counts[::7])) + 1
        theta = np.linspace(0, 2*np.pi, 500, endpoint=False)
        circle = np.stack([np.cos(theta), np.sin(theta), np.zeros_like(theta)], axis=-1)
        expected = np.zeros_like(counts)
        for cell, count in zip(cells, counts):
            if count:
                rotation = cell_to_rotation_matrix(cell)
                visited = np.unique(xyz_to_cell(circle @ rotation[0].T, res=0))
                expected[np.isin(cells, visited)] += count
        actual = generate_fourier_sampling_distribution_image(cells, counts, inverse, tilt, 0)
        np.testing.assert_array_equal(actual.data, expected[inverse])
        np.testing.assert_array_equal(actual.mask, tilt > 90)
        empty = generate_fourier_sampling_distribution_image(cells, counts * 0, inverse, tilt, 0)
        self.assertFalse(empty.data.any())

    def test_batch_shares_star_and_grid_and_resolves_heterogeneous_symmetry(self):
        import importlib
        import json
        from unittest.mock import patch
        module = importlib.import_module('bakery.cli.orientation_and_fourier_sampling')
        with tempfile.TemporaryDirectory(prefix='figure batch ') as directory:
            root = Path(directory)
            particles = root / 'particles with spaces.star'
            starfile.write({'particles': pd.DataFrame({'rlnAngleRot': [0., 90.],
                'rlnAngleTilt': [20., 60.], 'rlnClassNumber': [1, 2]})}, particles)
            module._particle_angles.cache_clear()
            module.projection_grid.cache_clear()
            commands = []
            for number in (1, 2, 3):  # Class 3 is empty.
                args = ['orientation-and-fourier-sampling', '--particles-file', str(particles),
                    '--input-type', 'relion_star_file', '--grid-resolution', '0', '--colormap', 'viridis',
                    '--symmetry', 'C1,C2,C1', '--class-number', str(number),
                    '--output-orientation-file', str(root / f'angles{number}.png'),
                    '--output-fourier-sampling-file', str(root / f'sampling{number}.png'), '--svg']
                import shlex
                commands.append(dict(ArgumentList=args if number != 3 else [],
                                     Arguments='' if number != 3 else shlex.join(args), WorkingDirectory=''))
            manifest = root / 'batch.json'
            manifest.write_text(json.dumps(commands))
            with patch.object(module.starfile, 'read', wraps=module.starfile.read) as read, \
                 patch.object(module, 'symmetrize', wraps=module.symmetrize) as symmetry:
                result = CliRunner().invoke(cli, ['batch', '--manifest', str(manifest)])
                self.assertEqual(result.exit_code, 0, result.output + str(result.exception))
                self.assertEqual(read.call_count, 1)
                self.assertEqual(symmetry.call_count, 1)
                self.assertEqual(symmetry.call_args.kwargs['symmetry'], 'C2')
            self.assertEqual(module.projection_grid.cache_info().misses, 1)
            for number in (1, 2, 3):
                for name in ('angles', 'sampling'):
                    for suffix in ('.png', '.svg', '.light.svg', '.dark.svg'):
                        self.assertTrue((root / f'{name}{number}{suffix}').exists())

    def test_mixed_map_fsc_and_card_batch_writes_exports_and_closes_figures(self):
        import json
        import mrcfile
        import matplotlib.pyplot as plt
        with tempfile.TemporaryDirectory(prefix='map batch ') as directory:
            root = Path(directory)
            volume = root / 'map.mrc'
            mrcfile.write(volume, np.random.default_rng(7).normal(size=(24,24,24)).astype(np.float32), voxel_size=2.)
            model = root / 'model.star'
            starfile.write({'model_class_1': pd.DataFrame({
                'rlnAngstromResolution': [20.,10.,5.], 'rlnGoldStandardFsc': [.9,.5,.1]})}, model)
            commands = [
                ['map-orthoslice-atlas', '--volume-file', str(volume), '--output-file', str(root/'slices.png'), '--svg'],
                ['fsc', '--metadata-file', str(model), '--input-type', 'relion_model_star', '--output-file', str(root/'fsc.png'), '--svg'],
                ['class3d-job-card', '--volume-file', str(volume), '--class-number', '1', '--output-file', str(root/'card.png')],
            ]
            manifest = root/'batch.json'
            manifest.write_text(json.dumps([dict(ArgumentList=args, Arguments='', WorkingDirectory='') for args in commands]))
            result = CliRunner().invoke(cli, ['batch', '--manifest', str(manifest)])
            self.assertEqual(result.exit_code, 0, result.output + str(result.exception))
            for name in ('slices','fsc'):
                for suffix in ('.png','.svg','.light.svg','.dark.svg'):
                    self.assertTrue((root/(name+suffix)).is_file())
            self.assertTrue((root/'card.png').is_file())
            self.assertTrue((root/'card.pdf').is_file())
            self.assertFalse(plt.get_fignums())
