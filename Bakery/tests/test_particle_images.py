import mrcfile
import numpy as np
import pandas as pd
import pytest
import starfile
from matplotlib import pyplot as plt

from bakery.cli.import_particles_job_card import import_particles_job_card
from bakery.cli.particle_image_atlas import particle_image_atlas
from bakery.particle_images import read_particle_images


@pytest.fixture
def particle_inputs(tmp_path):
    single = np.arange(64, dtype=np.float32).reshape(8, 8)
    multiple = np.stack([single + 100, single + 200])
    mrcfile.write(tmp_path / 'single.mrc', single)
    mrcfile.write(tmp_path / 'multiple.mrcs', multiple)
    names = [f'1@{tmp_path / "single.mrc"}',
             f'2@{tmp_path / "multiple.mrcs"}',
             f'1@{tmp_path / "multiple.mrcs"}']
    star = tmp_path / 'particles.star'
    starfile.write({'particles': pd.DataFrame({'rlnImageName': names})}, star)
    return names, star, [single, multiple[1], multiple[0]]


def test_mixed_single_image_and_multi_image_stacks_keep_full_planes(particle_inputs):
    names, _, expected = particle_inputs
    images = read_particle_images(names + [names[0]])
    for actual, wanted in zip(images, expected + [expected[0]]):
        np.testing.assert_array_equal(actual, wanted)
        assert actual.shape == (8, 8)


@pytest.mark.parametrize('command', ['card', 'atlas'])
def test_import_previews_render_mixed_stack_depths(particle_inputs, tmp_path, command):
    _, star, _ = particle_inputs
    output = tmp_path / f'{command}.png'
    try:
        if command == 'card':
            import_particles_job_card([star], output)
        else:
            particle_image_atlas([star], 20, output)
        assert output.stat().st_size > 0
    finally:
        plt.close('all')


def test_single_image_rejects_a_second_particle_index(particle_inputs):
    names, _, _ = particle_inputs
    with pytest.raises(ValueError, match='particle index 2 exceeds stack bounds'):
        read_particle_images([names[0].replace('1@', '2@', 1)])
