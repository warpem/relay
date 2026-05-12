from pathlib import Path

import starfile
import typer
from matplotlib import pyplot as plt

from ._cli import cli


@cli.command(no_args_is_help=True, help="line plot showing FSC curves")
def class3d_fsc_per_class(
    input_file: Path = typer.Option(..., help='model.star file from RELION Class3D'),
    output_file: Path = typer.Option(...)
):
    # parse input
    star = starfile.read(input_file)

    # find per class data tables
    class_to_table = {
        int(k.lstrip('model_class_')): v
        for k, v
        in star.items()
        if k.startswith('model_class_')
    }

    # plot, per class
    for class_number, df in class_to_table.items():
        # construct output filename
        suffix = f'_class{class_number:03d}'
        per_class_output_file = output_file.with_stem(output_file.stem + suffix)

        # calculate pseudo-FSC from SSNR estimate
        # SSNR=2*FSC/(1-FSC) (Unser, M., et.al. (1987), Ultramicroscopy 23, 39–51)
        fsc = df['rlnSsnrMap'] / (2 + df['rlnSsnrMap'])

        # plot
        plt.rcParams.update({'font.size': 20})
        fig, ax = plt.subplots()
        ax.plot(1 / df['rlnAngstromResolution'], fsc, color='black')

        # format the x axis labels
        def format_xtick(x, pos):
            if abs(x) < 1e-6:
                label = 'DC'
            else:
                label = f'{1 / x:.2f}'
            return label
        ax.xaxis.set_major_formatter(format_xtick)

        # add titles to axes and set y-axis limits
        ax.set(
            ylabel='FSC',
            xlabel='Resolution (1 / Å)',
            ylim=[-0.1, 1.1]
        )

        # remove unneeded spines
        ax.spines['top'].set_visible(False)
        ax.spines['right'].set_visible(False)

        # write output file
        dpi = 100
        plt.tight_layout(pad=0.8)
        plt.savefig(
            per_class_output_file,
            dpi=dpi,
        )
