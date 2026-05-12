from pathlib import Path

import einops
import mrcfile
import numpy as np
import pandas as pd
import starfile
import typer
from matplotlib import pyplot as plt

from ._cli import cli


@cli.command(no_args_is_help=True, help="atlas image of n particles from STAR files")
def particle_image_atlas(
    input_star_file: list[Path] = typer.Option(..., help="supply this option multiple times to pass multiple files"),
    n_images: int = typer.Option(...),
    output_file: Path = typer.Option(...)
):
    # read rlnImageName from all files and concatenate
    image_name_columns = [
        starfile.read(star, always_dict=True)["particles"]["rlnImageName"]
        for star
        in input_star_file
    ]
    image_names = pd.concat(image_name_columns)

    # take random subset
    rng = np.random.default_rng(seed=44)
    image_names = image_names.iloc[rng.choice(len(image_names), size=n_images)]

    # split into index and path
    df = image_names.str.split('@', expand=True)
    df.columns = ['index', 'path']
    df['index'] = df['index'].astype(int) - 1

    # construct a mapping from each image file to a memory mapped array
    # containing particle image data
    unique_image_files = df['path'].unique()
    path_to_image = {
        image_file: mrcfile.mmap(image_file, mode='r').data
        for image_file
        in unique_image_files
    }

    # get batch of images and transform into to one big images with individual images
    # arranged in a row
    particle_images = [
        path_to_image[image['path']][image['index']]
        for _, image
        in df.iterrows()
    ]
    particle_image_atlas = einops.rearrange(particle_images, 'b h w -> h (b w)')

    # render, ensuring that 1px in image is 1px in rendered output
    fig, ax = plt.subplots()
    ax.imshow(particle_image_atlas, cmap='gray', interpolation='sinc', origin='lower')
    ax.axis('off')
    fig.subplots_adjust(left=0, bottom=0, right=1, top=1)
    h, w = particle_image_atlas.shape
    fig.set_size_inches(h=h / fig.dpi, w=w / fig.dpi)
    plt.savefig(output_file, bbox_inches='tight', pad_inches=0, dpi=fig.dpi)
