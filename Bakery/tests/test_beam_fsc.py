"""Beam and RELION model tables share plotting without changing FSC meanings."""
import tempfile
import unittest
from pathlib import Path

import matplotlib.pyplot as plt
import mrcfile
import numpy as np
import pandas as pd
import starfile
from typer.testing import CliRunner

from bakery.cli import cli
from bakery.fsc.fsc_data_model import FscData


class BeamFscTests(unittest.TestCase):
    def frame(self):
        return pd.DataFrame({
            'rlnAngstromResolution': [np.inf, 20, 10, 5],
            'rlnFourierShellCorrelationUnmaskedMaps': [1, 0.6, 0.1, 0],
            'rlnFourierShellCorrelationMaskedMaps': [1, 0.95, 0.7, 0.2],
            'rlnCorrectedFourierShellCorrelationPhaseRandomizedMaskedMaps': [np.nan, 0.2, 0.1, 0],
            'rlnFourierShellCorrelationCorrected': [1, 0.9, 0.5, 0.1],
        })

    def test_corrected_curve_is_primary_and_other_curves_are_preserved(self):
        frame = self.frame()
        data = FscData.from_model_frame(frame)
        np.testing.assert_array_equal(data.fsc, frame['rlnFourierShellCorrelationCorrected'])
        np.testing.assert_array_equal(data.fsc_masked, frame['rlnFourierShellCorrelationMaskedMaps'])
        np.testing.assert_array_equal(data.fsc_unmasked, frame['rlnFourierShellCorrelationUnmaskedMaps'])

    def test_unmasked_only_export_is_supported(self):
        frame = self.frame().iloc[:, :2]
        data = FscData.from_model_frame(frame)
        np.testing.assert_array_equal(data.fsc, frame.iloc[:, 1])
        self.assertIsNone(data.fsc_masked)

    def test_published_resolution_is_used_instead_of_recomputing_a_crossing(self):
        data = FscData.from_model_tables({
            'model_class_1': self.frame(),
            'model_classes': pd.DataFrame({'rlnEstimatedResolution': [5.1]}),
        })
        self.assertEqual(data.estimated_resolution, 5.1)
        from bakery.fsc.draw_fsc import draw_fsc
        fig, ax = plt.subplots()
        draw_fsc(ax, data)
        self.assertIn('5.1', ax.get_title())
        plt.close(fig)

    def test_relion_curve_remains_unchanged(self):
        frame = pd.DataFrame({'rlnAngstromResolution': [20, 10, 5], 'rlnGoldStandardFsc': [0.8, 0.5, 0.1]})
        data = FscData.from_model_frame(frame)
        np.testing.assert_array_equal(data.fsc, [0.8, 0.5, 0.1])
        self.assertIsNone(data.fsc_unmasked)

    def test_beam_fsc_and_job_card_render_from_mode12_map(self):
        with tempfile.TemporaryDirectory(prefix='beam fsc ') as directory:
            root = Path(directory)
            star = root / 'model.star'
            starfile.write({'model_class_1': self.frame()}, star)
            volume = root / 'map.mrc'
            rng = np.random.default_rng(42)
            with mrcfile.new(volume) as stream:
                stream.set_data(rng.normal(size=(16, 16, 16)).astype(np.float16))
            for command, args in [
                ('map-orthoslice-atlas', ['--volume-file', str(volume), '--slice-thickness-px', '1']),
                ('fsc', ['--metadata-file', str(star), '--input-type', 'relion_model_star']),
                ('refine3d-job-card', ['--model-star-file', str(star), '--volume-file', str(volume)]),
            ]:
                output = root / (command + '.png')
                result = CliRunner().invoke(cli, [command, *args, '--output-file', str(output)])
                self.assertEqual(result.exit_code, 0, str(result.exception) + result.output)
                self.assertGreater(output.stat().st_size, 0)
            plt.close('all')
