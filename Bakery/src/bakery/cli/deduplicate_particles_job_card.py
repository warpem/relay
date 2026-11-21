import json
from pathlib import Path

import matplotlib.axes
import numpy as np
import typer
from matplotlib import pyplot as plt

from bakery.cli._cli import cli

KEPT_COLOR = '#4c72b0'
REMOVED_COLOR = '#dd8452'
TARGET_BINS = 40


@cli.command(no_args_is_help=True, help="visualize particle deduplication: nearest-neighbour distances and counts")
def deduplicate_particles_job_card(
    report_file: Path = typer.Option(..., help="JSON report written by beam deduplicate --report"),
    output_file: Path = typer.Option(...),
):
    report = json.loads(report_file.read_text())
    fig = plt.figure(figsize=(2, 1))
    histogram_ax = fig.add_axes((0.04, 0.2, 0.5, 0.74))
    text_ax = fig.add_axes((0.59, 0.0, 0.41, 1.0))
    draw_neighbour_histogram(histogram_ax, report)
    draw_summary(text_ax, report)
    fig.savefig(output_file, dpi=288)
    fig.savefig(str(output_file.with_suffix('.pdf')), dpi=288)
    plt.close(fig)


def rebin(counts: np.ndarray, bin_width: float, upper: float) -> tuple[np.ndarray, np.ndarray]:
    """Merge Beam's fine bins into about TARGET_BINS bins covering [0, upper]."""
    factor = max(1, int(round(upper / TARGET_BINS / bin_width)))
    n = min(len(counts), int(np.ceil(upper / (bin_width * factor))) * factor)
    padded = np.zeros(int(np.ceil(n / factor)) * factor, dtype=np.int64)
    padded[:n] = counts[:n]
    merged = padded.reshape(-1, factor).sum(axis=1)
    edges = np.arange(len(merged) + 1) * bin_width * factor
    return merged, edges


def draw_neighbour_histogram(ax: matplotlib.axes.Axes, report: dict):
    neighbours = report.get('nearest_neighbour_angstrom') or {}
    counts = np.asarray(neighbours.get('counts') or [], dtype=np.int64)
    threshold = float(report['distance_angstrom'])
    if not neighbours.get('particles') or not len(counts):
        ax.text(0.5, 0.5, 'No particles share a\nmicrograph or tilt series', ha='center', va='center',
                fontsize=4.5, transform=ax.transAxes)
        ax.axis('off')
        return

    # The upper 5% are sparse, isolated particles; the threshold always stays in view.
    upper = max(float(neighbours['quantiles']['p95']), 1.25 * threshold)
    merged, edges = rebin(counts, float(neighbours['bin_width']), upper)
    centers = (edges[:-1] + edges[1:]) / 2
    colors = [REMOVED_COLOR if center < threshold else KEPT_COLOR for center in centers]
    ax.bar(edges[:-1], merged, width=np.diff(edges), align='edge', color=colors, linewidth=0)
    ax.axvline(threshold, color='#c44e52', linestyle='--', linewidth=0.5)
    ax.text(threshold, 1.0, f' {threshold:g} Å', transform=ax.get_xaxis_transform(),
            ha='left', va='top', fontsize=4, color='#c44e52')

    ax.set_xlim(0, edges[-1])
    ax.set_ylim(bottom=0)
    ax.set_yticks([])
    ax.tick_params(axis='x', labelsize=4, length=1.5, width=0.4, pad=1)
    ax.set_xlabel('Nearest neighbour (Å)', fontsize=4.5, labelpad=1)
    for side in ('top', 'right', 'left'):
        ax.spines[side].set_visible(False)
    ax.spines['bottom'].set_linewidth(0.4)


def summary_lines(report: dict) -> list[tuple[str, float, str]]:
    """(text, font size, weight) lines describing the run, each short enough for the card's column."""
    particles, kept, removed = report['particles'], report['kept'], report['removed']
    share = 100 * removed / particles if particles else 0.0
    lines = [
        (f'Kept {kept:,}', 6, 'bold'),
        (f'of {particles:,} particles', 4.5, 'normal'),
        (f'Removed {removed:,} ({share:.1f}%)', 4.5, 'normal'),
        ('', 2, 'normal'),
        (f'Pairs within {report["distance_angstrom"]:g} Å:', 4.5, 'normal'),
        ('worse one removed' if report['drop'] == 'worse' else 'both removed', 4.5, 'normal'),
    ]
    if report['drop'] == 'worse':
        score = report.get('score')
        if score is None:
            lines.append(('No score: first kept', 4.5, 'normal'))
        elif len(score) <= 13:
            lines.append((f'Score: {score}', 4.5, 'normal'))
        else:
            lines += [('Score:', 4.5, 'normal'), (score, 4, 'normal')]
        if score is not None and report.get('lower_is_better'):
            lines.append(('(lower is better)', 4, 'normal'))
    groups = report.get('groups', 0)
    kind = report.get('group_kind', 'micrograph')
    noun = kind if kind == 'tilt series' or groups == 1 else kind + 's'
    lines.append((f'{groups:,} {noun}', 4.5, 'normal'))
    neighbours = report.get('nearest_neighbour_angstrom') or {}
    if neighbours.get('particles'):
        lines.append((f'Median spacing {neighbours["quantiles"]["p50"]:.0f} Å', 4.5, 'normal'))
    return lines


def draw_summary(ax: matplotlib.axes.Axes, report: dict):
    ax.axis('off')
    y = 0.93
    for text, size, weight in summary_lines(report):
        ax.text(0.0, y, text, fontsize=size, fontweight=weight, ha='left', va='top', transform=ax.transAxes)
        y -= size / 72 * 1.35  # one line, in axes units of a 1-inch-tall axes
