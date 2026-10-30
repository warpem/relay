"""Bounded static diagnostics from committed Beam products; never loads checkpoints."""
import json
from pathlib import Path

import matplotlib.pyplot as plt
import numpy as np
import pandas as pd
import starfile
import typer

from ._cli import cli
from ..figure_export import save_figure, _write_atomic

MAX_SCATTER_POINTS = 2000


def particles(tables):
    frame = tables.get('particles')
    if frame is None:
        return pd.DataFrame()
    return frame


def match_defocus(before, after):
    """Match stable particle identities; never assume STAR row ordering is stable."""
    columns = ['rlnDefocusU', 'rlnDefocusV']
    if any(c not in before or c not in after for c in columns):
        return pd.DataFrame()
    key = next((k for k in ('ucsfUid', 'rlnImageName') if k in before and k in after
                and before[k].notna().all() and after[k].notna().all()
                and before[k].is_unique and after[k].is_unique), None)
    if key is None:
        return pd.DataFrame()
    joined = before[[key, *columns]].merge(after[[key, *columns]], on=key,
                                          suffixes=('_before', '_after'), validate='one_to_one')
    start = (joined.rlnDefocusU_before + joined.rlnDefocusV_before) / 2
    end = (joined.rlnDefocusU_after + joined.rlnDefocusV_after) / 2
    return pd.DataFrame({'start': start, 'delta': end - start}).replace([np.inf, -np.inf], np.nan).dropna()


def _save(fig, path):
    try:
        fig.tight_layout()
        save_figure(fig, path, dpi=150)
    finally:
        plt.close(fig)


def ctf_figures(before, after, output: Path, iteration: int, baseline: int):
    """All particles/groups in aggregates; no coefficient-index plots."""
    start, final = particles(before), particles(after)
    matched = match_defocus(start, final)
    optics = after.get('optics', pd.DataFrame()).copy()
    prefix = f'ctf_it{iteration:04d}'
    fig, axes = plt.subplots(1, 2, figsize=(10, 3.5))
    if len(matched):
        delta = matched.delta.to_numpy()
        extent = max(1., float(np.max(np.abs(delta))))
        axes[0].hist(delta, bins=np.linspace(-extent, extent, 101), color='#1f77b4')
        axes[0].set(xlabel='Total mean-defocus correction (Å)', ylabel='Particles',
                    title=f'Defocus correction · RMS {np.sqrt(np.mean(delta ** 2)):.2f} Å')
    else:
        axes[0].text(.5, .5, 'No uniquely matched defocus values', ha='center', transform=axes[0].transAxes)
        axes[0].set_axis_off()
    tilt_columns = ['rlnBeamTiltX', 'rlnBeamTiltY']
    if all(c in optics for c in tilt_columns):
        tilt = optics.dropna(subset=tilt_columns)
        if len(tilt) > MAX_SCATTER_POINTS:
            collection = axes[1].hexbin(tilt.rlnBeamTiltX, tilt.rlnBeamTiltY, gridsize=45,
                                        mincnt=1, bins='log', cmap='viridis', rasterized=True)
            fig.colorbar(collection, ax=axes[1], label='Optics groups per bin')
        else:
            axes[1].scatter(tilt.rlnBeamTiltX, tilt.rlnBeamTiltY, s=25, alpha=.8)
            if len(tilt) <= 10:
                for row in tilt.itertuples():
                    axes[1].annotate(str(int(row.rlnOpticsGroup)), (row.rlnBeamTiltX, row.rlnBeamTiltY),
                                     xytext=(5, 5), textcoords='offset points')
        axes[1].set(xlabel='Beam tilt X (mrad)', ylabel='Beam tilt Y (mrad)', title=f'Beam tilt · {len(tilt):,} optics groups')
        axes[1].set_aspect('equal', adjustable='datalim')
    else:
        axes[1].text(.5, .5, 'Beam tilt not published', ha='center', transform=axes[1].transAxes)
        axes[1].set_axis_off()
    fig.suptitle(f'CTF refinement · iteration {iteration} − {baseline} · histogram includes all matched particles')
    _save(fig, output / f'{prefix}.svg')
    return {'baseline': baseline, 'matched_particles': len(matched), 'group_count': len(optics), 'groups': []}


@cli.command(no_args_is_help=True)
def beam_refinement_diagnostics(
    beam_directory: Path = typer.Option(...),
    iteration: int = typer.Option(...),
    output_directory: Path = typer.Option(...),
):
    output_directory.mkdir(parents=True, exist_ok=True)
    def product(i, suffix):
        return beam_directory / f'it{i:03d}' / f'run_it{i:03d}_{suffix}'
    manifest_path = beam_directory / f'it{iteration:03d}' / 'manifest.json'
    manifest = json.loads(manifest_path.read_text()) if manifest_path.exists() else {}
    restart = manifest.get('restart_state', {})
    controller = restart.get('controller', {})
    history = []
    for i in range(1, iteration+1):
        path = product(i, 'model.star')
        if not path.exists():
            continue
        tables = starfile.read(path, always_dict=True)
        classes = tables.get('model_classes', pd.DataFrame())
        if 'rlnEstimatedResolution' in classes and len(classes):
            value = float(classes.rlnEstimatedResolution.iloc[0])
            if np.isfinite(value) and value > 0:
                history.append((i, value))
    result = {'version': 2, 'history': bool(history), 'ctf': None}
    if history:
        fig, ax = plt.subplots(figsize=(5.5, 3))
        ax.plot(*zip(*history), 'o-', markersize=4, label='Map', color='#1f77b4')
        alignment = controller.get('alignment_resolution_history', [])[:iteration]
        if alignment:
            ax.plot(range(1, len(alignment)+1), alignment, '--', color='#7f7f7f', label='Alignment')
        counts = controller.get('particle_count_history', [])[:iteration]
        if counts:
            count_ax = ax.twinx()
            count_ax.step(range(1, len(counts)+1), counts, where='mid', color='#2ca02c', alpha=.5)
            count_ax.set_ylabel('Particles', color='#2ca02c')
        ax.set(xlabel='Iteration', ylabel='Resolution (Å)', xticks=[i for i, _ in history])
        ax.legend(fontsize=10)
        _save(fig, output_directory / f'history_it{iteration:04d}.svg')
    baseline = restart.get('ctf_refined_iteration')
    if isinstance(baseline, int) and 0 < baseline < iteration and product(baseline, 'data.star').exists():
        # parse_as_string preserves uint64 particle UIDs exactly.
        read = lambda i: starfile.read(product(i, 'data.star'), always_dict=True,
                                      parse_as_string=['ucsfUid', 'rlnImageName'])
        result['ctf'] = ctf_figures(read(baseline), read(iteration), output_directory, iteration, baseline)
    # Publish availability only after every referenced figure/export is complete.
    _write_atomic(output_directory / f'diagnostics_it{iteration:04d}.json', json.dumps(result))
