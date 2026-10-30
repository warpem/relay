import matplotlib.pyplot
import numpy as np
import pandas as pd
from pathlib import Path

from .fsc_data_model import FscData


def draw_fsc(
    ax: matplotlib.pyplot.Axes,
    data: FscData,
    directional_file: Path | None = None,
) -> matplotlib.pyplot.Axes:
    # prepare data
    resolution_inv = 1 / data.resolution
    fsc_values = data.fsc

    if directional_file is not None:
        # Beam publishes one FSC per direction and one column per shell. Match
        # shell indices, not the rounded Angstrom values in CSV column labels.
        frame = pd.read_csv(directional_file)
        columns = [c for c in frame if c.startswith('s') and c[1:].split('_')[0].isdigit()]
        indices = np.array([int(c[1:].split('_')[0]) for c in columns])
        valid = (indices > 0) & (indices < len(resolution_inv))
        values = frame.loc[:, np.array(columns)[valid]].to_numpy(dtype=float)
        if values.size:
            finite_columns = np.isfinite(values).any(axis=0)
            band = np.nanquantile(values[:, finite_columns], [.1, .9], axis=0)
            ax.fill_between(resolution_inv[indices[valid][finite_columns]], *band,
                            color='#9467bd', alpha=.25, linewidth=0, label='Directional 10–90%')
    
    # plot the FSC curve
    ax.plot(resolution_inv, fsc_values, color='black',
            label='Gold standard' if data.fsc_unmasked is None else
                  ('Corrected' if data.fsc_masked is not None else 'Unmasked'))
    if data.fsc_masked is not None:
        ax.plot(resolution_inv, data.fsc_unmasked, label='Unmasked', alpha=0.65)
        ax.plot(resolution_inv, data.fsc_masked, label='Masked', alpha=0.65)
        if data.fsc_phase_randomized is not None:
            ax.plot(resolution_inv, data.fsc_phase_randomized, label='Phase randomized', alpha=0.65)
        ax.legend(fontsize=10)

    # add 0.143 threshold line
    ax.axhline(y=0.143, color='red', linestyle='--', alpha=0.7, linewidth=1)

    # find where FSC crosses 0.143 threshold
    threshold = 0.143
    crossing_resolution = None
    
    # find crossing point using linear interpolation
    for i in range(len(fsc_values) - 1):
        y1, y2 = fsc_values[i], fsc_values[i + 1]
        x1, x2 = resolution_inv[i], resolution_inv[i + 1]
        
        # check if threshold is crossed between these two points
        if (y1 >= threshold >= y2) or (y1 <= threshold <= y2):
            # linear interpolation to find exact crossing point
            if y2 != y1:  # avoid division by zero
                x_cross = x1 + (threshold - y1) * (x2 - x1) / (y2 - y1)
                crossing_resolution = 1 / x_cross  # convert back to Angstroms
                break
    
    # calculate resolution for title
    if crossing_resolution is not None:
        resolution_text = f'{crossing_resolution:.2f}' if crossing_resolution < 2 else f'{crossing_resolution:.1f}'
    else:
        # check if entire curve is below threshold
        if fsc_values.max() < threshold:
            resolution_text = 'Inf'
        else:
            # if no crossing found but curve goes above threshold, show highest resolution
            highest_res = data.resolution.min()
            resolution_text = f'{highest_res:.1f}'
    
    # add resolution as title
    if data.fsc_unmasked is not None:
        # Beam's controller has its own supported-band/crossing policy. Use its
        # published estimate rather than selecting a different crossing here.
        resolution_text = ((f'{data.estimated_resolution:.2f}' if data.estimated_resolution < 2 else f'{data.estimated_resolution:.1f}')
                           if data.estimated_resolution is not None else 'unresolved')
    ax.set_title(f'Resolution: {resolution_text}\u2009Å', fontsize=14, pad=10)
    if directional_file is not None and data.directional_best is not None and data.directional_worst is not None:
        summary = f'Directional: {data.directional_best:.2f}–{data.directional_worst:.2f} Å'
        if data.cfar is not None:
            summary += f' · cFAR {data.cfar:.3f}'
        ax.text(.02, .025, summary, transform=ax.transAxes, fontsize=10)

    # format the x axis labels
    def format_xtick(x, pos):
        if abs(x) < 1e-6:
            label = 'DC'
        else:
            label = f'{1 / x:.2f}'
        return label

    ax.xaxis.set_major_formatter(format_xtick)

    # add titles to axes
    ax.set(
        ylabel='Fourier shell correlation',
        xlabel='Resolution (1 / Å)',
    )

    # set ylim
    ax.set(ylim=[-0.1, 1.1])

    # remove unneeded spines
    ax.spines['top'].set_visible(False)
    ax.spines['right'].set_visible(False)
    return ax
