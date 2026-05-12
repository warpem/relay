from pathlib import Path

import matplotlib.axes
import mrcfile
import numpy as np
import starfile
import typer
from matplotlib import pyplot as plt

from bakery.cli._cli import cli
from bakery.image_utils import fourier_crop_square_image, square_crop, normalize_central_50_percent


@cli.command(no_args_is_help=True)
def boxnet_inference_2d_job_card(
    motion_corrected_image_file_1: Path = typer.Option(...),
    particle_star_file_1: Path = typer.Option(...),
    motion_corrected_image_file_2: Path = typer.Option(...),
    particle_star_file_2: Path = typer.Option(...),
    output_file: Path = typer.Option(...),
):
    fig, axs = plt.subplots(ncols=2, figsize=(2, 1))
    draw_picking_panel(
        ax=axs[0],
        motion_corrected_image_file=motion_corrected_image_file_1,
        particle_star_file=particle_star_file_1
    )
    draw_picking_panel(
        ax=axs[1],
        motion_corrected_image_file=motion_corrected_image_file_2,
        particle_star_file=particle_star_file_2
    )
    plt.tight_layout(pad=0.1)
    fig.savefig(output_file, dpi=288)
    fig.savefig(str(output_file.with_suffix('.pdf')), dpi=288)


def draw_picking_panel(
    ax: matplotlib.axes.Axes,
    motion_corrected_image_file: Path,
    particle_star_file: Path
):
    # read image with pixel size
    with mrcfile.open(motion_corrected_image_file) as mrc:
        image = mrc.data
        pixel_size = mrc.voxel_size.x
    h, w = image.shape

    # crop to square and downsample
    image, dh, dw = square_crop(image)
    h_sq, w_sq = image.shape
    image = fourier_crop_square_image(image=image, target_sidelength=144 * 2)
    h_sq_ds, w_sq_ds = image.shape

    # normalize
    image = normalize_central_50_percent(image)

    # draw image
    ax.imshow(image, cmap="gray", origin="lower", vmin=-2, vmax=2, interpolation='sinc')

    # read particle data
    df = starfile.read(particle_star_file)
    yx = df[['rlnCoordinateY', 'rlnCoordinateX']].to_numpy()
    yx = (yx - np.asarray([dh, dw])) * (h_sq_ds / h_sq)

    # draw particles
    ax.scatter(
        x=yx[:, -1], y=yx[:, -2],
        s=10,
        marker='o',
        facecolors='none',
        edgecolors='yellow',
        linewidths=0.5,
    )

    # explicitly set limits to image dimensions
    ax.set(xlim=(0, image.shape[-1]), ylim=(0, image.shape[-2]))

    # turn off axis
    ax.axis('off')
