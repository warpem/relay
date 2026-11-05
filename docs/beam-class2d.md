# Beam 2D classification

`💥 Beam → 💥 2D classification` runs `beam class2d` on the selected cluster queue
using the Beam module. Default resources are one GPU, eight CPU workers, and
32 GB host memory. CPU mode requests no GPU. The wrapper requires the Beam
version that publishes portable class2d snapshots.

Connect one or more sets of extracted **single-image particles**. Relay merges
multiple STAR inputs before staging. This command does not accept references,
particle tilt stacks, or a tilt-series metadata resource. Beam's CLI also accepts
CryoSPARC files directly; in Relay those go through particle import.

Normal settings are the final number of classes (64), training updates (60),
final full-data passes (2), training particles per class (100), learning rate (0.2), optional final
subclassification, particle diameter (200 Å), working box (64 px), regularization (FRC or Denoise),
translation range (100 Å), compute device, CPU workers, host memory, and optional
particle scratch directory. The even working box must not exceed the input box.
Advanced settings cover initial classes, peripheral class
ratio, angular sampling, pose marginalization, CTF before the first peak,
soft/hard reconstruction assignments, PCA rank and sample cap, discovery interval,
compute batch size, and denoising training steps. Other options can be supplied
through the single-line additional-arguments field. Paths, device, workers,
quiet, help, and dry-run are managed by Relay.

## Displays and output resources

Beam publishes a snapshot at each discovery block boundary, at the end of
training, and after each final full-data pass. Every full-data pass has its own
log section and visualization snapshot, including class populations. Snapshot
numbers are consecutive; they are
not training-update numbers. Class identities may change during training.

The expanded view reuses RELION's sortable class grid and final class-selection
workflow. It also shows the PCA mean and signed components when available, plus
the first two **equalized discovery coordinates**. The scatter describes the last
PCA sample, not all particles or final class assignments; colors identify the
sample's discovery groups. At most 20,000 sample points are drawn.

Optional subclassification writes four fixed-pose subclasses per regular parent
under `beam/subclasses/`. The expanded view displays completed groups beside their
parent average as they become available, with within-parent percentages and
resolution estimates. Skipped parents show the reason supplied by Beam.

The original particle/template outputs and selection retain the parent bank.
Separate **Subclass particles** and **Subclasses** outputs contain the completed
subclass collection. Child classes receive consecutive collection IDs, while
`rlnBeamParentClassNumber` preserves the parent identity in particle metadata.
The grouped view maps local child selections to those collection IDs and uses
the existing class-selection job for export. Export is enabled after
subclassification completes; manual finalization of an interrupted run makes
completed parent groups selectable. Peripheral or skipped parents are absent
from the subclass particle collection.

Subclass statistics, group metadata, and the selection MRC/STAR files
are compiled once into `.relay/subclasses/`; opening the finished view does not
parse Beam's subclass summaries or load PyTorch checkpoints.

Final particles retain source stack paths, sampling and coordinates, and acquire
class labels and in-plane shifts. A 2D psi angle is not advertised as a fitted 3D
orientation. Particle output and selection become available only after final
assignment. Template resources use each snapshot's actual class count, including
when it differs from the requested target. Manual finalization of an interrupted
run exposes completed class/PCA snapshots without fabricating particle labels.

Each atomic `beam/snapshots/itNNN` directory contains:

- `classes.mrcs`: full-resolution display class averages, matching the denoised
  display averages when denoising is enabled.
- `model.star`: RELION-compatible reference paths, class resolutions, and final
  class populations when available.
- `principal_components.mrcs`: the mean at image 1, then signed PCs 1…rank, on the
  PCA working grid with its physical pixel size. Independent Fourier boundary
  conjugates are restored before the real inverse transform.
- `embeddings.npz`: equalized `coordinates`, zero-based original input table
  `particle_rows`, one-based `discovery_classes`, and PCA `scales`. These rows
  belong to the current PCA discovery sample.
- `snapshot.json`: update number, counts, and final-assignment availability.

Relay compiles class statistics, the embedding figure, and per-snapshot
stdout into its `.relay` directory. Relay requests `--show-progress`; carriage-return
bars are collapsed to one current line when the logs are compiled. Stderr is collected using the same mechanism
as other cluster jobs. Opening a finished expanded view reads these saved files;
it does not load Beam checkpoints or import PyTorch.

## Current CLI and Relay mapping

There are **51 functional options**, plus `--help`. `classify2d` is an alias for
`class2d`. The following surface was read from Beam's parser during integration.
Defaults are Beam defaults; Relay uses GPU mode by default instead of CPU.

| Option | Beam default | Relay mapping |
|---|---|---|
| `--input` | `required` | Managed by Relay |
| `--project-root` | `unset` | Managed by Relay |
| `--output` | `class2d` | Managed by Relay |
| `--particle-diameter` | `200.0` | Particle diameter |
| `--iterations` | `60` | Training updates |
| `--working-box` | `64` | Working box size |
| `--device` | `cpu` | Compute device (Relay default: cuda:0) |
| `--workers` | `8` | CPU workers |
| `--cache-dir` | `unset` | Particle scratch directory |
| `--cache-threads` | `4` | Additional arguments |
| `--batch-size` | `128` | Advanced: Particles per compute batch |
| `--particles-per-class` | `100` | Training particles per class |
| `--minimum-batch-size` | `1024` | Additional arguments |
| `--reservoir-size` | `2048` | Additional arguments |
| `--maximum-pca-particles` | `50000` | Advanced: Maximum PCA particles |
| `--initial-classes` | `4` | Advanced: Initial classes |
| `--final-classes` | `64` | Number of classes |
| `--pca-rank` | `16` | Advanced: Principal components |
| `--pca-iterations` | `300` | Additional arguments |
| `--coverage-interval` | `5` | Additional arguments |
| `--discovery-interval` | `10` | Advanced: Updates between PCA rebuilds |
| `--final-iterations` | `2` | Final full-data passes |
| `--denoise-initial-steps` | `10000` | Advanced: Initial denoising steps (Denoise only) |
| `--denoise-steps` | `1000` | Advanced: Denoising steps per update (Denoise only) |
| `--denoise-minimum-classes` | `8` | Additional arguments |
| `--pose-modes` | `60` | Additional arguments |
| `--pose-modes-per-class` | `0` | Additional arguments |
| `--seed` | `7` | Additional arguments |
| `--full-replacement` | `False` | Additional arguments |
| `--marginalize-poses` | `False` | Advanced: Marginalize poses |
| `--ctf-intact-first-peak` | `False` | Advanced: CTF intact before first peak |
| `--subclassify` | `False` | Subclassify final classes |
| `--denoise` | `False` | Regularization = Denoise |
| `--learning-rate` | `0.2` | Learning rate |
| `--initial-angular-sampling` | `20.0` | Advanced: Initial angular sampling |
| `--translation-range` | `100.0` | Translation range |
| `--reconstruction-assignments` (soft, hard) | `soft` | Advanced: Reconstruction assignments |
| `--pose-parent-mass` | `0.99` | Additional arguments |
| `--pca-tolerance` | `1e-06` | Additional arguments |
| `--no-pca-mask` | `True` | Additional arguments |
| `--density-power` | `0.5` | Additional arguments |
| `--class-seeding` (particles, pca) | `pca` | Additional arguments |
| `--peripheral-class-ratio` | `1.0` | Advanced: Peripheral classes per regular class |
| `--peripheral-quantile` | `0.8` | Additional arguments |
| `--half-mix` | `0.1` | Additional arguments |
| `--coverage-weight` | `0.02` | Additional arguments |
| `--maximum-particles` | `unset` | Additional arguments |
| `--dry-run` | `False` | Not allowed in Relay jobs |
| `--quiet` | `False` | Not allowed in Relay jobs |
| `--show-progress` | `auto` | Managed by Relay: enabled on stdout |
| `--dont-show-progress` | `auto` | Not allowed in Relay jobs |


## Client-side image rendering

The shared class display fetches MRC stacks directly through the server file API
and decodes them in one shared Web Worker using Relay's MRC reader. It creates
individual PNG blob previews for classes, signed principal components, parent
comparisons and subclasses. Blazor handles labels/selection only: no image arrays
or base64 payloads pass through .NET. Selected/unselected RELION and Beam class
views use the same renderer.

Each class uses its own 0.5th percentile as black and maximum as white. Signed
PCA images use a symmetric 99.5th absolute-percentile range with zero at mid-gray.
MRC row zero is displayed at the bottom, matching previous origin='lower' plots.
Stacks are shared across views while needed; requests and blob URLs are released
on source changes/disposal. Errors and download progress are shown in the view.
The browser never paints a wide, whole-stack background texture, so existing
large class banks can display without regenerating their saved outputs.
