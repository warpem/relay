import json
from io import BytesIO
from pathlib import Path

import imodmodel
import matplotlib.axes
import numpy as np
import typer
from matplotlib import pyplot as plt, ticker

from ._cli import cli
from ..image_utils import square_crop_rgba


@cli.command(no_args_is_help=True, help="job card for ts_etomo job types")
def ts_etomo_job_card(
    tilt_image_file: Path = typer.Option(..., help="png image of tilt to show"),
    fiducial_model_file: Path = typer.Option(..., help=".fid for tilt series of tilt-image-file"),
    tilt_image_index: int = typer.Option(..., help="index of tilt image"),
    processed_items_json_file: Path = typer.Option(...),
    output_file: Path = typer.Option(...),
):
    # parse json file for FSC curve
    with open(processed_items_json_file, "r") as f:
        data = json.load(f)

    # plot...
    fig, axs = plt.subplot_mosaic(
        mosaic=[["image", "scatters", "scatters"]],
        figsize=(3, 1),
    )

    # draw fiducials on tilt image
    _draw_slice_with_fiducials(
        ax=axs["image"],
        tilt_image_file=tilt_image_file,
        fiducial_model_file=fiducial_model_file,
        tilt_image_index=tilt_image_index
    )

    # scatters
    scatter_image = _get_scatter_plot_image(data)
    axs["scatters"].imshow(scatter_image)
    axs["scatters"].set_axis_off()

    # write output file
    dpi = 2 * 144
    fig.tight_layout(pad=0.1)
    fig.savefig(output_file, dpi=dpi)
    plt.close(fig)

    # write PDF with vector scatter plots
    pdf_fig, pdf_axs = plt.subplot_mosaic(
        mosaic=[["image", "image", "dy"],
                ["image", "image", "dx"]],
        figsize=(9, 3),
    )
    _draw_slice_with_fiducials(
        ax=pdf_axs["image"],
        tilt_image_file=tilt_image_file,
        fiducial_model_file=fiducial_model_file,
        tilt_image_index=tilt_image_index
    )
    _draw_shift_scatter_plots(pdf_axs, data)
    pdf_fig.tight_layout(pad=0.5, h_pad=0.3)
    pdf_fig.savefig(str(output_file.with_suffix('.pdf')))
    plt.close(pdf_fig)


def _draw_slice_with_fiducials(
    ax: matplotlib.axes.Axes,
    tilt_image_file: Path,
    fiducial_model_file: Path,
    tilt_image_index: int,
):
    # parse image
    tilt_image = plt.imread(tilt_image_file)
    tilt_image, bottom, left = square_crop_rgba(tilt_image)

    # plot image
    ax.imshow(tilt_image, origin="lower", cmap="gray", interpolation='sinc')

    # parse imod model file
    df = imodmodel.read(fiducial_model_file)

    # grab coords in slice
    idx = df['z'].to_numpy(dtype=int) == tilt_image_index
    xy = df[['x', 'y']].iloc[idx].to_numpy()
    x = xy[:, 0] - left
    y = xy[:, 1] - bottom

    # plot coords
    ax.scatter(x=x, y=y, c="yellow", s=0.25)

    # explicitly set x/y limits
    h, w = tilt_image.shape[:-1]
    ax.set_xlim([0, w])
    ax.set_ylim([0, h])

    # remove axes, ticks and tick labels
    ax.set_axis_off()


def _get_scatter_plot_image(json_data):
    # create figure and buffer to render to
    fig, axs = plt.subplot_mosaic(
        mosaic=[["dy"],
                ["dx"]],
        figsize=(2, 1)
    )
    buffer = BytesIO()

    # draw plots
    _draw_shift_scatter_plots(axs, json_data)

    # tight layout
    fig.tight_layout(pad=0.2, h_pad=0.2)

    # save to buffer
    fig.savefig(buffer, format='png', dpi=288)
    plt.close(fig)

    # read buffer into numpy array
    buffer.seek(0)
    scatter_image = plt.imread(buffer, format='png')

    return scatter_image


def _draw_shift_scatter_plots(axs, json_data):
    """Draw shift scatter plots on axes dict with 'dy' and 'dx' keys."""
    # extract data
    min_shift_x_nm = np.array([entry["MinShiftX"] for entry in json_data]) / 10
    mean_shift_x_nm = np.array([entry["MeanShiftX"] for entry in json_data]) / 10
    max_shift_x_nm = np.array([entry["MaxShiftX"] for entry in json_data]) / 10

    min_shift_y_nm = np.array([entry["MinShiftY"] for entry in json_data]) / 10
    mean_shift_y_nm = np.array([entry["MeanShiftY"] for entry in json_data]) / 10
    max_shift_y_nm = np.array([entry["MaxShiftY"] for entry in json_data]) / 10

    n = len(mean_shift_x_nm)

    # scatter min=IndianRed, mean=YellowGreen, and max=RoyalBlue
    axs["dy"].scatter(x=np.arange(n), y=min_shift_y_nm, c="indianred", s=1)
    axs["dy"].scatter(x=np.arange(n), y=mean_shift_y_nm, c="yellowgreen", s=1)
    axs["dy"].scatter(x=np.arange(n), y=max_shift_y_nm, c="royalblue", s=1)

    axs["dx"].scatter(x=np.arange(n), y=min_shift_x_nm, c="indianred", s=1)
    axs["dx"].scatter(x=np.arange(n), y=mean_shift_x_nm, c="yellowgreen", s=1)
    axs["dx"].scatter(x=np.arange(n), y=max_shift_x_nm, c="royalblue", s=1)

    # set y limits
    y_limits_min = -1
    y_data_max = np.max(np.concatenate([max_shift_y_nm, max_shift_x_nm]))
    y_limits_max = 1.1 * y_data_max

    # Remove spines (the box around the plot)
    for ax in (axs["dy"], axs["dx"]):
        # set axis limits
        ax.set_ylim(y_limits_min, y_limits_max)

        # set yticks
        ax.set_yticks([0, y_data_max])
        labels = ax.get_yticks().tolist()
        ax.set_yticklabels(labels, fontsize=4)
        ax.tick_params(pad=1)  # Reduce padding between ticks and labels (default is 4)
        ax.tick_params(size=2)  # make ticks smaller
        formatter = ticker.FormatStrFormatter('%.0f')
        ax.yaxis.set_major_formatter(formatter)

        # remove spines
        ax.spines['top'].set_visible(False)
        ax.spines['right'].set_visible(False)

        # Remove xticks/labels
        ax.set_xticks([])
        ax.set_xticklabels([])

    # add titles
    axs["dy"].set_title("x-axis shifts (nm)", fontsize=4, pad=0)
    axs["dx"].set_title("y-axis shifts (nm)", fontsize=4, pad=0)
