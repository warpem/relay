"""Compile completed Beam subclass groups and optional selection resources."""
import json
import os
from pathlib import Path

import mrcfile
import numpy as np
import pandas as pd
import starfile
import typer

from ._cli import cli


def atomic_json(path, value):
    temporary = path.with_name('.' + path.name + '.partial')
    temporary.write_text(json.dumps(value, allow_nan=False))
    os.replace(temporary, path)


@cli.command(no_args_is_help=True)
def beam_class2d_subclasses(
    beam_directory: Path = typer.Option(...),
    source_manifest: Path = typer.Option(...),
    output_directory: Path = typer.Option(...),
    finalize: bool = False,
):
    manifest = json.loads(source_manifest.read_text())
    output_directory.mkdir(parents=True, exist_ok=True)
    groups = []
    offset = 0
    stacks, models, particles = [], [], []
    optics = None
    sampling = None
    for record in sorted(manifest['parents'], key=lambda p: p['parent_class']):
        parent = int(record['parent_class'])
        if record['status'] != 'complete':
            groups.append(dict(ParentId=parent, FirstClass=0, Classes=[], Reason=record.get('reason', 'Skipped')))
            continue
        # Resolve from identity, not a path supplied by the metadata.
        source = beam_directory / 'subclasses' / f'parent-{parent:03d}'
        summary = json.loads((source / 'summary.json').read_text())
        count = int(summary['class_count'])
        target = output_directory / f'parent-{parent:03d}'
        target.mkdir(exist_ok=True)
        stats_path = target / 'stats.json'
        if not stats_path.exists():
            counts = np.asarray(summary['occupancies'], dtype=float)
            if len(counts) != count or not np.isfinite(counts).all() or np.any(counts < 0):
                raise ValueError(f'Invalid subclass populations for parent {parent}')
            display = summary['display_reconstruction']
            shells = summary['history'][-1]['display_map_shells']
            stats = [dict(Id=i+1, Distribution=float(counts[i]/max(counts.sum(), 1)),
                          Resolution=(float(display['box'] * display['pixel_size'] / shell)
                                      if shell > 0 and np.isfinite(shell) else None))
                     for i, shell in enumerate(shells)]
            if len(stats) != count:
                raise ValueError(f'Subclass resolution count mismatch for parent {parent}')
            atomic_json(stats_path, stats)
        stats = json.loads(stats_path.read_text())
        groups.append(dict(ParentId=parent, FirstClass=offset+1, Classes=stats, Reason=None))
        if finalize:
            with mrcfile.open(source / 'classes_display.mrcs') as stack:
                values = stack.data.copy()
                pixel_size = float(stack.voxel_size.x)
            if sampling is not None and (values.shape[1:] != stacks[0].shape[1:] or
                                         not np.isclose(pixel_size, sampling)):
                raise ValueError('Subclass image grids do not match')
            sampling = pixel_size
            stacks.append(values)
            tables = starfile.read(source / 'particles.star', always_dict=True)
            if optics is None:
                optics = tables['optics']
            else:
                pd.testing.assert_frame_equal(optics, tables['optics'])
            rows = tables['particles'].copy()
            labels = rows['rlnClassNumber'].to_numpy()
            if not np.all((labels >= 1) & (labels <= count) & (labels == np.floor(labels))):
                raise ValueError('Subclass particle labels are out of range')
            if 'rlnBeamParentClassNumber' in rows and not rows['rlnBeamParentClassNumber'].eq(parent).all():
                raise ValueError('Subclass particle parent identity mismatch')
            rows['rlnBeamParentClassNumber'] = parent
            rows['rlnClassNumber'] = labels.astype(int) + offset
            particles.append(rows)
            models.extend(dict(rlnBeamParentClassNumber=parent, rlnBeamSubclassNumber=i+1, rlnReferenceImage=f'{offset+i+1:06d}@{output_directory / "classes.mrcs"}',
                               rlnEstimatedResolution=s['Resolution'] if s['Resolution'] is not None else 999., rlnClassDistribution=s['Distribution'])
                          for i, s in enumerate(stats))
        offset += count
    if finalize and stacks:
        # Publish a complete selection collection once; groups are rendered incrementally.
        temporary = output_directory / '.classes.mrcs.partial'
        with mrcfile.new(temporary, overwrite=True) as stack:
            stack.set_data(np.concatenate(stacks).astype('float32'))
            stack.voxel_size = (sampling, sampling, 1)
            stack.set_image_stack()
        os.replace(temporary, output_directory / 'classes.mrcs')
        rows = pd.concat(particles, ignore_index=True)
        counts = rows['rlnClassNumber'].value_counts()
        for i, model in enumerate(models):
            model['rlnClassDistribution'] = float(counts.get(i+1, 0)/max(len(rows), 1))
        for name, tables in [('particles.star', dict(optics=optics, particles=rows)),
                             ('model.star', {'model_classes': pd.DataFrame(models)})]:
            temporary = output_directory / ('.' + name + '.partial')
            starfile.write(tables, temporary, overwrite=True, float_format="%.17g")
            os.replace(temporary, output_directory / name)
        atomic_json(output_directory / 'stats.json', [dict(Id=i+1, Resolution=m['rlnEstimatedResolution'],
                                                          Distribution=m['rlnClassDistribution'])
                                                     for i, m in enumerate(models)])
    atomic_json(output_directory / 'groups.json', groups)
