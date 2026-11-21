import json

import numpy as np
import pytest
from typer.testing import CliRunner

from bakery import cli
from bakery.cli.deduplicate_particles_job_card import rebin, summary_lines


def _report(**changes):
    counts = [0] * 2000
    counts[10] = 40      # duplicates near 5 Å
    for i in range(200, 700):
        counts[i] = 30   # packing at 100-350 Å
    report = {
        'version': 1, 'particles': 15_040, 'kept': 15_000, 'removed': 40,
        'distance_angstrom': 20.0, 'drop': 'worse', 'score': 'bmSignalScale', 'lower_is_better': False,
        'groups': 120, 'group_kind': 'micrograph', 'position_sources': ['coordinates x rlnMicrographPixelSize'],
        'notes': [],
        'nearest_neighbour_angstrom': {
            'particles': 15_040, 'quantiles': {'p5': 103.0, 'p25': 160.0, 'p50': 225.0, 'p75': 290.0, 'p95': 340.0,
                                               'p99': 348.0},
            'bin_width': 0.5, 'counts': counts, 'beyond': 0,
        },
    }
    report.update(changes)
    return report


@pytest.mark.parametrize('changes', [
    {},
    {'drop': 'both'},
    {'score': 'rlnMaxValueProbDistribution', 'lower_is_better': True},
    {'score': None, 'group_kind': 'tilt series', 'groups': 1},
    {'nearest_neighbour_angstrom': {'particles': 0, 'quantiles': {}, 'bin_width': 0.5, 'counts': [], 'beyond': 0}},
    {'distance_angstrom': 500.0},
])
def test_card_renders(tmp_path, changes):
    report_file = tmp_path / 'report.json'
    report_file.write_text(json.dumps(_report(**changes)))
    output_file = tmp_path / 'card.png'
    result = CliRunner().invoke(cli, ['deduplicate-particles-job-card', '--report-file', str(report_file),
                                      '--output-file', str(output_file)])
    assert result.exit_code == 0, result.exception
    assert output_file.stat().st_size > 0
    assert output_file.with_suffix('.pdf').stat().st_size > 0


def test_rebin_merges_fine_bins_up_to_the_cutoff():
    counts = np.ones(2000, dtype=np.int64)
    merged, edges = rebin(counts, 0.5, upper=340.0)
    assert edges[-1] >= 340.0
    assert edges[-1] - edges[-2] == pytest.approx(8.5)  # 17 fine bins: 340 Å / 40 bins
    assert merged.sum() == round(edges[-1] / 0.5)
    assert np.all(merged == 17)


def test_summary_lines_fit_the_card_column():
    for changes in ({}, {'drop': 'both'}, {'score': 'rlnMaxValueProbDistribution', 'lower_is_better': True},
                    {'score': None}, {'groups': 12_345, 'particles': 2_345_678, 'kept': 2_300_000,
                                      'removed': 45_678}):
        lines = summary_lines(_report(**changes))
        assert all(len(text) <= 22 or size < 4.5 for text, size, _ in lines), lines
    texts = [text for text, *_ in summary_lines(_report(drop='both'))]
    assert 'both removed' in texts and not any(text.startswith('Score') for text in texts)
