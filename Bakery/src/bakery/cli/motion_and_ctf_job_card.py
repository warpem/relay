from pathlib import Path

import einops
import matplotlib.axes
import mrcfile
import numpy as np
import typer
from matplotlib import pyplot as plt

from bakery.cli._cli import cli
from bakery.motion_track_utils import (
    parse_motion_grid_from_json,
    expand_motion_grid,
    evaluate_motion_tracks
)
from bakery.image_utils import process_image_for_visualization
from bakery.ctf_utils import draw_ctf_fit_quality_panel


@cli.command(no_args_is_help=True)
def motion_and_ctf_job_card(
    motion_tracks_json_file: Path = typer.Option(...),
    motion_corrected_image_file: Path = typer.Option(...),
    frame_series_xml_file: Path = typer.Option(...),
    output_file: Path = typer.Option(...),
):
    fig, axs = plt.subplots(ncols=2, figsize=(2, 1))
    draw_motion_correction_panel(
        ax=axs[0],
        motion_corrected_image_file=motion_corrected_image_file,
        motion_tracks_json_file=motion_tracks_json_file
    )
    draw_ctf_fit_quality_panel(
        ax=axs[1],
        item_xml_file=frame_series_xml_file
    )
    plt.tight_layout(pad=0.1)
    fig.savefig(output_file, dpi=288)
    fig.savefig(str(output_file.with_suffix('.pdf')), dpi=288)


def draw_motion_correction_panel(
    ax: matplotlib.axes.Axes,
    motion_corrected_image_file: Path,
    motion_tracks_json_file: Path
):
    # read image with pixel size
    with mrcfile.open(motion_corrected_image_file) as mrc:
        image = mrc.data.astype(np.float32)
        pixel_size = mrc.voxel_size.x
    h, w = image.shape

    # process image with same method as import_fs_job_card
    image_processed = process_image_for_visualization(image, target_long_side=256)
    h_ds, w_ds = image_processed.shape
    
    # calculate scale factor for motion track scaling
    scale_factor = min(256 / h, 256 / w) if max(h, w) > 256 else 1.0

    # draw image with maintained aspect ratio, normalized to [0, 1]
    ax.imshow(image_processed, cmap="gray", origin="lower", vmin=0, vmax=1, aspect='equal', interpolation='sinc')

    # read motion grid data
    motion_grid = parse_motion_grid_from_json(motion_tracks_json_file)

    # make sure each dim in motion grid has at least four points for cubic interpolation
    motion_grid = expand_motion_grid(motion_grid)

    # evaluate motion tracks on 3x3 grid, accounting for original image dimensions
    # coordinate system for evaluation is [0, 1] covering each dim
    gh, gw = 3, 3
    margin_lower = 1 / 4
    margin_upper = 1 - margin_lower
    y, x = np.linspace(margin_lower, margin_upper, num=gh), np.linspace(margin_lower, margin_upper, num=gw)
    gy, gx = np.meshgrid(y, x, indexing='ij')
    grid_yx = einops.rearrange([gy, gx], 'yx h w -> h w yx')
    tracks = evaluate_motion_tracks(motion_grid, grid_yx, n_samples=100)  # (gh, gw, 100, 2)

    # draw tracks
    track_scale_factor = 25
    for i in range(gh):
        for j in range(gw):
            # calculate grid position in image pixel space
            interval = margin_upper - margin_lower
            step_y, step_x = interval / (gh - 1), interval / (gw - 1)
            y, x = margin_lower + i * step_y, margin_lower + j * step_x
            y, x = y * (h_ds - 1), x * (w_ds - 1)

            # get motion track points in pixels
            dy, dx = tracks[i, j, :, -2], tracks[i, j, :, -1]
            track_y, track_x = (
                y + (dy * pixel_size * scale_factor * track_scale_factor),
                x + (dx * pixel_size * scale_factor * track_scale_factor),
            )

            # plot line
            ax.plot(
                track_x, track_y,
                color='yellow',
                linewidth=0.5,
                solid_capstyle='round'
            )
    ax.axis('off')


