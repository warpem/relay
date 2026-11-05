"""Saved class2d displays do not require Beam or PyTorch on the Relay server."""
import json
import tempfile
import unittest
from pathlib import Path

import mrcfile
import numpy as np
import pandas as pd
import starfile
from typer.testing import CliRunner

from bakery.cli import cli


class BeamClass2DFigureTests(unittest.TestCase):
    def test_compiles_dynamic_classes_signed_components_and_discovery_sample(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            snapshot = root / 'snapshot'; snapshot.mkdir()
            output = root / 'relay'
            y, x = np.mgrid[-1:1:16j, -1:1:16j]
            mean = np.exp(-4 * (x*x + y*y))
            component = x * mean
            mrcfile.write(snapshot / 'classes.mrcs', np.stack([mean, mean*.5]).astype('float32'))
            mrcfile.write(snapshot / 'principal_components.mrcs', np.stack([mean, component]).astype('float32'))
            starfile.write({'model_classes': pd.DataFrame({'rlnEstimatedResolution': [8., 10.],
                                                         'rlnClassDistribution': [.75, .25]})}, snapshot / 'model.star')
            np.savez(snapshot / 'embeddings.npz', coordinates=np.array([[1., 2.], [-1., -2.]]),
                     discovery_classes=np.array([1, 2]))
            result = CliRunner().invoke(cli, ['beam-class2d-figures', '--snapshot-directory', str(snapshot),
                                             '--output-directory', str(output), '--iteration', '2'])
            self.assertEqual(result.exit_code, 0, result.output + str(result.exception))
            stats = json.loads((output / 'stats_it0002.json').read_text())
            self.assertEqual([s['Id'] for s in stats], [1, 2])
            self.assertEqual([s['Distribution'] for s in stats], [.75, .25])
            self.assertEqual(json.loads((output / 'pca_info_it0002.json').read_text()),
                             {'PrincipalComponentCount': 1, 'EmbeddingParticleCount': 2})
            self.assertTrue((output / 'embedding_it0002.light.svg').is_file())
            self.assertFalse((output / 'pca_it0002.png').exists())
            self.assertFalse((output / 'classes_it0002.png').exists())

    def test_snapshot_without_pca_or_occupancies_still_renders_classes(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            mrcfile.write(root / 'classes.mrcs', np.ones((1, 8, 8), dtype='float32'))
            starfile.write({'model_classes': pd.DataFrame({'rlnEstimatedResolution': [8.]})}, root / 'model.star')
            result = CliRunner().invoke(cli, ['beam-class2d-figures', '--snapshot-directory', str(root),
                                             '--output-directory', str(root / 'relay'), '--iteration', '1'])
            self.assertEqual(result.exit_code, 0, result.output + str(result.exception))
            stats = json.loads((root / 'relay/stats_it0001.json').read_text())
            self.assertIsNone(stats[0]['Distribution'])
            self.assertFalse((root / 'relay/classes_it0001.png').exists())
