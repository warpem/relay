from pathlib import Path

import starfile
import typer
from matplotlib import pyplot as plt

from ._cli import cli
from .m_refine_job_card import _draw_fsc_on_axes, _read_global_resolution


@cli.command(no_args_is_help=True, help="FSC curves for an M species expanded view")
def m_species_fsc(
    fsc_star_file: Path = typer.Option(...),
    species_xml_file: Path = typer.Option(...),
    output_file: Path = typer.Option(...),
):
    data = starfile.read(fsc_star_file)
    resolution = _read_global_resolution(species_xml_file)

    fig, ax = plt.subplots(figsize=(6, 4))
    _draw_fsc_on_axes(ax, data, global_resolution=resolution)
    ax.set(xlabel='Resolution (Å)', ylabel='Fourier shell correlation')
    ax.legend(loc='lower left')
    fig.tight_layout()
    fig.savefig(output_file, dpi=300)
    fig.savefig(output_file.with_suffix('.pdf'))
    plt.close(fig)
