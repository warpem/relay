from pathlib import Path
from typing import Annotated, List

import typer

from bakery.cli._cli import cli
from bakery.cli.class3d_job_card import class3d_job_card


@cli.command(no_args_is_help=True)
def initial_reference_3d_job_card(
    volume_file: List[Path] = typer.Option(...),
    class_number: List[int] = typer.Option(...),
    output_file: Path = typer.Option(...),
    model_star_file: Annotated[Path | None, typer.Option()] = None,
):
    class3d_job_card(volume_file, class_number, output_file, model_star_file)
