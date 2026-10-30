# Beam integration

Particle import accepts RELION STAR or CryoSPARC `.cs` metadata. For CryoSPARC,
select the primary `.cs` file, the original project directory, and up to five
optional passthrough files using separate `.cs` file pickers. RELION has its own
`.star` picker and an optional project directory (defaults to the STAR file's
directory). Relay submits `beam import --absolute-paths --check-stacks` for
either format to a CPU cluster queue requiring the `beam` and `cpu` modules.
After completion, Relay collects the STAR metadata and generates particle
previews. Pixel data are referenced in place. Extraction coordinates retain
the original micrograph frame and remain separate from alignment shifts.

Beam is needed on the import/refinement compute nodes; the Relay server does
not launch Beam. Queue submission templates must load `ml beam` for jobs
requiring that module. The server still needs Bakery for result visualization
and access to the shared inputs/results. Refinement queues need Beam and its
native CPU/CUDA backend. The refinement job requests the `beam` software module
and either `gpu` or `cpu`; configure queue submission templates accordingly. GPU
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

Beam's expanded view leads with left-aligned, width-capped orthoslices, followed by the 3D map
and quality plots. Bakery writes PNGs plus self-contained SVG siblings for the
slices, FSC, orientation and Fourier sampling figures. The adaptive `.svg`
contains light/dark CSS and embedded raster data; Relay sets the embedding
`color-scheme` from its selected theme, including manual overrides. Density
pixels and heatmap colors stay unchanged. `.light.svg` and `.dark.svg` contain
literal colors for stable downloads into other applications. Figures can be saved through the browser image context menu. Fixed-theme
variants and the original PNG remain available in the job outputs.
Job-card PNG/PDF generation is unchanged.

Directional FSC's 10–90% band and published directional range/cFAR are included
in the FSC figure when the CSV is present. Resolution titles retain two decimals
below 2 Å. A static history figure shows map/alignment resolution and particle
counts. CTF diagnostics become available after Beam publishes a
`restart_state.ctf_refined_iteration` baseline and a later iteration. Defocus
corrections are matched by unique particle UID (falling back to unique image
name), never by row position. Histograms include all matched particles; the
y axis is linear. Beam-tilt overviews include every group, using density bins
above 2,000 groups. Coefficient-index bars and the optics TSV export are omitted.
Beam now exports observed/fitted/residual odd/even phase MRCs and fit masks
for up to ten optics groups during integrated aberration fitting. Units and
Fourier sampling are recorded in MRC header labels. Relay does not yet render
these phase maps. Older jobs need another CTF fit to produce them; saved
Zernike coefficients cannot reconstruct the observed/residual fields.
All figures have adaptive and fixed-theme SVG exports. Display width caps map
10-point figure text to one UI em; narrow containers can shrink them. History is
5.5 × 3 inches, matching the 3-inch polar figure height without scaling its text.
Diagnostics version 2 regenerates older figures on collection/finalization.


Existing PNG-only jobs still display. Their SVGs are generated on the next
result collection or manual finalization; opening a view does not run Bakery.
For a standalone figure, the Bakery `fsc`, `map-orthoslice-atlas`, and
`orientation-and-fourier-sampling` commands accept `--svg` to add SVG siblings
alongside their requested PNG outputs. Supplying an `.svg` output filename also
writes the adaptive and two fixed SVGs. Both Relay and Bakery must be updated
together before collecting Beam results with this version.

Manual finalization accepts the last complete iteration after interruption and
generates any missing Relay visualizations. A job with no complete iteration
cannot be finalized. Resume is intentionally not supported.
