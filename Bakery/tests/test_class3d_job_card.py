import mrcfile
import numpy as np
import pandas as pd
import pytest
import starfile
from matplotlib import pyplot as plt
from PIL import Image
from typer.testing import CliRunner

from bakery import cli
from bakery.cli.class3d_job_card import class_grid, class_resolutions


@pytest.mark.parametrize('count, width', [
    (1, 2), (2, 2), (5, 5), (6, 2), (8, 2), (9, 3), (12, 3),
    (13, 4), (16, 4), (17, 5), (20, 5), (21, 5), (1000, 5),
])
def test_renderer_matches_card_aspect_ratio(count, width):
    rows, cols = class_grid(count)
    assert cols / rows == width
    assert rows * cols >= min(count, 20)


@pytest.mark.parametrize('command', ['class3d-job-card', 'initial-reference-3d-job-card'])
@pytest.mark.parametrize('count', [1, 6, 9, 20, 21])
def test_rendered_size_and_resolution_labels(tmp_path, monkeypatch, command, count):
    volumes = []
    for size in [16, 24]:
        path = tmp_path / f'map{size}.mrc'
        mrcfile.write(path, np.ones((size, size, size), dtype=np.float32))
        volumes.append(path)
    model = tmp_path / 'model.star'
    # Distinct values prove selected classes use their original 1-based IDs.
    starfile.write({'model_classes': pd.DataFrame({
        'rlnEstimatedResolution': [float('nan'), 0, -1] + list(range(4, 30)),
    })}, model)
    output = tmp_path / 'card.png'
    args = [command, '--output-file', str(output), '--model-star-file', str(model)]
    for index in range(count):
        args += ['--volume-file', str(volumes[index % 2]), '--class-number', str(index + 4)]
    figures = []
    original_close = plt.close
    monkeypatch.setattr(plt, 'close', lambda fig=None: figures.append(fig) if hasattr(fig, 'axes') else None)
    try:
        result = CliRunner().invoke(cli, args)
        assert result.exit_code == 0, result.exception
        rows, cols = class_grid(count)
        with Image.open(output) as image:
            assert image.width / image.height == pytest.approx(cols / rows, abs=0.01)
        assert output.with_suffix('.pdf').is_file()
        fig = figures[-1]
        populated = [ax for ax in fig.axes if ax.images]
        assert len(populated) == min(count, 20)
        for index, ax in enumerate(populated):
            assert ax.texts[0].get_text() == str(index + 4)
            label = ax.texts[1]
            assert label.get_text() == f'{index + 4:.1f} Å'
            assert label.get_position() == (0.96, 0.04)
            assert label.get_ha() == 'right'
    finally:
        for fig in figures:
            original_close(fig)


def test_missing_and_invalid_resolutions_are_omitted(tmp_path):
    assert class_resolutions(None, [1, 3]) == [None, None]
    model = tmp_path / 'model.star'
    starfile.write({'model_classes': pd.DataFrame({
        'rlnEstimatedResolution': [float('nan'), float('inf'), 0, -1, 3.25],
    })}, model)
    assert class_resolutions(model, [5, 1, 2, 3, 4, 99]) == [3.25, None, None, None, None, None]
    starfile.write({'model_classes': pd.DataFrame({'rlnClassDistribution': [1.0]})}, model, overwrite=True)
    assert class_resolutions(model, [1]) == [None]
