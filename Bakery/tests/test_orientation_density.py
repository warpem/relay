"""Angular plots report density, including the clipped equatorial cells."""
import importlib

import h3
import numpy as np
import pytest

module = importlib.import_module('bakery.cli.orientation_and_fourier_sampling')


@pytest.mark.parametrize('resolution', [0, 1, 2])
def test_hemisphere_areas_partition_two_pi_and_match_full_interior_cells(resolution):
    cells = [c for root in h3.get_res0_cells() for c in h3.cell_to_children(root, resolution)]
    areas = module.hemisphere_cell_areas(tuple(cells))
    assert areas.sum() == pytest.approx(2*np.pi, abs=1e-10)
    for cell, area in zip(cells, areas):
        latitudes = np.array(h3.cell_to_boundary(cell))[:, 0]
        if (latitudes >= 0).all():
            assert area == pytest.approx(h3.cell_area(cell, unit='rads^2'), rel=1e-10)
        elif (latitudes < 0).all():
            assert area == 0


def test_uniform_density_is_uniform_interior_and_equator_with_zeros_preserved():
    cells, inverse, tilt = module.projection_grid(1)
    areas = module.hemisphere_cell_areas(tuple(cells))
    image = module.generate_orientation_distribution_image(areas*123, inverse, tilt, cells)
    np.testing.assert_allclose(image.compressed(), 123, rtol=1e-12)
    empty = module.generate_orientation_distribution_image(areas*0, inverse, tilt, cells)
    assert not empty.compressed().any()


def test_isotropic_monte_carlo_has_only_counting_noise_after_area_normalization():
    from bakery.spherical_hexbin.utils import xyz_to_cell
    rng = np.random.default_rng(998)
    v = rng.normal(size=(200000, 3))
    v /= np.linalg.norm(v, axis=1, keepdims=True)
    v = np.where(v[:, 2:3] < 0, -v, v)
    counts = module.count_particles_per_h3_cell(xyz_to_cell(v, res=1))
    cells, inverse, tilt = module.projection_grid(1)
    areas = module.hemisphere_cell_areas(tuple(cells))
    observed = np.array([counts.get(c, 0) for c in cells])
    expected = len(v)*areas/(2*np.pi)
    keep = expected > 20
    reduced_chisq = np.mean((observed[keep]-expected[keep])**2/expected[keep])
    assert .7 < reduced_chisq < 1.3


@pytest.mark.parametrize('symmetry', ['C1', 'D2', 'T'])
def test_symmetrized_directions_stay_in_plotted_hemisphere(tmp_path, monkeypatch, symmetry):
    import pandas as pd
    import starfile
    from bakery.spherical_hexbin.utils import xyz_to_cell as real_xyz_to_cell
    path = tmp_path / 'particles.star'
    starfile.write({'particles': pd.DataFrame({
        'rlnAngleRot': [23., 117., 246.], 'rlnAngleTilt': [20., 60., 110.],
    })}, path)
    captured = []
    def cells(vectors, res):
        captured.append(vectors)
        return real_xyz_to_cell(vectors, res=res)
    monkeypatch.setattr(module, 'xyz_to_cell', cells)
    module.orientation_and_fourier_sampling(
        path, 0, symmetry, 'viridis', module.InputType.RELION_STAR_FILE,
        output_orientation_file=None, output_fourier_sampling_file=None,
        projection_directions_file=None, class_number=None,
    )
    assert len(captured) == 1
    assert (captured[0][..., 2] >= 0).all()
