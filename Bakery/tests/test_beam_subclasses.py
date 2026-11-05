"""Subclass displays and global selection labels preserve parent/particle identity."""
import json
import tempfile
import unittest
from pathlib import Path

import mrcfile
import numpy as np
import pandas as pd
import starfile
from typer.testing import CliRunner

from bakery.cli import cli


class BeamSubclassFigureTests(unittest.TestCase):
    def parent(self, beam, number, labels):
        folder = beam / 'subclasses' / f'parent-{number:03d}'
        folder.mkdir(parents=True)
        images = (np.arange(4*8*8).reshape(4, 8, 8) + number).astype('float32')
        with mrcfile.new(folder / 'classes_display.mrcs') as stack:
            stack.set_data(images); stack.voxel_size = (2, 2, 1); stack.set_image_stack()
        counts = np.bincount(labels, minlength=5)[1:]
        (folder / 'summary.json').write_text(json.dumps(dict(class_count=4, occupancies=counts.tolist(),
            display_reconstruction=dict(box=8, pixel_size=2), history=[dict(display_map_shells=[1, 2, 3, 4])])))
        rows = pd.DataFrame(dict(rlnImageName=[f'{i+1}@/source-{number}.mrcs' for i in range(len(labels))],
                                 rlnOpticsGroup=1, rlnClassNumber=labels,
                                 rlnBeamParentClassNumber=number, rlnOriginXAngst=np.arange(len(labels))+.5))
        starfile.write(dict(optics=pd.DataFrame(dict(rlnOpticsGroup=[1], rlnImagePixelSize=[2.])),
                            particles=rows), folder / 'particles.star')
        return dict(parent_class=number, status='complete'), images, rows

    def invoke(self, beam, source, target, finalize=False):
        args = ['beam-class2d-subclasses', '--beam-directory', str(beam), '--source-manifest', str(source),
                '--output-directory', str(target)]
        if finalize: args.append('--finalize')
        result = CliRunner().invoke(cli, args)
        self.assertEqual(result.exit_code, 0, result.output + str(result.exception))

    def test_incremental_groups_then_selectable_collection_across_nonconsecutive_parents(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory); beam = root / 'beam'; target = root / 'relay'; source = root / 'source.json'
            one, images1, rows1 = self.parent(beam, 1, [1, 1, 2, 3, 3, 3, 4, 4])
            three, images3, rows3 = self.parent(beam, 3, [1, 2, 3, 4])
            mrcfile.write(beam / 'classes_display.mrcs', np.stack([images1[0], images1[1], images3[0]]))
            skipped = dict(parent_class=2, status='skipped', reason='fewer than four assigned particles')
            source.write_text(json.dumps(dict(status='running', parents=[one, skipped])))
            self.invoke(beam, source, target)
            self.assertFalse((target / 'particles.star').exists())
            groups = json.loads((target / 'groups.json').read_text())
            self.assertEqual(groups[0]['FirstClass'], 1)
            self.assertTrue((target / 'parent-001/parent.png').exists())
            self.assertEqual(groups[1]['Classes'], [])
            self.assertEqual(groups[1]['Reason'], skipped['reason'])
            self.assertEqual(groups[0]['Classes'][2]['Distribution'], 3/8)
            mtime = (target / 'parent-001/classes.png').stat().st_mtime_ns
            source.write_text(json.dumps(dict(status='complete', parents=[three, skipped, one])))
            self.invoke(beam, source, target, finalize=True)
            self.assertEqual((target / 'parent-001/classes.png').stat().st_mtime_ns, mtime)
            groups = json.loads((target / 'groups.json').read_text())
            self.assertEqual([g['ParentId'] for g in groups], [1, 2, 3])
            self.assertEqual(groups[2]['FirstClass'], 5)
            np.testing.assert_array_equal(mrcfile.read(target / 'classes.mrcs'), np.concatenate([images1, images3]))
            rows = starfile.read(target / 'particles.star', always_dict=True)['particles']
            self.assertEqual(rows.rlnBeamParentClassNumber.tolist(), [1]*8 + [3]*4)
            self.assertEqual(rows.rlnClassNumber.tolist(), rows1.rlnClassNumber.tolist() + [5, 6, 7, 8])
            self.assertEqual(rows.rlnImageName.tolist(), rows1.rlnImageName.tolist() + rows3.rlnImageName.tolist())
            np.testing.assert_allclose(rows.rlnOriginXAngst, list(rows1.rlnOriginXAngst) + list(rows3.rlnOriginXAngst))
            model = starfile.read(target / 'model.star', always_dict=True)['model_classes']
            self.assertEqual(len(model), 8)
            self.assertEqual(model.rlnBeamParentClassNumber.tolist(), [1]*4 + [3]*4)
            self.assertEqual(model.rlnBeamSubclassNumber.tolist(), [1, 2, 3, 4]*2)
            self.assertAlmostEqual(model.rlnClassDistribution.sum(), 1.)
            self.assertTrue(model.rlnReferenceImage.iloc[-1].startswith('000008@'))

    def test_skipped_parents_do_not_create_empty_selection_resources(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory); source = root / 'source.json'; target = root / 'relay'
            source.write_text(json.dumps(dict(status='complete', parents=[dict(parent_class=1, status='skipped', reason='empty')])) )
            self.invoke(root / 'beam', source, target, finalize=True)
            self.assertFalse((target / 'particles.star').exists())
            self.assertEqual(json.loads((target / 'groups.json').read_text())[0]['Reason'], 'empty')
