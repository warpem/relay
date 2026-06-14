# Job Type Taxonomy Reorganization

**Date:** 2026-06-14
**Status:** Design — pending review
**Scope:** Refund job type organization (menu taxonomy + on-disk folders)

## Problem

The job type hierarchy mixes several orthogonal organizing axes, which makes it
feel chaotic to navigate and maintain. Four axes are tangled together today:

| Axis | Question it answers | Where it lives |
|------|--------------------|----------------|
| Execution model | How does the job run? (local / CPU / GPU / pool) | Base classes `LocalJob`/`RelionJob`/`WarpJob`/`WarpJobGpu` + interfaces |
| Tool backend | What external software does it wrap? | Same base classes (`RelionJob` vs `WarpJob`) |
| Workflow domain | Where in the science pipeline? | `TypeCategory` string |
| On-disk location | Where is the file? | `/Jobs/...` folders |

Concrete symptoms:

- **Mixed-axis top level.** Menu top level mixes data modality (`2D`, `3D`,
  `Tilt-series`), pipeline stage (`Import`, `Preprocessing`, `PostProcessing`),
  and subsystem (`M`, `Tools`, `Notes`) at the same level. A job like
  `ExtractParticles2D` could plausibly belong to three of them.
- **Folder ≠ category.** `ExtractParticles2D` lives in `/Jobs/Preprocessing/`
  but its `TypeCategory` is `2D.ExtractParticles`.
- **Hand-typed category strings drift.** `"Pre-processing.MotionCTF"` vs
  `"Preprocessing.CTF2D"`; `"Tilt-series.Alignment.Peak alignment"` (spaces) vs
  camelCase siblings; `"Tools.Create mask"`. No consistency, no compiler help.

## Scope

**In scope**

1. Normalize all `TypeCategory` values onto one coherent taxonomy with
   consistent naming.
2. Reorganize `/Jobs/` folders (and the namespaces that mirror them) to match
   the taxonomy 1:1.

**Out of scope** (the user will take these separately)

- Capability/interface model (`ILocalJob`/`IClusterJob`/`IPooledJob` vs the
  `QueueType` property). Noted below under *Deferred* for context, but **not**
  changed here.
- Base-class inheritance restructuring (`LocalJob`/`RelionJob`/`WarpJob`/
  `WarpJobGpu` stay exactly as they are).

**Mechanism decision:** Keep the existing `abstract string TypeCategory`
mechanism unchanged. A structured/enum/derived-from-namespace scheme was
considered and rejected: C# namespaces cannot carry attributes, so display
labels with illegal characters (`Motion & CTF`, `2D classes`) would still need a
string carrier — and a free string is also the friendliest path for **external
plugins** to register new job types later. We only clean up the *values* and the
*folders*, not the mechanism.

## Organizing principle

**Primary axis: data modality, then pipeline stage.** Relay processes both
single-particle (frame-series) and tomography (tilt-series) data. The two
pipelines **diverge early and converge late**: acquisition-specific stages
differ, but once you have a 3D-ready `ParticleSet`, downstream classification and
refinement are modality-agnostic.

Key domain facts that shaped placement:

- **2D classification is frame-series-specific** conceptually, but is a RELION
  job — grouped with the rest of RELION under *Refinement* (user's call).
- **3D classification and 3D refinement are modality-agnostic** (RELION).
- **`InitialReference` is used by both Fs and Ts** → Refinement.
- **`SelectParticles` is tilt-series-specific** in this implementation → stays
  under Tilt-series / Selection.
- **M is fully agnostic** and a distinct multi-step subsystem → its own branch.
- **Branch 3 ("the agnostic 2D/3D refinement jobs") is essentially the RELION
  subsystem.** Named *Refinement* (workflow-stage name) rather than `RELION`, so
  the menu reads as "what you're doing" with the tool as an implementation
  detail.

## Target taxonomy

Five top-level branches:

```
Frame-series          Warp acquisition, single particle → particles
  Import
  Motion & CTF
  Picking
  Extraction

Tilt-series           Warp acquisition, tomography → particles
  Import
  Alignment
  CTF
  Reconstruction
  Picking
  Extraction
  Selection

Refinement            modality-agnostic particle classification/refinement (RELION)
  Initial model
  2D classes
  3D classes
  3D refinement
  Masks
  Post-process

M                     multi-particle refinement (agnostic subsystem)

Common                modality-agnostic utilities
  Import
  Tools
  Notes
```

## Per-job mapping

Leaf (final) segment names are display labels and are easily adjustable; the
structural value here is the top-level + stage grouping. `TypeGuid` values are
**unchanged** (they key serialization), so existing saved projects keep working.

### Frame-series

| Job | New `TypeCategory` | New folder |
|-----|--------------------|-----------|
| ImportDataSetFs   | `Frame-series.Import.Frame series`       | `Jobs/FrameSeries/Import/ImportDataSetFs/` |
| Motion2D          | `Frame-series.Motion & CTF.Motion`       | `Jobs/FrameSeries/MotionCtf/Motion2D/` |
| CTF2D             | `Frame-series.Motion & CTF.CTF`          | `Jobs/FrameSeries/MotionCtf/CTF2D/` |
| MotionAndCTF2D    | `Frame-series.Motion & CTF.Motion and CTF` | `Jobs/FrameSeries/MotionCtf/MotionAndCTF2D/` |
| BoxNetInference2D | `Frame-series.Picking.BoxNet`            | `Jobs/FrameSeries/Picking/BoxNetInference2D/` |
| ExtractParticles2D| `Frame-series.Extraction.Extract particles` | `Jobs/FrameSeries/Extraction/ExtractParticles2D/` |

### Tilt-series

| Job | New `TypeCategory` | New folder |
|-----|--------------------|-----------|
| ImportDataSetTs   | `Tilt-series.Import.Tilt series`              | `Jobs/TiltSeries/Import/ImportDataSetTs/` |
| AlignAretomo      | `Tilt-series.Alignment.AreTomo`               | `Jobs/TiltSeries/Alignment/AlignAretomo/` |
| AlignEtomo        | `Tilt-series.Alignment.Etomo patch tracking`  | `Jobs/TiltSeries/Alignment/AlignEtomo/` |
| AlignMiss         | `Tilt-series.Alignment.MISS patch tracking`   | `Jobs/TiltSeries/Alignment/AlignMiss/` |
| AutoLevel         | `Tilt-series.Alignment.Auto-level`            | `Jobs/TiltSeries/Alignment/AutoLevel/` |
| PeakAlign         | `Tilt-series.Alignment.Peak alignment`        | `Jobs/TiltSeries/Alignment/PeakAlign/` |
| StackTilts        | `Tilt-series.Alignment.Stack tilts`           | `Jobs/TiltSeries/Alignment/StackTilts/` |
| ImportAlignments  | `Tilt-series.Alignment.Import alignments`     | `Jobs/TiltSeries/Alignment/ImportAlignments/` |
| Ctf               | `Tilt-series.CTF.CTF`                          | `Jobs/TiltSeries/Ctf/Ctf/` |
| ReconstructTomograms | `Tilt-series.Reconstruction.Tomograms`     | `Jobs/TiltSeries/Reconstruction/ReconstructTomograms/` |
| ReconstructMap    | `Tilt-series.Reconstruction.Map`              | `Jobs/TiltSeries/Reconstruction/ReconstructMap/` |
| Denoising         | `Tilt-series.Reconstruction.Denoising`        | `Jobs/TiltSeries/Reconstruction/Denoising/` |
| TemplateMatch     | `Tilt-series.Picking.Template matching`       | `Jobs/TiltSeries/Picking/TemplateMatch/` |
| ExtractParticles  | `Tilt-series.Extraction.Extract particles`    | `Jobs/TiltSeries/Extraction/ExtractParticles/` |
| DeselectTilts     | `Tilt-series.Selection.Deselect tilts`        | `Jobs/TiltSeries/Selection/DeselectTilts/` |
| SelectTomograms   | `Tilt-series.Selection.Select tomograms`      | `Jobs/TiltSeries/Selection/SelectTomograms/` |
| SelectParticles   | `Tilt-series.Selection.Select particles`      | `Jobs/TiltSeries/Selection/SelectParticles/` |

### Refinement

| Job | New `TypeCategory` | New folder |
|-----|--------------------|-----------|
| InitialReference   | `Refinement.Initial model.Initial reference` | `Jobs/Refinement/InitialModel/InitialReference3D/` |
| Class2D            | `Refinement.2D classes.Classify 2D`          | `Jobs/Refinement/Classes2D/Class2D/` |
| Class2DSelect      | `Refinement.2D classes.Select 2D classes`    | `Jobs/Refinement/Classes2D/Class2DSelect/` |
| Class3D            | `Refinement.3D classes.Classify 3D`          | `Jobs/Refinement/Classes3D/Class3D/` |
| Class3DContinue    | `Refinement.3D classes.Continue 3D`          | `Jobs/Refinement/Classes3D/Class3D/` |
| Class3DSupervised  | `Refinement.3D classes.Supervised 3D`        | `Jobs/Refinement/Classes3D/Class3D/` |
| Class3DSelect      | `Refinement.3D classes.Select 3D classes`    | `Jobs/Refinement/Classes3D/Class3DSelect/` |
| Refine3D           | `Refinement.3D refinement.Refine 3D`         | `Jobs/Refinement/Refinement3D/Refine3D/` |
| CreateMask         | `Refinement.Masks.Create mask`               | `Jobs/Refinement/Masks/CreateMask/` |
| PostProcess        | `Refinement.Post-process.Post-process`       | `Jobs/Refinement/PostProcess/PostProcess3D/` |

### M

`TypeCategory` values move from `M.*` to a consistent `M.<Job>` form; folders
stay under `Jobs/M/`. Drop the two empty placeholder dirs
(`RemoveDataSource`, `RemoveSpecies`).

| Job | New `TypeCategory` |
|-----|--------------------|
| CreatePopulation | `M.Create population` |
| CreateDataSource | `M.Create data source` |
| CreateSpecies    | `M.Create species` |
| GetSpecies       | `M.Get species` |
| GetTiltSeries    | `M.Get tilt series` |
| ModifySpecies    | `M.Modify species` |
| EstimateWeights  | `M.Estimate weights` |
| Refine           | `M.Refine` |

### Common

| Job | New `TypeCategory` | New folder |
|-----|--------------------|-----------|
| ImportMap              | `Common.Import.Map`                  | `Jobs/Common/Import/ImportMap/` |
| ImportMask             | `Common.Import.Mask`                 | `Jobs/Common/Import/ImportMask/` |
| ImportParticles        | `Common.Import.Particles`            | `Jobs/Common/Import/ImportParticles/` |
| ImportParticlePositions| `Common.Import.Particle positions`   | `Jobs/Common/Import/ImportParticlePositions/` |
| ThresholdStatistics    | `Common.Tools.Threshold statistics`  | `Jobs/Common/Tools/ThresholdStatistics/` |
| Note                   | `Common.Notes.Note`                  | `Jobs/Common/Notes/Note/` |
| Vibe                   | `Common.Notes.Vibe`                  | `Jobs/Common/Notes/Vibe/` |

## Naming conventions (going forward)

- **Top-level segment:** modality (`Frame-series`, `Tilt-series`) or subsystem
  (`Refinement`, `M`, `Common`). Hyphenated multiword modality names.
- **Stage segment:** Title-case noun phrase (`Motion & CTF`, `Alignment`,
  `Reconstruction`, `3D classes`). Spaces and `&` allowed — it is a display
  string.
- **Leaf segment:** short human-readable verb/noun describing the job
  (`Classify 3D`, `Peak alignment`). Sentence-case, no camelCase identifiers.
- **Folder/namespace:** legal PascalCase identifiers mirroring the structural
  path (`FrameSeries`, `MotionCtf`, `Classes3D`). The `_2D`/`_3D`
  underscore-prefix hacks go away because the new top levels start with letters.

## Mechanics & impact

- **No mechanism change.** `TypeCategory`, `TypeName`, `TypeGuid`,
  `PopulateTypes()`, and the `TypeHierarchy` tree-builder are untouched; they
  already parse dot-notation strings, so they keep working with the new values.
- **`TypeGuid` is preserved** for every job → existing saved spaces/projects
  deserialize unchanged.
- **Folder move ⇒ namespace rename.** Namespaces mirror folders
  (`Refund.Jobs.<path>`), so moving folders renames namespaces, which touches
  the `using` statements / type references in **~96 files**. This is mechanical
  (IDE move/rename refactor; compiler flags any miss). The reflection-based
  `Job.Types` registry needs no edits.
- **Class3D family:** `Class3D`, `Class3DContinue`, `Class3DSupervised` share one
  source folder today and continue to; only their `TypeCategory` strings change.

## Deferred (context only — not done here)

The capability model (`ILocalJob`/`IClusterJob`/`IPooledJob` vs the abstract
`QueueType`) is genuinely redundant: "where can this run" is a **runtime,
config-dependent** decision, but it is encoded in three overlapping places that
can drift. A future change should split it into (1) a type-level behavioral
contract for "has a local code path" and (2) a config-aware runtime resolver for
"where does this instance run now." Left for a separate effort.

## Risks / open items

- Leaf display strings are first-draft; expect light bikeshedding during
  implementation. Structure (top-level + stage) is the locked part.
- `Ctf` folder doubling (`Jobs/TiltSeries/Ctf/Ctf/`) is awkward; could flatten to
  `Jobs/TiltSeries/Ctf/` if the single-job-per-leaf-folder convention is relaxed.
- Confirm nothing keys off the literal old `TypeCategory` strings (e.g. tests,
  saved layouts, docs) beyond the reflection registry before renaming.
