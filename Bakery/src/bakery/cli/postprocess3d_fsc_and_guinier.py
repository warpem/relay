from pathlib import Path

import numpy as np
import starfile
import typer
from matplotlib import pyplot as plt

from ._cli import cli


@cli.command(no_args_is_help=True, help="line plot showing FSC curves from RELION postprocess")
def postprocess3d_fsc_and_guinier(
    postprocess_star_file: Path = typer.Option(...),
    output_file_fsc: Path = typer.Option(...),
    output_file_guinier: Path = typer.Option(...),
):
    # parse
    star = starfile.read(postprocess_star_file)
    df = star['fsc']

    # prepare data for resolution calculation
    resolution_inv = 1 / df['rlnAngstromResolution']
    # use corrected FSC for resolution calculation
    fsc_values = df['rlnFourierShellCorrelationCorrected']

    # plot...
    fig, ax = plt.subplots()

    # phase randomized
    ax.plot(
        resolution_inv,
        df['rlnCorrectedFourierShellCorrelationPhaseRandomizedMaskedMaps'],
        label='phase randomized',
        color='#eea9dd'  # seaborn colorblind pink
    )

    # unmasked
    ax.plot(
        resolution_inv,
        df['rlnFourierShellCorrelationUnmaskedMaps'],
        label='unmasked',
        color='#be5b1eff'  # seaborn colorblind reddish brown
    )

    # masked
    ax.plot(
        resolution_inv,
        df['rlnFourierShellCorrelationMaskedMaps'],
        label='masked',
        color='#66a8e1'  # seaborn colorblind light blue
    )

    # corrected
    ax.plot(
        resolution_inv,
        fsc_values,
        label='corrected',
        color='#3d916a',  # seaborn colorblind dark green
    )

    # add 0.143 threshold line
    ax.axhline(y=0.143, color='red', linestyle='--', alpha=0.7, linewidth=1)

    # find where FSC crosses 0.143 threshold
    threshold = 0.143
    crossing_resolution = None
    
    # find crossing point using linear interpolation on corrected FSC
    for i in range(len(fsc_values) - 1):
        y1, y2 = fsc_values.iloc[i], fsc_values.iloc[i + 1]
        x1, x2 = resolution_inv.iloc[i], resolution_inv.iloc[i + 1]
        
        # check if threshold is crossed between these two points
        if (y1 >= threshold >= y2) or (y1 <= threshold <= y2):
            # linear interpolation to find exact crossing point
            if y2 != y1:  # avoid division by zero
                x_cross = x1 + (threshold - y1) * (x2 - x1) / (y2 - y1)
                crossing_resolution = 1 / x_cross  # convert back to Angstroms
                break
    
    # calculate resolution for title
    if crossing_resolution is not None:
        resolution_text = f'{crossing_resolution:.1f}'
    else:
        # check if entire curve is below threshold
        if fsc_values.max() < threshold:
            resolution_text = 'Inf'
        else:
            # if no crossing found but curve goes above threshold, show highest resolution
            highest_res = df['rlnAngstromResolution'].min()
            resolution_text = f'{highest_res:.1f}'
    
    # add resolution as title
    ax.set_title(f'Resolution: {resolution_text}\u2009Å', fontsize=14, pad=10)

    # legend
    ax.legend()

    # set axis limits
    ax.set(
        ylim=[-0.1, 1.1]
    )

    # add axis labels
    ax.set(
        ylabel='Fourier shell correlation',
        xlabel='Resolution (1 / Å)',
    )

    # format ticklabels
    def format_xtick(x, pos):
        if abs(x) < 1e-6:
            label = 'DC'
        else:
            label = f'{1 / x:.2f}'
        return label

    ax.xaxis.set_major_formatter(format_xtick)

    # remove spines
    ax.spines['top'].set_visible(False)
    ax.spines['right'].set_visible(False)

    # write output file
    dpi = 300
    plt.tight_layout()
    plt.savefig(
        output_file_fsc,
        dpi=dpi,
        backend='agg'
    )


    # Now output the Guinier plots
    df = star['guinier']

    # plot...
    fig, ax = plt.subplots()

    # log amplitudes
    ax.plot(
        df['rlnResolutionSquared'],
        df['rlnLogAmplitudesOriginal'],
        label='unweighted',
        color='#eea9dd'  # seaborn colorblind pink
    )

    # MTF corrected (if available)
    if 'rlnLogAmplitudesMTFCorrected' in df.columns:
        ax.plot(
            df['rlnResolutionSquared'],
            df['rlnLogAmplitudesMTFCorrected'],
            label='MTF corrected',
            color='#be5b1eff'  # seaborn colorblind reddish brown
        )

    # masked
    ax.plot(
        df['rlnResolutionSquared'],
        df['rlnLogAmplitudesWeighted'],
        label='weighted',
        color='#66a8e1'  # seaborn colorblind light blue
    )

    # sharpened (if available)
    if 'rlnLogAmplitudesSharpened' in df.columns:
        ax.plot(
            df['rlnResolutionSquared'],
            df['rlnLogAmplitudesSharpened'],
            label='sharpened',
            color='#3d916a',  # seaborn colorblind dark green
        )

    # get B-factor from general data frame for title
    try:
        bfactor_data = star['general']['rlnBfactorUsedForSharpening']
        if hasattr(bfactor_data, 'iloc'):
            bfactor = bfactor_data.iloc[0]
        else:
            bfactor = bfactor_data
        title_text = f'B-factor: {bfactor:.1f}\u2009Å²'
    except (KeyError, IndexError, TypeError):
        title_text = 'Guinier plot'
    
    ax.set_title(title_text, fontsize=14, pad=10)

    # legend
    ax.legend()

    # add axis labels
    ax.set(
        ylabel='Natural Logarithm of Amplitude (ln)',
        xlabel='Resolution$^2$ (1 / Å$^2$)',
    )

    # ylim
    y_min, y_max = np.min(df['rlnLogAmplitudesOriginal']), np.max(df['rlnLogAmplitudesOriginal'])
    ax.set_ylim(y_min - 2.2, y_max + 1.1)

    # format ticklabels
    def format_xtick(x, pos):
        if abs(x) < 1e-6:
            label = 'DC'
        else:
            label = f'{1 / x:.2f}'
        return label

    ax.xaxis.set_major_formatter(format_xtick)

    # remove spines
    ax.spines['top'].set_visible(False)
    ax.spines['right'].set_visible(False)

    # write output file
    dpi = 300
    plt.tight_layout()
    plt.savefig(
        output_file_guinier,
        dpi=dpi,
        backend='agg'
    )
