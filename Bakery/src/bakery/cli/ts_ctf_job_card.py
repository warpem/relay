import json
from io import BytesIO
from pathlib import Path

import matplotlib.axes
import numpy as np
import typer
from matplotlib import pyplot as plt
import matplotlib.ticker as ticker

from bakery.ctf_utils import draw_ctf_fit_quality_panel, get_ctf_1d, CTFParameters
from ._cli import cli


@cli.command(no_args_is_help=True, help="job card for ts_ctf job type")
def ts_ctf_job_card(
    tilt_series_xml_file: Path = typer.Option(..., help="tilt series xml"),
    processed_items_json_file: Path = typer.Option(...),
    output_file: Path = typer.Option(...),
):
    # parse json file for distribution data
    with open(processed_items_json_file, "r") as f:
        data = json.load(f)

    # get pixel size from single ts xml, assume same for all tilt series
    pixel_size = CTFParameters.from_warp_xml(tilt_series_xml_file).pixel_size

    # create plot...
    fig, axs = plt.subplot_mosaic(
        mosaic=[["ctf", "scatters"]],
        figsize=(2, 1),
    )

    # CTF for one tilt series
    draw_ctf_fit_quality_panel(axs["ctf"], tilt_series_xml_file)

    # scatter plots with min/mean/max defocus per series and |CTF| for all series
    # plots are pre-rendered as an image to ensure spacing between plots
    scatter_image = _get_scatter_plots_image(data, tilt_series_xml_file)
    axs["scatters"].imshow(scatter_image, interpolation='sinc')
    axs["scatters"].set_axis_off()

    # write output file
    dpi = 2 * 144
    fig.tight_layout(pad=0.1)
    fig.savefig(output_file, dpi=dpi)
    plt.close(fig)

    # write PDF with vector scatter plots
    pdf_fig, pdf_axs = plt.subplot_mosaic(
        mosaic=[["ctf", "defocus-scatter"],
                ["ctf", "|CTF|-line"]],
        figsize=(8, 4),
    )
    draw_ctf_fit_quality_panel(pdf_axs["ctf"], tilt_series_xml_file)
    _draw_defocus_scatter_plot(pdf_axs["defocus-scatter"], data)
    _draw_sum_abs_ctf_plot(pdf_axs["|CTF|-line"], data, CTFParameters.from_warp_xml(tilt_series_xml_file))
    pdf_fig.tight_layout(pad=0.5, h_pad=0.3)
    pdf_fig.savefig(str(output_file.with_suffix('.pdf')))
    plt.close(pdf_fig)


def _get_scatter_plots_image(json_data, ts_xml_file):
    # create figure and buffer to render to
    fig, axs = plt.subplot_mosaic(
        mosaic=[["defocus-scatter"],
                ["|CTF|-line"]],
        figsize=(1, 1)
    )
    buffer = BytesIO()

    # draw plots
    _draw_defocus_scatter_plot(axs["defocus-scatter"], json_data)
    _draw_sum_abs_ctf_plot(axs["|CTF|-line"], json_data, CTFParameters.from_warp_xml(ts_xml_file))

    # tight layout with vertical spacing
    fig.tight_layout(pad=0.2, h_pad=0.1)

    # save to buffer
    fig.savefig(buffer, format='png', dpi=288)
    plt.close(fig)

    # read buffer into numpy array
    buffer.seek(0)
    scatter_image = plt.imread(buffer, format='png')

    return scatter_image


def _draw_defocus_scatter_plot(ax, json_data):
    # extract data
    min_defocus_per_series = [entry["MinDef"] for entry in json_data]
    mean_defocus_per_series = [entry["MeanDef"] for entry in json_data]
    max_defocus_per_series = [entry["MaxDef"] for entry in json_data]
    n = len(mean_defocus_per_series)

    # scatter min=IndianRed, mean=YellowGreen, and max=RoyalBlue
    ax.scatter(x=np.arange(n), y=min_defocus_per_series, c="indianred", s=1)
    ax.scatter(x=np.arange(n), y=mean_defocus_per_series, c="yellowgreen", s=1)
    ax.scatter(x=np.arange(n), y=max_defocus_per_series, c="royalblue", s=1)

    # set ylim
    min_defocus = np.min(min_defocus_per_series)
    max_defocus = np.max(max_defocus_per_series)
    y_min = min_defocus - 0.1 * (max_defocus - min_defocus)
    y_max = max_defocus + 0.1 * (max_defocus - min_defocus)
    ax.set_ylim(y_min, y_max)

    # Remove xticks and xticklabels
    ax.set_xticks([])
    ax.set_xticklabels([])

    # set yticks
    ax.set_yticks([0, np.max(max_defocus_per_series)])
    labels = ax.get_yticks().tolist()
    ax.set_yticklabels(labels, fontsize=4)
    formatter = ticker.FormatStrFormatter('%.1f')
    ax.yaxis.set_major_formatter(formatter)
    ax.tick_params(pad=1)  # Reduce padding between ticks and labels (default is 4)
    ax.tick_params(size=2)  # make ticks smaller

    # add title
    ax.set_title("defocus (µm)", fontsize=4, pad=0)

    # Remove spines (the box around the plot)
    ax.spines['top'].set_visible(False)
    ax.spines['right'].set_visible(False)




def _draw_sum_abs_ctf_plot(ax: matplotlib.axes.Axes, json_data, ctf_parameters: CTFParameters):
    # simulate CTF at defocus for all series
    defoci = [entry["Def"] for entry in json_data]
    ctfs = [
        get_ctf_1d(
            num_elements=4096,
            pixel_size=ctf_parameters.pixel_size,
            defocus=defocus,
            amplitude=ctf_parameters.amplitude,
            cs=ctf_parameters.cs,
            voltage=ctf_parameters.voltage,
            phase_shift=ctf_parameters.phase_shift
        )
        for defocus
        in defoci
    ]

    # get mean of abs ctfs for plot
    abs_ctf = np.mean(np.abs(np.stack(ctfs, axis=0)), axis=0)

    # plot
    ax.set_title("|CTF|", fontsize=4, pad=0)
    ax.plot(abs_ctf, color='black', linewidth=0.25)
    ax.set_ylim([0, 1.1])
    ax.set_yticks([0, 1.0])
    ax.set_xticks([])
    ax.set_xticklabels([])
    labels = ax.get_yticks().tolist()
    ax.set_yticklabels(labels, fontsize=4)
    ax.tick_params(pad=1)  # Reduce padding between ticks and labels (default is 4)
    ax.tick_params(size=2)  # make ticks smaller

    ax.spines["top"].set_visible(False)
    ax.spines["right"].set_visible(False)


TS_XML_FILE = "/Users/burta2/programming/relay2/Bakery/TS_1.xml"
