# Beam 3D classification

The **💥 Beam → 💥 Classify 3D** cluster job runs `beam class3d` and requires the
queue's `beam` module block. It uses one GPU (or CPU), eight CPU workers by
default, and the same particle merging, scratch, logging and manual finalization
as Beam refinement.

- **Unsupervised:** connect one reference map and choose the class count (default
  four). Beam replicates the reference and bootstraps a balanced partition.
- **Supervised:** connect multiple reference maps or map collections. Every map
  becomes one class, and Relay calculates the class count. At least two maps
  are required. Paired half-maps take precedence over an average; average-only
  maps seed both halves. Relay stages two reference-list STAR files.

Both modes accept ordinary images or raw particle tilt stacks. Tilt stacks
require a connected Warp tilt-series metadata resource. Outputs include
classified particles, per-class maps with independent half-maps, and a snapshot
of tilt-series metadata. Exporting selected classes preserves half-maps and
forwards the metadata resource, allowing downstream Beam refinement.

Controls cover fixed iteration count (default 25), working box size (default 128
pixels), FSC/denoise regularization,
global/local alignment, symmetry, starting resolution, search ranges, batch
size and compute resources. Local alignment requires poses and a mask. Advanced
controls and the single-line additional arguments field follow refinement.
Classification always processes all particles; it does not expose refinement's
convergence tail, CTF fitting, Ewald correction or Blush. Beam currently has no
fixed-pose classification mode.

Relay saves class statistics, map slices, spherical/directional FSC, angular
distributions and Fourier-sampling figures per iteration. Tilt sampling uses
Beam's per-class physical acquisition directions. Finished views use these
saved figures and compiled stdout/stderr. Class FSC is a controller diagnostic;
independently refine selected particles for a final resolution estimate.

This integration requires Beam's classification iteration headings and
`run_itNNN_classCCC_projection_directions.npy` exports for tilt stacks.
