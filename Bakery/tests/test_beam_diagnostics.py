import json
import importlib
from pathlib import Path

import matplotlib.pyplot as plt
import numpy as np
import pandas as pd
import starfile
from typer.testing import CliRunner

from bakery.cli import cli
from bakery.cli.beam_refinement_diagnostics import match_defocus, ctf_figures
from bakery.fsc.draw_fsc import draw_fsc
from bakery.fsc.fsc_data_model import FscData


def test_defocus_matches_uint64_identities_not_row_positions():
    before = pd.DataFrame({'ucsfUid': ['18446744073709551615', '18446744073709551614'],
                           'rlnDefocusU': [100., 200.], 'rlnDefocusV': [80., 180.]})
    after = before.iloc[::-1].copy()
    after.rlnDefocusU += [6, 10]
    after.rlnDefocusV += [6, 10]
    result = match_defocus(before, after)
    np.testing.assert_array_equal(result.start, [90, 190])
    np.testing.assert_array_equal(result.delta, [10, 6])
    before.ucsfUid = 'duplicate'
    assert match_defocus(before, after).empty


def test_overview_uses_linear_counts_without_coefficient_plots_or_tsv(tmp_path, monkeypatch):
    n = 25
    optics = pd.DataFrame({'rlnOpticsGroup': range(1, n+1), 'rlnBeamTiltX': np.linspace(0, .2, n),
                           'rlnBeamTiltY': np.linspace(.3, .4, n),
                           'rlnOddZernike': ['[0,1]']*n, 'rlnEvenZernike': ['[0,2]']*n})
    data = pd.DataFrame({'ucsfUid': [str(i) for i in range(n)], 'rlnOpticsGroup': range(1, n+1),
                        'rlnDefocusU': np.full(n, 100.), 'rlnDefocusV': np.full(n, 100.)})
    writes = []
    def save(fig, path):
        writes.append(path.name)
        assert len(fig.axes) == 2
        assert fig.axes[0].get_yscale() == 'linear'
        assert fig.axes[0].get_ylabel() == 'Particles'
        plt.close(fig)
    monkeypatch.setattr(importlib.import_module('bakery.cli.beam_refinement_diagnostics'), '_save', save)
    result = ctf_figures({'particles': data, 'optics': optics}, {'particles': data, 'optics': optics}, tmp_path, 7, 5)
    assert result['group_count'] == 25
    assert result['matched_particles'] == 25
    assert result['groups'] == []
    assert len(writes) == 1
    assert not list(tmp_path.glob('*.tsv'))


def test_diagnostics_cli_publishes_manifest_after_static_figures(tmp_path):
    beam, output = tmp_path/'beam', tmp_path/'out'
    for i in (1, 2):
        folder = beam/f'it{i:03d}'
        folder.mkdir(parents=True)
        starfile.write({'model_classes': pd.DataFrame({'rlnEstimatedResolution': [3/i]})}, folder/f'run_it{i:03d}_model.star')
        starfile.write({'particles': pd.DataFrame({'ucsfUid': ['18446744073709551615'],
                                                  'rlnDefocusU': [100.+i], 'rlnDefocusV': [100.+i]})},
                       folder/f'run_it{i:03d}_data.star')
    (beam/'it002/manifest.json').write_text(json.dumps({'restart_state': {'ctf_refined_iteration': 1}}))
    result = CliRunner().invoke(cli, ['beam-refinement-diagnostics', '--beam-directory', str(beam),
                                     '--iteration', '2', '--output-directory', str(output)])
    assert result.exit_code == 0, result.output + str(result.exception)
    manifest = json.loads((output/'diagnostics_it0002.json').read_text())
    assert manifest['history']
    assert manifest['ctf']['matched_particles'] == 1
    for stem in ('history_it0002', 'ctf_it0002'):
        for suffix in ('.svg', '.light.svg', '.dark.svg'):
            assert (output/(stem+suffix)).exists()


def test_large_optics_overview_aggregates_without_generating_a_panel_per_group(tmp_path, monkeypatch):
    from matplotlib.collections import PolyCollection
    n = 2001
    optics = pd.DataFrame({'rlnOpticsGroup': range(1, n+1),
                           'rlnBeamTiltX': np.linspace(-.2, .2, n),
                           'rlnBeamTiltY': np.sin(np.linspace(0, 20, n))})
    overviews = []
    def save(fig, path):
        overviews.append(path)
        assert any(isinstance(c, PolyCollection) for c in fig.axes[1].collections)
        plt.close(fig)
    monkeypatch.setattr(importlib.import_module('bakery.cli.beam_refinement_diagnostics'), '_save', save)
    result = ctf_figures({}, {'optics': optics}, tmp_path, 7, 5)
    assert result['group_count'] == 2001
    assert not result['groups']
    assert len(overviews) == 1


def test_directional_band_matches_shell_indices_not_rounded_csv_labels(tmp_path):
    path = tmp_path/'directional.csv'
    pd.DataFrame({'x': [0, 1], 'y': [0, 0], 'z': [1, 0], 's0': [np.nan]*2,
                  's1_10A': [.9, .7], 's2_5A': [.4, .2]}).to_csv(path, index=False)
    data = FscData(resolution=np.array([np.inf, 10.123, 5.234]), fsc=np.array([1., .8, .3]))
    fig, ax = plt.subplots()
    draw_fsc(ax, data, directional_file=path)
    band = ax.collections[0].get_paths()[0].vertices
    np.testing.assert_allclose(np.unique(band[:, 0]), 1/np.array([10.123, 5.234]))
    assert 'Directional 10–90%' in ax.collections[0].get_label()
    plt.close(fig)
