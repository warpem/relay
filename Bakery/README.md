# Bakery

Static visualization generator for [Relay](../README.md).

Bakery is a Python CLI tool that produces SVG/PNG figures embedded in Relay job cards
(2D class atlases, FSC curves, orthoslice panels, orientation plots, etc.).

## Installation

```bash
conda create -n bakery python=3.12
conda activate bakery
pip install ./Bakery
```

## Usage

```bash
bakery --help
```
Single-view 3D map previews (class, refinement, species, import, postprocessing,
particle-map and template panels) integrate the full map along z. They show the
central two thirds in x and y with per-map min/max grayscale contrast, matching
Beam 3D refinement. The legacy `xy-slice` command also uses this preview;
`--slice-thickness-angstroms` remains accepted but does not limit projection depth.
Three-plane orthoslice atlases and tomogram slabs retain their slice views.
