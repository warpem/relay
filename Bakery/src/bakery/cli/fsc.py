from enum import Enum
from pathlib import Path

import typer
from matplotlib import pyplot as plt

from ._cli import cli
from ..fsc.draw_fsc import draw_fsc
from ..fsc.fsc_data_model import FscData
from ..figure_export import save_figure


class InputType(Enum):
    relion_postprocess_star = 'relion_postprocess_star'
    relion_model_star = 'relion_model_star'


@cli.command(no_args_is_help=True, help="line plot showing FSC curves")
def fsc(
    metadata_file: Path = typer.Option(...),
    input_type: InputType = typer.Option(...),
    output_file: Path = typer.Option(...),
    svg: bool = False,
    directional_fsc_file: Path | None = None,
):
    if input_type == InputType.relion_postprocess_star:
        fsc_data = FscData.from_relion_postprocess_star(metadata_file)
    elif input_type == InputType.relion_model_star:
        fsc_data = FscData.from_relion_refine3d_model_star(metadata_file)
    else:
        raise NotImplementedError(input_type)

    # plot...
    fig, ax = plt.subplots(figsize=(6.4, 3.2), layout="constrained")
    draw_fsc(ax, data=fsc_data, directional_file=directional_fsc_file)

    # write output file
    dpi = 300
    save_figure(fig, output_file, svg=svg, dpi=dpi)
    plt.close(fig)
