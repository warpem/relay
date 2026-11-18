"""Per-class entry point sharing geometry and STAR data with single-map plots."""
from pathlib import Path
from typing import Optional
import typer
from ._cli import cli
from .orientation_and_fourier_sampling import orientation_and_fourier_sampling, InputType


@cli.command(no_args_is_help=True)
def orientation_and_fourier_sampling_class3d(
    particle_star_file: Path = typer.Option(...),
    n_classes: int = typer.Option(...),
    grid_resolution: int = typer.Option(...),
    symmetry: Optional[str] = typer.Option(None),
    colormap: str = typer.Option(...),
    output_orientation_file: Optional[Path] = typer.Option(None),
    output_fourier_sampling_file: Optional[Path] = typer.Option(None),
):
    for number in range(1, n_classes + 1):
        def output(path):
            return path.with_stem(path.stem + f'_class{number:03d}') if path is not None else None
        orientation_and_fourier_sampling(
            particles_file=particle_star_file, grid_resolution=grid_resolution,
            symmetry=symmetry, colormap=colormap, input_type=InputType.RELION_STAR_FILE,
            output_orientation_file=output(output_orientation_file),
            output_fourier_sampling_file=output(output_fourier_sampling_file),
            svg=False, projection_directions_file=None, class_number=number,
        )
