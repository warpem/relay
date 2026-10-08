import json
from xml.etree import ElementTree as ET

import mrcfile
import numpy as np
import pytest
from matplotlib import pyplot as plt
from typer.testing import CliRunner

from bakery import cli


@pytest.fixture
def card_inputs(tmp_path):
    image_file = tmp_path / 'average.mrc'
    image = np.random.default_rng(0).normal(size=(64, 96)).astype(np.float32)
    mrcfile.write(image_file, image, voxel_size=1)

    root = ET.Element('Movie')
    ctf = ET.SubElement(root, 'CTF')
    for name, value in dict(PixelSize=1, Defocus=2, Amplitude=0.1,
                           Cs=2.7, Voltage=300, PhaseShift=0).items():
        ET.SubElement(ctf, 'Param', Name=name, Value=str(value))
    options = ET.SubElement(root, 'OptionsCTF')
    ET.SubElement(options, 'Param', Name='RangeMin', Value='0.1')
    ET.SubElement(options, 'Param', Name='RangeMax', Value='0.8')
    ET.SubElement(root, 'PS1D').text = ';'.join(
        f'{x}|{1 + np.sin(100 * x)}' for x in np.linspace(0, 0.5, 256)
    )
    ET.SubElement(root, 'SimulatedScale').text = '0|2;0.25|1.5;0.5|1'
    xml_file = tmp_path / 'movie.xml'
    ET.ElementTree(root).write(xml_file)
    return image_file, xml_file


@pytest.mark.parametrize('with_motion', [False, True])
@pytest.mark.parametrize('with_diagnostics', [False, True])
def test_card_renders_with_and_without_motion(card_inputs, tmp_path, with_motion, with_diagnostics):
    image_file, xml_file = card_inputs
    output_file = tmp_path / 'card.png'
    if with_diagnostics:
        tree = ET.parse(xml_file)
        tree.getroot().set('CTFSpecimenThicknessAngstrom', '1234.25')
        ET.SubElement(tree.getroot(), 'CTFQuality').text = '0|NaN;0.1|-0.4;0.2|0.85'
        tree.write(xml_file)
    args = ['motion-and-ctf-job-card',
            '--motion-corrected-image-file', str(image_file),
            '--frame-series-xml-file', str(xml_file),
            '--output-file', str(output_file)]
    if with_motion:
        tracks_file = tmp_path / 'motion.json'
        tracks_file.write_text(json.dumps({'0_0': {'x': [0, 1, 2, 3],
                                                   'y': [0, 0.5, 1, 1.5]}}))
        args += ['--motion-tracks-json-file', str(tracks_file)]

    try:
        result = CliRunner().invoke(cli, args)
        assert result.exit_code == 0, result.exception
        assert output_file.stat().st_size > 0
        assert output_file.with_suffix('.pdf').stat().st_size > 0
        image_ax, ctf_ax, quality_ax = plt.gcf().axes
        assert len(image_ax.images) == 1
        assert not image_ax.axison
        assert len(image_ax.lines) == (9 if with_motion else 0)
        assert len(ctf_ax.lines) == 2
        assert len(quality_ax.lines) == (1 if with_diagnostics else 0)
        assert quality_ax.get_ylim()[0] < 0
        if with_diagnostics:
            np.testing.assert_allclose(quality_ax.lines[0].get_xdata(), [0, 0.1, 0.2])
            np.testing.assert_allclose(quality_ax.lines[0].get_ydata(), [np.nan, -0.4, 0.85])
            assert quality_ax.texts[0].get_text() == 'Est. thickness: 1234 Å'
        else:
            assert len(quality_ax.texts) == 0
    finally:
        plt.close('all')


def test_explicit_missing_motion_file_still_fails(card_inputs, tmp_path):
    image_file, xml_file = card_inputs
    try:
        result = CliRunner().invoke(cli, [
            'motion-and-ctf-job-card',
            '--motion-corrected-image-file', str(image_file),
            '--frame-series-xml-file', str(xml_file),
            '--motion-tracks-json-file', str(tmp_path / 'missing.json'),
            '--output-file', str(tmp_path / 'card.png'),
        ])
        assert result.exit_code != 0
        assert isinstance(result.exception, FileNotFoundError)
    finally:
        plt.close('all')
