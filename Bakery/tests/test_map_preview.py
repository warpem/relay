"""Single-view previews integrate density outside the central plane consistently."""
import importlib

import matplotlib.pyplot as plt
import mrcfile
import numpy as np
import pandas as pd
import pytest
import starfile
from matplotlib.figure import Figure
from typer.testing import CliRunner

from bakery.cli import cli
from bakery.map_preview import draw_map_preview, project_z, read_z_projection


def test_projection_uses_full_depth_and_accumulates_half_precision_safely(tmp_path):
    volume = np.zeros((4, 12, 18), dtype=np.float16)
    volume[0, 4, 8] = 40000
    volume[-1, 4, 8] = 40000
    path = tmp_path / 'volume.mrc'
    mrcfile.write(path, volume)
    projection = read_z_projection(path)
    assert projection.dtype == np.float32
    assert projection[4, 8] == 80000
    assert np.isfinite(projection).all()
    np.testing.assert_array_equal(projection, project_z(volume))


def test_zoom_preserves_full_projection_contrast():
    image = np.zeros((12, 18))
    image[0, 0], image[-1, -1] = -2, 10
    fig, ax = plt.subplots()
    artist = draw_map_preview(ax, image)
    np.testing.assert_allclose(ax.get_xlim(), [2.5, 14.5])
    np.testing.assert_allclose(ax.get_ylim(), [1.5, 9.5])
    assert artist.get_clim() == (-2, 10)
    plt.close(fig)


@pytest.mark.parametrize('module,function,kwargs', [
    ('class3d_job_card', 'draw_z_slice_panel', {'label': '5', 'n_rows': 1}),
    ('m_refine_job_card', 'draw_z_slice_panel_with_resolution',
     {'species_name': 'test', 'resolution': 8., 'n_rows': 1}),
    *[(name, 'draw_z_slice_panel', {}) for name in (
        'refine3d_job_card', 'postprocess3d_job_card', 'import_map_3d_job_card',
        'm_species_job_card', 'm_refine_job_card')],
])
def test_card_panels_use_identical_contrast_and_zoom(module, function, kwargs):
    image = np.arange(144, dtype=np.float32).reshape(12, 12) - 10
    fig, ax = plt.subplots()
    panel = getattr(importlib.import_module(f'bakery.cli.{module}'), function)
    panel(ax=ax, image=image, **kwargs)
    assert ax.images[0].get_clim() == (-10, 133)
    np.testing.assert_allclose(ax.get_xlim(), [1.5, 9.5])
    np.testing.assert_allclose(ax.get_ylim(), [1.5, 9.5])
    plt.close(fig)


def test_class_and_refinement_cli_render_the_same_projection(tmp_path, monkeypatch):
    volume = np.zeros((4, 12, 12), dtype=np.float32)
    volume[0, 5, 5] = 3
    volume[-1, 5, 5] = 4
    path = tmp_path / 'volume.mrc'
    mrcfile.write(path, volume)
    model = tmp_path / 'model.star'
    starfile.write({'model_class_1': pd.DataFrame({
        'rlnAngstromResolution': [20., 10., 5.], 'rlnGoldStandardFsc': [.9, .5, .1],
    })}, model)
    previews = []
    original = Figure.savefig

    def save(fig, filename, *args, **kwargs):
        if isinstance(filename, type(path)) and filename.suffix == '.png':
            artist = fig.axes[0].images[0]
            previews.append((artist.get_array().copy(), artist.get_clim()))
        original(fig, filename, *args, **kwargs)

    monkeypatch.setattr(Figure, 'savefig', save)
    for command, args in [
        ('class3d-job-card', ['--class-number', '5']),
        ('refine3d-job-card', ['--model-star-file', str(model)]),
        ('import-map-3d-job-card', []),
        ('xy-slice', ['--slice-thickness-angstroms', '1']),
    ]:
        output = tmp_path / f'{command}.png'
        result = CliRunner().invoke(cli, [command, '--volume-file', str(path),
                                        '--output-file', str(output), *args])
        assert result.exit_code == 0, result.exception
        assert output.stat().st_size > 0
    assert len(previews) == 4
    for image, limits in previews:
        np.testing.assert_array_equal(image, volume.sum(axis=0))
        assert limits == (0, 7)
    assert not plt.get_fignums()


def test_template_preview_keeps_diameter_overlay_in_map_coordinates(tmp_path):
    from bakery.ts_template_match.plot_ts_template_match_job_card import plot_ts_template_match_job_card

    rng = np.random.default_rng(42)
    tomogram = tmp_path / 'tomogram.mrc'
    mrcfile.write(tomogram, rng.normal(size=(16, 16, 16)).astype(np.float32), voxel_size=2)
    template = tmp_path / 'template.mrc'
    volume = np.zeros((4, 300, 300), dtype=np.float32)
    volume[0, 140, 140] = 3
    volume[-1, 140, 140] = 4
    mrcfile.write(template, volume, voxel_size=2)
    particles = tmp_path / 'particles.star'
    particles.write_text('data_\nloop_\n_rlnCoordinateX #1\n_rlnCoordinateY #2\n_rlnCoordinateZ #3\n')
    fig, axes = plt.subplots(ncols=2)
    plot_ts_template_match_job_card(*axes, tomogram, particles, template, 8)
    np.testing.assert_array_equal(axes[1].images[0].get_array(), volume.sum(axis=0))
    assert axes[1].images[0].get_clim() == (0, 7)
    np.testing.assert_allclose(axes[1].get_xlim(), [49.5, 249.5])
    assert axes[1].patches[0].radius == 2
    assert axes[1].patches[0].center == (149.5, 149.5)
    plt.close(fig)


def test_export_particle_map_is_projected_without_changing_tomogram_slab(tmp_path, monkeypatch):
    from bakery.ts_export_particles.plot_ts_export_particles_job_card import plot_ts_export_particles_job_card

    path = tmp_path / 'volume.mrc'
    rng = np.random.default_rng(42)
    volume = rng.normal(size=(4, 12, 12)).astype(np.float32)
    mrcfile.write(path, volume)
    figures = []
    monkeypatch.setattr(Figure, 'savefig', lambda fig, *args, **kwargs: figures.append(fig))
    plot_ts_export_particles_job_card(path, path, 2., 8., tmp_path / 'card.png')
    ax = figures[0].axes[1]
    np.testing.assert_array_equal(ax.images[0].get_array(), volume.sum(axis=0))
    np.testing.assert_allclose(ax.get_xlim(), [1.5, 9.5])
    assert ax.patches[0].radius == 2
