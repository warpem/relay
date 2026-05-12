import matplotlib.pyplot

from .fsc_data_model import FscData


def draw_fsc(
    ax: matplotlib.pyplot.Axes,
    data: FscData,
) -> matplotlib.pyplot.Axes:
    # prepare data
    resolution_inv = 1 / data.resolution
    fsc_values = data.fsc
    
    # plot the FSC curve
    ax.plot(resolution_inv, fsc_values, color='black')

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
        resolution_text = f'{crossing_resolution:.1f}'
    else:
        # check if entire curve is below threshold
        if fsc_values.max() < threshold:
            resolution_text = 'Inf'
        else:
            # if no crossing found but curve goes above threshold, show highest resolution
            highest_res = data.resolution.min()
            resolution_text = f'{highest_res:.1f}'
    
    # add resolution as title
    ax.set_title(f'Resolution: {resolution_text}\u2009Å', fontsize=14, pad=10)

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
