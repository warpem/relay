"""Compile portable Beam class2d snapshots into Relay-owned figures and statistics."""
import json
from pathlib import Path

import matplotlib.pyplot as plt
import mrcfile
import numpy as np
import starfile
import typer

from ._cli import cli
from ..figure_export import save_figure


def image_atlas(images, output, *, signed=False):
    images = np.asarray(images, dtype=np.float32)
    if images.ndim != 3 or not np.isfinite(images).all():
        raise ValueError('Image atlas requires a finite stack of square images')
    # Independent robust contrast keeps weak components visible. Signed PCs share
    # a zero midpoint; no offsets are removed from their exported scientific data.
    normalized = []
    for image in images:
        if signed:
            scale = max(float(np.quantile(np.abs(image), .995)), 1e-20)
            normalized.append(np.clip(image / scale * .5 + .5, 0, 1))
        else:
            low, high = np.quantile(image, .005), image.max()
            normalized.append(np.clip((image - low) / max(high - low, 1e-20), 0, 1))
    plt.imsave(output, np.concatenate(normalized, axis=1), cmap='gray', vmin=0, vmax=1, origin='lower')


@cli.command(no_args_is_help=True)
def beam_class2d_figures(
    snapshot_directory: Path = typer.Option(...),
    output_directory: Path = typer.Option(...),
    iteration: int = typer.Option(...),
):
    output_directory.mkdir(parents=True, exist_ok=True)
    tag = f'it{iteration:04d}'
    table = starfile.read(snapshot_directory / 'model.star', always_dict=True)['model_classes']
    stats = []
    for i, row in table.iterrows():
        def value(name):
            v = row.get(name)
            return float(v) if v is not None and np.isfinite(v) else None
        stats.append(dict(Id=int(i)+1, Resolution=value('rlnEstimatedResolution'),
                          Distribution=value('rlnClassDistribution')))
    image_atlas(mrcfile.read(snapshot_directory / 'classes.mrcs'), output_directory / f'classes_{tag}.png')
    info = dict(PrincipalComponentCount=0, EmbeddingParticleCount=0)
    components = snapshot_directory / 'principal_components.mrcs'
    if components.exists():
        images = mrcfile.read(components)
        info['PrincipalComponentCount'] = len(images) - 1
        image_atlas(images, output_directory / f'pca_{tag}.png', signed=True)
    embeddings = snapshot_directory / 'embeddings.npz'
    if embeddings.exists():
        with np.load(embeddings, allow_pickle=False) as data:
            coordinates = data['coordinates']
            labels = data['discovery_classes']
        if coordinates.ndim != 2 or not np.isfinite(coordinates).all() or len(labels) != len(coordinates):
            raise ValueError('Invalid PCA embedding arrays')
        info['EmbeddingParticleCount'] = len(coordinates)
        figure, ax = plt.subplots(figsize=(4, 3.5))
        if coordinates.shape[1] and len(coordinates):
            # Bound SVG size while preserving deterministic sample coverage.
            ids = np.linspace(0, len(coordinates)-1, min(len(coordinates), 20000), dtype=int)
            y = coordinates[ids, 1] if coordinates.shape[1] > 1 else np.zeros(len(ids))
            ax.scatter(coordinates[ids, 0], y, c=labels[ids], cmap='turbo', s=2, alpha=.35, linewidths=0, rasterized=True)
        ax.set(xlabel='PC 1 (equalized)', ylabel='PC 2 (equalized)')
        figure.tight_layout()
        save_figure(figure, output_directory / f'embedding_{tag}.svg', svg=True)
        plt.close(figure)
    (output_directory / f'stats_{tag}.json').write_text(json.dumps(stats, allow_nan=False))
    (output_directory / f'pca_info_{tag}.json').write_text(json.dumps(info))
