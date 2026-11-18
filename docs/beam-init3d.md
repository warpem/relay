# Beam initial models

**💥 Beam → 💥 Initial model** runs `beam init3d` on a cluster queue with the
`beam` module. It accepts merged particle images or raw particle tilt stacks.
Tilt stacks require the Warp tilt-series metadata input, as in the other Beam
jobs. No reference or mask is connected, and existing particle poses are ignored.

The normal controls are class count (default 1), update count (1,000), box size
(64), initial resolution (35 Å), final resolution (12 Å), particle diameter,
symmetry, positivity enforcement, scientific minibatch endpoints (100→500 particles
per update across halves/classes), and compute settings. The box size is even and at least 16 pixels;
Beam caps it at the input box, preserving the field of view and adjusting map
sampling. Classification now exposes the same box-size control, defaulting to
128. Refinement keeps its existing grid policy.

Advanced controls include maximum particles for short trials, starting blob
count, class similarity, initial/final learning rates (0.5→0.1, exponentially
annealed), snapshot interval (50 updates), pose marginalization temperature,
hardware batch size and additional arguments. Class
similarity passes `--class-similarity` (default 0.5, range 0–1) to control early
class-assignment sharing; 0 disables sharing. The optional first-CTF-peak override applies only to ordinary images.
The job defaults to one GPU and eight CPU workers, with node-local particle
scratch available as in refinement/classification.

At the configured snapshot interval, Beam publishes model snapshots. Relay numbers these preview
iterations consecutively; its logs retain the original update numbers. The active
update block appears in the iteration selector with a spinner before its
preview is ready, including the final block during full-data assignment. Relay keeps slices and cards
for those updates, with a bounded 3D viewer per class. After the optimization,
Beam assigns all selected particles against the final maps. Relay then publishes
the particle STAR, class occupancies, orientation/sampling plots and tilt-series
metadata snapshot. The shared class-grid view enables selection/export at this
point. Initial models have no FSC or independent half-map pair.

Stdout and stderr are compiled into Relay logs with carriage-return progress
reduction. Finalization can accept the latest complete intermediate models from
an interrupted run; its particle and tilt-metadata outputs stay unavailable when
there was no final assignment. Finished views and resources use saved Relay
state and figures.

Beam integration changes force progress bars to stdout, export per-class tilt
sampling directions, and atomically publish map files and the final summary so
Relay does not collect partial products. No resume interface is exposed.

Positivity is enabled by default. Disabling Enforce positivity passes
`--no-positivity` and allows signed density. Scientific minibatches control the
particles sampled per optimizer update; the advanced hardware batch size only
controls processing tiles. Initial minibatch must not exceed final minibatch;
both are positive, and both learning rates must be in (0, 1].

Snapshot interval drives log blocks, preview filenames and intermediate-storage
retention consistently, including a shorter final block. Existing saved jobs
without the new interval field retain ten-update numbering. Set snapshot cadence
through its job field rather than Additional arguments.
