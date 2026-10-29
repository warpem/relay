# Beam integration

Particle import accepts RELION STAR or CryoSPARC `.cs` metadata. For CryoSPARC,
select the primary file, the original project directory, and any passthrough
files (one path per line). Relay runs `beam import --absolute-paths --check-stacks`
locally and then uses the existing particle previews. Pixel data
are referenced in place. Extraction coordinates retain the original micrograph
frame and remain separate from alignment shifts.

The local Relay process needs the updated `beam` executable on PATH. Refinement
queues need Beam and its native CPU/CUDA backend, plus the updated Bakery
package for visualization. The job requests the `beam` software module and
either `gpu` or `cpu`; configure queue submission templates accordingly. GPU
refinement requests one GPU and uses its scheduler-visible `cuda:0` device.

`BeamRefine3D` accepts one or more particle resources, one starting reference,
and an optional mask. Global refinement uses Beam's automatic masks when no
mask is connected. Local refinement requires existing particle poses and a
mask. A connected mask disables automatic starting-reference symmetry alignment
so it remains in the same frame as the reference.

The normal controls cover symmetry, initial low-pass, particle diameter,
global/local search ranges, regularization, optional CTF fitting, CPU workers,
and host memory. Advanced fields cover local priors/pivot/recentering,
denoising bandwidth, Ewald hand, magnification fitting, batch size, scratch,
and additional CLI arguments. Hidden dependent fields are omitted from the
command. Custom arguments override scientific defaults; resource allocation,
input/output connections, and the project root remain owned by Relay.

Beam writes into `<job>/beam/itNNN/run_itNNN_*`. Relay discovers complete
iterations during execution/finalization and saves iteration stdout, stderr,
map slices, FSC plots, orientation/sampling plots, and job cards in its own
results directory. Finished views read those files directly. FSC plots use
Beam's published resolution estimate and label its separate FSC curves.
Raw unfiltered half maps are exposed for downstream post-processing, alongside
the display map, particles, and refinement mask.

Manual finalization accepts the last complete iteration after interruption and
generates any missing Relay visualizations. A job with no complete iteration
cannot be finalized. Resume is intentionally not supported.
