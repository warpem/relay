from pathlib import Path

import matplotlib.pyplot as plt
import mrcfile
import numpy as np
import pandas as pd
import pytest
import starfile
from matplotlib.figure import Figure
from typer.testing import CliRunner

from bakery.cli import cli


@pytest.fixture
def saved_figures(monkeypatch):
    figures = []
    savefig = Figure.savefig

    def record_figure(fig, filename, *args, **kwargs):
        savefig(fig, filename, *args, **kwargs)
        if isinstance(filename, Path) and filename.suffix == '.png':
            figures.append(fig)

    monkeypatch.setattr(Figure, 'savefig', record_figure)
    return figures


def write_species(root, name, size, blank=False):
    folder = root / name
    folder.mkdir(parents=True)
    coordinates = np.linspace(-1, 1, size)
    z, y, x = np.meshgrid(coordinates, coordinates, coordinates, indexing='ij')
    volume = np.exp(-8 * (x**2 + y**2 + z**2)).astype(np.float32)
    if blank:
        volume.fill(0)
    mrcfile.write(folder / f'{name}_denoised.mrc', volume, voxel_size=1.5)
    (folder / f'{name}.species').write_text(
        '<Species><Param Name="GlobalResolution" Value="3.4" /></Species>'
    )
    data = pd.DataFrame({
        'wrpResolution': [999, 12, 6, 4, 3],
        'wrpFSCRandomized': [0.9, 0.8, 0.1, 0.0, 0.0],
        'wrpFSCUnmasked': [1.0, 0.9, 0.5, 0.2, 0.0],
        'wrpFSCCorrected': [1.0, 0.95, 0.8, 0.4, 0.0],
    })
    starfile.write(data, folder / f'{name}_fsc.star')
    return folder, data


@pytest.mark.parametrize('sizes', [
    [16, 32],
    [16, 16, 16],
    [16, 24, 32, 16, 24, 32],
    [16, 24] * 10,
])
def test_multiple_species_render_equal_squares(tmp_path, saved_figures, sizes):
    root = tmp_path / 'species'
    names = [f'Species {index:02d}' for index in range(len(sizes))]
    for name, size in zip(names, sizes):
        folder, _ = write_species(root, name, size)
        # Multi-species cards must not require FSC files.
        (folder / f'{name}_fsc.star').unlink()
    output = tmp_path / 'card.png'

    result = CliRunner().invoke(cli, [
        'm-refine-job-card', '--species-folder', str(root), '--output-file', str(output),
    ])

    assert result.exit_code == 0, result.output
    assert output.stat().st_size > 0
    assert output.with_suffix('.pdf').stat().st_size > 0
    fig, = saved_figures
    axes = [ax for ax in fig.axes if ax.images]
    assert len(axes) == len(sizes)
    bounds = [ax.get_window_extent().bounds for ax in axes]
    np.testing.assert_allclose([b[2] for b in bounds], bounds[0][2])
    np.testing.assert_allclose([b[3] for b in bounds], bounds[0][2])
    for ax, name, size in zip(axes, names, sizes):
        assert ax.images[0].get_array().shape == (size, size)
        assert np.isfinite(ax.images[0].get_array()).all()
        assert [label.get_text() for label in ax.texts] == [name, '3.4\u2009Å']
    assert not plt.get_fignums()


def test_blank_species_do_not_produce_invalid_pixels(tmp_path, saved_figures):
    root = tmp_path / 'species'
    write_species(root, 'Small', 16, blank=True)
    write_species(root, 'Large', 32, blank=True)
    with np.errstate(divide='raise', invalid='raise'):
        result = CliRunner().invoke(cli, [
            'm-refine-job-card', '--species-folder', str(root),
            '--output-file', str(tmp_path / 'card.png'),
        ])
    assert result.exit_code == 0, result.output
    fig, = saved_figures
    for ax in fig.axes:
        assert not np.ma.getmaskarray(ax.images[0].get_array()).any()
        np.testing.assert_array_equal(ax.images[0].get_array(), 0)


@pytest.mark.parametrize('explicit_species', [False, True])
def test_single_species_keeps_slice_and_fsc(tmp_path, saved_figures, explicit_species):
    root = tmp_path / 'species'
    write_species(root, 'Selected species', 16)
    args = ['m-refine-job-card', '--species-folder', str(root),
            '--output-file', str(tmp_path / 'card.png')]
    if explicit_species:
        write_species(root, 'Other species', 32)
        args.extend(['--species', 'Selected species'])

    result = CliRunner().invoke(cli, args)

    assert result.exit_code == 0, result.output
    fig, = saved_figures
    assert len(fig.axes) == 2
    assert fig.axes[0].images[0].get_array().shape == (16, 16)
    assert fig.axes[1].images[0].get_array().ndim == 3
    assert (tmp_path / 'card.pdf').stat().st_size > 0


def test_species_fsc_uses_m_curves_and_reported_resolution(tmp_path, saved_figures):
    folder, data = write_species(tmp_path, 'Test species', 16)
    # Expanded FSC generation is independent of map availability and box size.
    (folder / 'Test species_denoised.mrc').unlink()
    output = tmp_path / 'fsc.png'

    result = CliRunner().invoke(cli, [
        'm-species-fsc', '--fsc-star-file', str(folder / 'Test species_fsc.star'),
        '--species-xml-file', str(folder / 'Test species.species'),
        '--output-file', str(output),
    ])

    assert result.exit_code == 0, result.output
    assert plt.imread(output).shape[:2] == (1200, 1800)
    assert output.with_suffix('.pdf').stat().st_size > 0
    fig, = saved_figures
    ax, = fig.axes
    for line, column in zip(ax.lines, ['wrpFSCRandomized', 'wrpFSCUnmasked', 'wrpFSCCorrected']):
        np.testing.assert_allclose(line.get_xdata(), 1 / data['wrpResolution'])
        np.testing.assert_allclose(line.get_ydata(), data[column])
    np.testing.assert_allclose(ax.lines[-1].get_ydata(), 0.143)
    assert [label.get_text() for label in ax.get_legend().texts] == [
        'Phase randomized', 'Unmasked', 'Corrected', '0.143 threshold',
    ]
    assert [label.get_text() for label in ax.texts] == ['3.4\u2009Å']
    assert not plt.get_fignums()
