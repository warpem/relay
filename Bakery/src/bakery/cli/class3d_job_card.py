from pathlib import Path
from typing import Annotated, List

import matplotlib.axes
import numpy as np
import typer
import starfile
from matplotlib import pyplot as plt

from bakery.cli._cli import cli
from bakery.map_preview import read_z_projection, draw_map_preview


def class_grid(n_classes: int) -> tuple[int, int]:
    """Rows/columns matching Relay's Class3DCardLayout (20-class preview cap)."""
    if n_classes < 1:
        raise ValueError('At least one volume is required.')
    n_classes = min(n_classes, 20)
    return (1, max(2, n_classes)) if n_classes <= 5 else (2, 2 * ((n_classes + 3) // 4))


def class_resolutions(model_star_file: Path | None, class_numbers: List[int]):
    if model_star_file is None or not model_star_file.is_file():
        return [None] * len(class_numbers)
    table = starfile.read(model_star_file, always_dict=True).get('model_classes')
    if table is None or 'rlnEstimatedResolution' not in table:
        return [None] * len(class_numbers)
    result = []
    for number in class_numbers:
        value = table.iloc[number - 1]['rlnEstimatedResolution'] if 1 <= number <= len(table) else None
        try:
            value = float(value)
        except (TypeError, ValueError):
            value = None
        result.append(value if value is not None and np.isfinite(value) and value > 0 else None)
    return result


@cli.command(no_args_is_help=True)
def class3d_job_card(
    volume_file: List[Path] = typer.Option(...),
    class_number: List[int] = typer.Option(...),
    output_file: Path = typer.Option(...),
    model_star_file: Annotated[Path | None, typer.Option()] = None,
):
    # figure out number of rows/cols
    n_volumes = len(volume_file)

    if n_volumes != len(class_number):
        raise ValueError('Number of volumes does not match the number of class numbers provided.')

    if n_volumes > 20:
        volume_file = volume_file[:20]
        n_volumes = 20

    n_rows, n_cols = class_grid(n_volumes)
    resolutions = class_resolutions(model_star_file, class_number[:n_volumes])

    aspect_ratio = n_cols / n_rows

    # take volume z projections
    volume_projections = []
    for file in volume_file:
        volume_projections.append(read_z_projection(file))

    # setup plot
    fig, axs = plt.subplots(nrows=n_rows, ncols=n_cols, figsize=(aspect_ratio, 1))

    # ensure axs is a flat array of Axes
    if isinstance(axs, matplotlib.axes.Axes):
        axs = np.array([axs])
    axs = axs.reshape(-1)

    # draw each projection
    for i in range(n_rows):
        for j in range(n_cols):
            idx = (i * n_cols) + j
            if idx <= n_volumes - 1:
                draw_z_slice_panel(
                    ax=axs[idx],
                    image=volume_projections[idx],
                    label=class_number[idx],
                    n_rows=n_rows,
                    resolution=resolutions[idx]
                )
            axs[idx].axis('off')
    plt.tight_layout(pad=0.1)  # avoids stray white pixels at edges
    fig.savefig(output_file, dpi=288, transparent=True)
    fig.savefig(str(output_file.with_suffix('.pdf')), dpi=288, transparent=True)
    plt.close(fig)


def draw_z_slice_panel(
    ax: matplotlib.axes.Axes,
    image: np.ndarray,
    label: str,
    n_rows: int,
    resolution: float | None = None,
):
    draw_map_preview(ax, image)

    # add label
    if n_rows == 1:
        x, y = 0.04, 0.90
    elif n_rows == 2:
        x, y = 0.06, 0.82
    else:
        raise ValueError()
    ax.text(
        x=x, y=y,
        s=label,
        color="white",
        fontsize=6,
        transform=ax.transAxes  # Specify that coordinates are in fractional space
    )

    if resolution is not None:
        ax.text(
            0.96, 0.04, f'{resolution:.1f} Å',
            ha='right', va='bottom', color='white', fontsize=6,
            transform=ax.transAxes,
        )
