"""Per-class figures select matching particle and model tables, including empty classes."""
import numpy as np
import pandas as pd
import starfile
from typer.testing import CliRunner

from bakery.cli import cli
from bakery.fsc.fsc_data_model import FscData


def test_class_fsc_uses_matching_curve_and_summary_row():
    star = {
        'model_class_1': pd.DataFrame({'rlnAngstromResolution': [10., 5.], 'rlnGoldStandardFsc': [.9, .3]}),
        'model_class_2': pd.DataFrame({'rlnAngstromResolution': [10., 5.],
                                     'rlnFourierShellCorrelationUnmaskedMaps': [.4, .1]}),
        'model_classes': pd.DataFrame({'rlnEstimatedResolution': [5., 9.],
                                      'rlnDirectionalBestResolution': [4., 7.],
                                      'rlnDirectionalWorstResolution': [6., 12.],
                                      'rlnCylindricalFscAreaRatio': [.9, .7]}),
    }
    data = FscData.from_model_tables(star, 2)
    np.testing.assert_array_equal(data.fsc, [.4, .1])
    assert data.estimated_resolution == 9.
    assert data.directional_best == 7.
    assert data.directional_worst == 12.
    assert data.cfar == .7


def test_class_figures_render_present_and_empty_classes(tmp_path):
    particles = tmp_path / 'particles.star'
    starfile.write({'particles': pd.DataFrame({
        'rlnAngleRot': [0., 90.], 'rlnAngleTilt': [20., 60.], 'rlnClassNumber': [1, 2],
    })}, particles)
    model = tmp_path / 'model.star'
    starfile.write({
        'model_class_1': pd.DataFrame({'rlnAngstromResolution': [20., 10., 5.], 'rlnGoldStandardFsc': [.8, .5, .1]}),
        'model_class_2': pd.DataFrame({'rlnAngstromResolution': [20., 10., 5.], 'rlnGoldStandardFsc': [.6, .2, .0]}),
    }, model)
    result = CliRunner().invoke(cli, ['fsc', '--input-type', 'relion_model_star', '--metadata-file', str(model),
                                    '--class-number', '2', '--output-file', str(tmp_path / 'fsc.png'), '--svg'])
    assert result.exit_code == 0, result.output
    for number, directions in [(2, np.array([[0., 1., 0.]])), (3, np.empty((0, 3)))]:
        direction_file = tmp_path / f'directions{number}.npy'
        np.save(direction_file, directions)
        result = CliRunner().invoke(cli, [
            'orientation-and-fourier-sampling', '--particles-file', str(particles),
            '--input-type', 'relion_star_file', '--grid-resolution', '0', '--colormap', 'chrisluts:I_Orange',
            '--class-number', str(number), '--projection-directions-file', str(direction_file), '--svg',
            '--output-orientation-file', str(tmp_path / f'angles{number}.png'),
            '--output-fourier-sampling-file', str(tmp_path / f'sampling{number}.png'),
        ])
        assert result.exit_code == 0, result.output
        for name in [f'angles{number}', f'sampling{number}']:
            for suffix in ['.png', '.svg', '.light.svg', '.dark.svg']:
                assert (tmp_path / f'{name}{suffix}').is_file()


def test_compact_fsc_is_rendered_at_column_width_with_a_readable_legend(tmp_path):
    import xml.etree.ElementTree as ET
    model = tmp_path / 'model.star'
    starfile.write({
        'model_class_1': pd.DataFrame({
            'rlnAngstromResolution': [np.inf, 20., 10., 5.],
            'rlnFourierShellCorrelationUnmaskedMaps': [1., .7, .3, .0],
            'rlnFourierShellCorrelationMaskedMaps': [1., .9, .5, .1],
            'rlnFourierShellCorrelationCorrected': [1., .8, .4, .05],
            'rlnCorrectedFourierShellCorrelationPhaseRandomizedMaskedMaps': [np.nan, .1, .0, .0],
        }),
        'model_classes': pd.DataFrame({'rlnEstimatedResolution': [6.2],
                                      'rlnDirectionalBestResolution': [5.8],
                                      'rlnDirectionalWorstResolution': [8.1],
                                      'rlnCylindricalFscAreaRatio': [.85]}),
    }, model)
    directions = tmp_path / 'directional.csv'
    pd.DataFrame({'s1_20': [.9, .8], 's2_10': [.5, .4], 's3_5': [.1, .05]}).to_csv(directions, index=False)
    output = tmp_path / 'compact.png'
    result = CliRunner().invoke(cli, ['fsc', '--input-type', 'relion_model_star', '--metadata-file', str(model),
                                    '--output-file', str(output), '--svg', '--compact', '--directional-fsc-file', str(directions)])
    assert result.exit_code == 0, result.output
    svg = ET.fromstring(output.with_suffix('.svg').read_text())
    assert abs(float(svg.attrib['viewBox'].split()[2]) - 280.8) < 1e-6  # 30% larger canvas, unchanged point sizes.
    texts = [t.text for t in svg.findall('.//{http://www.w3.org/2000/svg}text')]
    assert 'FSC' in texts
    assert 'Corrected' in texts and 'Phase rand.' in texts
    assert '10–90% band' in texts
    assert not any(text and text.startswith('Resolution:') for text in texts)
