# Map figure generation

Beam and RELION refinement, 3D classification and initial-model collectors batch
one iteration's Bakery commands into a single interpreter. Parallel RELION tasks
share a concurrent queue; existing process limits still apply to complete batches.
Failures propagate through the normal visualization error path. Partial completed
figures remain reusable on retry, and temporary command manifests are deleted.

The orientation/Fourier-sampling renderers share a bounded cache of particle-angle
columns (keyed by file identity, modification time and size), spherical image grids,
and sparse great-circle-to-cell operators. The Fourier operator preserves the
original H3 resolution and 500-point circle membership rule, including counting
each visited cell once per circle. It replaces repeated scans with dictionary
lookups and sparse multiplication. No particle subsampling is introduced.

A comma-separated symmetry list is resolved using the requested class number.
Particle orientations and physical tilt-projection directions use the same selected
symmetry. Empty classes produce valid empty figures. RELION's per-class entry point
uses the same implementation as Beam and single-map jobs.

Output dimensions and PNG/adaptive/light/dark SVG exports are retained. Binned
polar heatmaps use nearest-neighbor display rather than expensive sinc
interpolation, preserving discrete bin values. Every command releases its open
Matplotlib figures before the next command, including legacy card renderers.

A local six-class benchmark with real particle metadata and physical tilt
projection directions took 39.7 seconds before and 9.9 seconds after for the twelve
orientation/Fourier figures, including interpreter startup (approximately 4x).
This measures those plots, not total iteration visualization time. Bakery process
runtimes are also logged by Relay for deployment-side profiling.
