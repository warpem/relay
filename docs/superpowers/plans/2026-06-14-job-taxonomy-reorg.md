# Job Taxonomy Reorganization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Normalize all 48 job-type `TypeCategory` strings onto one modality-first taxonomy and reorganize the `/Jobs/` folders + namespaces to mirror it 1:1.

**Architecture:** Two phases. **Phase A** rewrites only the `TypeCategory` string literals (no file moves) and is guarded by a new reflection-based xUnit test that asserts the full taxonomy. **Phase B** moves folders and renames the matching namespaces branch-by-branch, verified by `dotnet build` (the compiler flags every stale `using`) plus re-running the Phase-A test. The `TypeCategory`/`TypeName`/`TypeGuid` mechanism itself is unchanged; `TypeGuid` values are never touched, so existing saved projects keep deserializing.

**Tech Stack:** C# / .NET 10, Blazor, xUnit 2.9. Build: `dotnet build Relay.sln`. Test: `dotnet test Refund.Tests/Refund.Tests.csproj`.

**Reference spec:** `docs/superpowers/specs/2026-06-14-job-taxonomy-reorg.md`

**Key mechanics (confirmed in source):**
- `Refund/DataModel/Job.cs:723 PopulateTypes()` reflects over every non-abstract `Job` subclass, throws on duplicate `TypeGuid` (`Job.cs:743`) **or** duplicate `TypeCategory` (`Job.cs:749`). Call `Job.PopulateStatic()` to populate `Job.Types` (guid→Type).
- `Job.cs:770-788` builds the menu tree by `TypeCategory.Split('.')`: every segment *except the last* is a group name matched by exact string equality; the **last segment is the leaf label**. Inconsistent spelling of an intermediate segment creates a duplicate group — this is the drift bug we are removing.
- Namespaces mirror folders as `Refund.Jobs.<folder.path>` (digit-leading folders get a `_` prefix: `_2D`, `_3D`). Moving a folder requires editing the `namespace` line and fixing every `using` that referenced it (~96 files reference `Refund.Jobs.*` sub-namespaces).

---

## Phase A — Normalize `TypeCategory` strings (no file moves)

### Task A1: Add the taxonomy safety-net test (RED)

**Files:**
- Create: `Refund.Tests/DataModel/JobTaxonomyTests.cs`

- [ ] **Step 1: Write the failing test**

The test is keyed by **class short name** (`Type.Name`), so it is independent of namespaces and survives Phase B unchanged. It asserts (a) every registered job maps to its expected new `TypeCategory`, (b) the registry count equals the expected count (catches an added/removed job), and (c) the menu tree has exactly the five expected top-level groups.

```csharp
using Refund.DataModel;

namespace Refund.Tests.DataModel;

public class JobTaxonomyTests
{
    // class short name -> expected TypeCategory (the locked taxonomy)
    private static readonly Dictionary<string, string> Expected = new()
    {
        // Frame-series
        ["ImportDataSetFs"]        = "Frame-series.Import.Frame series",
        ["Motion2D"]               = "Frame-series.Motion & CTF.Motion",
        ["CTF2D"]                  = "Frame-series.Motion & CTF.CTF",
        ["MotionAndCTF2D"]         = "Frame-series.Motion & CTF.Motion and CTF",
        ["BoxNetInference2D"]      = "Frame-series.Picking.BoxNet",
        ["ExtractParticles2D"]     = "Frame-series.Extraction.Extract particles",

        // Tilt-series
        ["ImportDataSetTs"]        = "Tilt-series.Import.Tilt series",
        ["AlignAretomo"]           = "Tilt-series.Alignment.AreTomo",
        ["AlignEtomo"]             = "Tilt-series.Alignment.Etomo patch tracking",
        ["AlignMiss"]              = "Tilt-series.Alignment.MISS patch tracking",
        ["AutoLevel"]              = "Tilt-series.Alignment.Auto-level",
        ["PeakAlign"]              = "Tilt-series.Alignment.Peak alignment",
        ["StackTilts"]             = "Tilt-series.Alignment.Stack tilts",
        ["ImportAlignments"]       = "Tilt-series.Alignment.Import alignments",
        ["Ctf"]                    = "Tilt-series.CTF.CTF",
        ["ReconstructTomograms"]   = "Tilt-series.Reconstruction.Tomograms",
        ["ReconstructMap"]         = "Tilt-series.Reconstruction.Map",
        ["Denoising"]              = "Tilt-series.Reconstruction.Denoising",
        ["TemplateMatch"]          = "Tilt-series.Picking.Template matching",
        ["ExtractParticles"]       = "Tilt-series.Extraction.Extract particles",
        ["DeselectTilts"]          = "Tilt-series.Selection.Deselect tilts",
        ["SelectTomograms"]        = "Tilt-series.Selection.Select tomograms",
        ["SelectParticles"]        = "Tilt-series.Selection.Select particles",

        // Refinement
        ["InitialReference"]       = "Refinement.Initial model.Initial reference",
        ["Class2D"]                = "Refinement.2D classes.Classify 2D",
        ["Class2DSelect"]          = "Refinement.2D classes.Select 2D classes",
        ["Class3D"]                = "Refinement.3D classes.Classify 3D",
        ["Class3DContinue"]        = "Refinement.3D classes.Continue 3D",
        ["Class3DSupervised"]      = "Refinement.3D classes.Supervised 3D",
        ["Class3DSelect"]          = "Refinement.3D classes.Select 3D classes",
        ["Refine3D"]               = "Refinement.3D refinement.Refine 3D",
        ["CreateMask"]             = "Refinement.Masks.Create mask",
        ["PostProcess"]            = "Refinement.Post-process.Post-process",

        // M
        ["CreatePopulation"]       = "M.Create population",
        ["CreateDataSource"]       = "M.Create data source",
        ["CreateSpecies"]          = "M.Create species",
        ["GetSpecies"]             = "M.Get species",
        ["GetTiltSeries"]          = "M.Get tilt series",
        ["ModifySpecies"]          = "M.Modify species",
        ["EstimateWeights"]        = "M.Estimate weights",
        ["Refine"]                 = "M.Refine",

        // Common
        ["ImportMap"]              = "Common.Import.Map",
        ["ImportMask"]             = "Common.Import.Mask",
        ["ImportParticles"]        = "Common.Import.Particles",
        ["ImportParticlePositions"]= "Common.Import.Particle positions",
        ["ThresholdStatistics"]    = "Common.Tools.Threshold statistics",
        ["Note"]                   = "Common.Notes.Note",
        ["Vibe"]                   = "Common.Notes.Vibe",
    };

    [Fact]
    public void EveryJobType_HasExpectedTypeCategory()
    {
        Job.PopulateStatic();

        var actual = Job.Types.Values.ToDictionary(
            t => t.Name,
            t => ((Job)Activator.CreateInstance(t)!).TypeCategory);

        Assert.Equal(Expected.Count, actual.Count); // no job added/removed unexpectedly

        foreach (var (name, category) in Expected)
        {
            Assert.True(actual.ContainsKey(name), $"Missing job type: {name}");
            Assert.Equal(category, actual[name]);
        }
    }

    [Fact]
    public void MenuHierarchy_HasFiveTopLevelGroups()
    {
        Job.PopulateStatic();

        var top = Job.TypeHierarchy.Subgroups
                     .Select(g => g.Name)
                     .OrderBy(x => x, StringComparer.Ordinal)
                     .ToArray();

        Assert.Equal(
            new[] { "Common", "Frame-series", "M", "Refinement", "Tilt-series" },
            top);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Refund.Tests/Refund.Tests.csproj --filter "FullyQualifiedName~JobTaxonomyTests"`
Expected: FAIL. `EveryJobType_HasExpectedTypeCategory` fails on the first mismatched category (current values are still the old strings); `MenuHierarchy_HasFiveTopLevelGroups` fails because today's top-level groups are `2D, 3D, Preprocessing, Tilt-series, Import, M, PostProcessing, Tools, Notes, Pre-processing`.

- [ ] **Step 3: Commit the failing test**

```bash
git add Refund.Tests/DataModel/JobTaxonomyTests.cs
git commit -m "test: add job taxonomy safety-net (expected RED until Phase A done)"
```

---

### Task A2: Rewrite `TypeCategory` values to the new taxonomy

In each job's main `.cs` file, replace the single line
`public override string TypeCategory => "<old>";` with the new value below.
Edit nothing else. After all five branches, the Task A1 test goes green.

**Files (one `TypeCategory` line each):**
- Modify: `Refund/Jobs/...` — the main job class file in each listed folder.

- [ ] **Step 1: Frame-series (6 files)**

| Class (file folder) | Old `TypeCategory` | New `TypeCategory` |
|---|---|---|
| ImportDataSetFs (`Import/ImportDataSetFs`)        | `Import.DataSetFs`        | `Frame-series.Import.Frame series` |
| Motion2D (`Preprocessing/Motion2D`)               | `Preprocessing.Motion2D`  | `Frame-series.Motion & CTF.Motion` |
| CTF2D (`Preprocessing/CTF2D`)                     | `Preprocessing.CTF2D`     | `Frame-series.Motion & CTF.CTF` |
| MotionAndCTF2D (`Preprocessing/MotionAndCTF2D`)   | `Pre-processing.MotionCTF`| `Frame-series.Motion & CTF.Motion and CTF` |
| BoxNetInference2D (`Preprocessing/BoxNetInference2D`) | `2D.BoxNetInference`  | `Frame-series.Picking.BoxNet` |
| ExtractParticles2D (`Preprocessing/ExtractParticles2D`) | `2D.ExtractParticles`| `Frame-series.Extraction.Extract particles` |

- [ ] **Step 2: Tilt-series (17 files)**

| Class (file folder) | Old `TypeCategory` | New `TypeCategory` |
|---|---|---|
| ImportDataSetTs (`Import/ImportDataSetTs`)        | `Import.DataSetTs`                         | `Tilt-series.Import.Tilt series` |
| AlignAretomo (`Ts/Alignment/AlignAretomo`)       | `Tilt-series.Alignment.AlignAretomo`       | `Tilt-series.Alignment.AreTomo` |
| AlignEtomo (`Ts/Alignment/AlignEtomo`)           | `Tilt-series.Alignment.AlignEtomoPatchTracking` | `Tilt-series.Alignment.Etomo patch tracking` |
| AlignMiss (`Ts/Alignment/AlignMiss`)             | `Tilt-series.Alignment.AlignMissPatchTracking`  | `Tilt-series.Alignment.MISS patch tracking` |
| AutoLevel (`Ts/Alignment/AutoLevel`)             | `Tilt-series.Alignment.AutoLevel`          | `Tilt-series.Alignment.Auto-level` |
| PeakAlign (`Ts/Alignment/PeakAlign`)             | `Tilt-series.Alignment.Peak alignment`     | `Tilt-series.Alignment.Peak alignment` |
| StackTilts (`Ts/Alignment/StackTilts`)           | `Tilt-series.Alignment.StackTilts`         | `Tilt-series.Alignment.Stack tilts` |
| ImportAlignments (`Ts/Alignment/ImportAlignments`)| `Tilt-series.Alignment.ImportAlignments`  | `Tilt-series.Alignment.Import alignments` |
| Ctf (`Ts/Ctf`)                                   | `Tilt-series.CTF`                          | `Tilt-series.CTF.CTF` |
| ReconstructTomograms (`Ts/Reconstruction/ReconstructTomograms`) | `Tilt-series.Reconstruction.Reconstruct` | `Tilt-series.Reconstruction.Tomograms` |
| ReconstructMap (`Ts/Reconstruction/ReconstructMap`) | `Tilt-series.Reconstruction.Map`        | `Tilt-series.Reconstruction.Map` |
| Denoising (`Ts/Denoising`)                       | `Tilt-series.Denoising`                    | `Tilt-series.Reconstruction.Denoising` |
| TemplateMatch (`Ts/TemplateMatch`)               | `Tilt-series.TemplateMatch`                | `Tilt-series.Picking.Template matching` |
| ExtractParticles (`Ts/ExtractParticles`)         | `Tilt-series.ExtractParticles`             | `Tilt-series.Extraction.Extract particles` |
| DeselectTilts (`Ts/DeselectTilts`)               | `Tilt-series.DeselectTilts`                | `Tilt-series.Selection.Deselect tilts` |
| SelectTomograms (`Ts/SelectTomograms`)           | `Tilt-series.SelectTomograms`              | `Tilt-series.Selection.Select tomograms` |
| SelectParticles (`Ts/SelectParticles`)           | `Tilt-series.SelectParticles`              | `Tilt-series.Selection.Select particles` |

> Note: `PeakAlign` and `ReconstructMap` keep the same final value but their *grouping* is now consistent with siblings; still re-confirm the literal matches the New column exactly (spacing matters).

- [ ] **Step 3: Refinement (10 files)**

| Class (file folder) | Old `TypeCategory` | New `TypeCategory` |
|---|---|---|
| InitialReference (`3D/InitialReference3D`)       | `3D.InitialReference`   | `Refinement.Initial model.Initial reference` |
| Class2D (`2D/Class2D`)                           | `2D.Class2D`            | `Refinement.2D classes.Classify 2D` |
| Class2DSelect (`2D/Class2DSelect`)               | `2D.Class2DSelect`      | `Refinement.2D classes.Select 2D classes` |
| Class3D (`3D/Class3D`)                           | `3D.Class3D`            | `Refinement.3D classes.Classify 3D` |
| Class3DContinue (`3D/Class3D`)                   | `3D.Class3DContinue`    | `Refinement.3D classes.Continue 3D` |
| Class3DSupervised (`3D/Class3D`)                 | `3D.Class3DSupervised`  | `Refinement.3D classes.Supervised 3D` |
| Class3DSelect (`3D/Class3DSelect`)               | `3D.Class3DSelect`      | `Refinement.3D classes.Select 3D classes` |
| Refine3D (`3D/Refine3D`)                         | `3D.Refine3D`           | `Refinement.3D refinement.Refine 3D` |
| CreateMask (`Tools/CreateMask`)                  | `Tools.Create mask`     | `Refinement.Masks.Create mask` |
| PostProcess (`PostProcessing/PostProcess3D`)     | `PostProcessing.PostProcess3D` | `Refinement.Post-process.Post-process` |

> `Class3DContinue` and `Class3DSupervised` live in the `3D/Class3D` folder alongside `Class3D` — edit each class's own `TypeCategory` line.

- [ ] **Step 4: M (8 files)**

| Class (file folder) | Old `TypeCategory` | New `TypeCategory` |
|---|---|---|
| CreatePopulation (`M/CreatePopulation`) | `M.CreatePopulation` | `M.Create population` |
| CreateDataSource (`M/CreateDataSource`) | `M.CreateDataSource` | `M.Create data source` |
| CreateSpecies (`M/CreateSpecies`)       | `M.CreateSpecies`    | `M.Create species` |
| GetSpecies (`M/GetSpecies`)             | `M.GetSpecies`       | `M.Get species` |
| GetTiltSeries (`M/GetTiltSeries`)       | `M.GetTiltSeries`    | `M.Get tilt series` |
| ModifySpecies (`M/ModifySpecies`)       | `M.ModifySpecies`    | `M.Modify species` |
| EstimateWeights (`M/EstimateWeights`)   | `M.EstimateWeights`  | `M.Estimate weights` |
| Refine (`M/Refine`)                     | `M.MRefinement`      | `M.Refine` |

- [ ] **Step 5: Common (7 files)**

| Class (file folder) | Old `TypeCategory` | New `TypeCategory` |
|---|---|---|
| ImportMap (`Import/ImportMap`)                       | `Import.Map`               | `Common.Import.Map` |
| ImportMask (`Import/ImportMask`)                     | `Import.Mask`              | `Common.Import.Mask` |
| ImportParticles (`Import/ImportParticles`)           | `Import.Particles`         | `Common.Import.Particles` |
| ImportParticlePositions (`Import/ImportParticlePositions`) | `Import.ParticlePositions` | `Common.Import.Particle positions` |
| ThresholdStatistics (`Tools/ThresholdStatistics`)   | `Tools.Threshold statistics` | `Common.Tools.Threshold statistics` |
| Note (`Notes/Note`)                                 | `Notes.Note`               | `Common.Notes.Note` |
| Vibe (`Notes/Vibe`)                                 | `Notes.Vibe`               | `Common.Notes.Vibe` |

- [ ] **Step 6: Build**

Run: `dotnet build Relay.sln`
Expected: Build succeeds (string-only edits; no signatures changed).

- [ ] **Step 7: Run the safety-net test to verify it passes**

Run: `dotnet test Refund.Tests/Refund.Tests.csproj --filter "FullyQualifiedName~JobTaxonomyTests"`
Expected: PASS (both tests). If `PopulateStatic()` threw a "Duplicate job type" for `TypeCategory`, two New values collided — re-check the table for an accidental duplicate.

- [ ] **Step 8: Commit**

```bash
git add Refund/Jobs
git commit -m "refactor: normalize job TypeCategory values onto modality-first taxonomy"
```

---

## Phase B — Move folders + rename namespaces to mirror the taxonomy

Each task moves one branch with `git mv` (preserves history), rewrites the `namespace` line in every moved `.cs`/`.razor.cs` (and any `@namespace`/`@using` in `.razor`), then leans on the compiler to surface stale `using` references elsewhere. **Procedure for every Phase-B task:**

1. `git mv` the folders as listed.
2. In each moved file, change the `namespace Refund.Jobs.<old>` line to the New namespace.
3. Run `dotnet build Relay.sln`. The compiler lists every file with a now-invalid `using Refund.Jobs.<old>` — update each to the New namespace.
4. Re-run `dotnet build Relay.sln` until it succeeds.
5. Run the safety-net test (must still PASS — proves all 48 types still register after the move).
6. Commit.

> The `Job.Types` registry is reflection-based and needs **no** edits. `TypeGuid` and `TypeCategory` are already correct from Phase A — Phase B changes only namespaces/locations.

### Task B1: Frame-series folders

**Namespace + folder moves:**

| Old folder | New folder | Old namespace | New namespace |
|---|---|---|---|
| `Jobs/Import/ImportDataSetFs` | `Jobs/FrameSeries/Import/ImportDataSetFs` | `Refund.Jobs.Import.ImportDataSetFs` | `Refund.Jobs.FrameSeries.Import.ImportDataSetFs` |
| `Jobs/Preprocessing/Motion2D` | `Jobs/FrameSeries/MotionCtf/Motion2D` | `Refund.Jobs.Preprocessing.Motion2D` | `Refund.Jobs.FrameSeries.MotionCtf.Motion2D` |
| `Jobs/Preprocessing/CTF2D` | `Jobs/FrameSeries/MotionCtf/CTF2D` | `Refund.Jobs.Preprocessing.CTF2D` | `Refund.Jobs.FrameSeries.MotionCtf.CTF2D` |
| `Jobs/Preprocessing/MotionAndCTF2D` | `Jobs/FrameSeries/MotionCtf/MotionAndCTF2D` | `Refund.Jobs.Preprocessing.MotionAndCTF2D` | `Refund.Jobs.FrameSeries.MotionCtf.MotionAndCTF2D` |
| `Jobs/Preprocessing/BoxNetInference2D` | `Jobs/FrameSeries/Picking/BoxNetInference2D` | `Refund.Jobs.Preprocessing.BoxNetInference2D` | `Refund.Jobs.FrameSeries.Picking.BoxNetInference2D` |
| `Jobs/Preprocessing/ExtractParticles2D` | `Jobs/FrameSeries/Extraction/ExtractParticles2D` | `Refund.Jobs.Preprocessing.ExtractParticles2D` | `Refund.Jobs.FrameSeries.Extraction.ExtractParticles2D` |

- [ ] **Step 1: Move folders**

```bash
mkdir -p Refund/Jobs/FrameSeries/Import Refund/Jobs/FrameSeries/MotionCtf Refund/Jobs/FrameSeries/Picking Refund/Jobs/FrameSeries/Extraction
git mv Refund/Jobs/Import/ImportDataSetFs        Refund/Jobs/FrameSeries/Import/ImportDataSetFs
git mv Refund/Jobs/Preprocessing/Motion2D        Refund/Jobs/FrameSeries/MotionCtf/Motion2D
git mv Refund/Jobs/Preprocessing/CTF2D           Refund/Jobs/FrameSeries/MotionCtf/CTF2D
git mv Refund/Jobs/Preprocessing/MotionAndCTF2D  Refund/Jobs/FrameSeries/MotionCtf/MotionAndCTF2D
git mv Refund/Jobs/Preprocessing/BoxNetInference2D Refund/Jobs/FrameSeries/Picking/BoxNetInference2D
git mv Refund/Jobs/Preprocessing/ExtractParticles2D Refund/Jobs/FrameSeries/Extraction/ExtractParticles2D
```

- [ ] **Step 2: Rewrite `namespace` lines** in every moved file (per the table above), then fix stale `using`s via the build loop (steps 3-4 of the Phase-B procedure).

- [ ] **Step 3: Build until green**

Run: `dotnet build Relay.sln`
Expected: eventually succeeds; fix each `CS0234`/`CS0246` (missing namespace/type) by repointing the `using` to the New namespace.

- [ ] **Step 4: Safety-net test still passes**

Run: `dotnet test Refund.Tests/Refund.Tests.csproj --filter "FullyQualifiedName~JobTaxonomyTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "refactor: move Frame-series jobs into Jobs/FrameSeries"
```

### Task B2: Tilt-series folders

**Moves** (`Jobs/Ts/*` → `Jobs/TiltSeries/*`; `ImportDataSetTs` joins from `Jobs/Import`). The CTF stage is flattened to `Jobs/TiltSeries/Ctf` (resolves the spec's `Ctf/Ctf` open item).

| Old folder | New folder | New namespace |
|---|---|---|
| `Jobs/Import/ImportDataSetTs` | `Jobs/TiltSeries/Import/ImportDataSetTs` | `Refund.Jobs.TiltSeries.Import.ImportDataSetTs` |
| `Jobs/Ts/Alignment/AlignAretomo` | `Jobs/TiltSeries/Alignment/AlignAretomo` | `Refund.Jobs.TiltSeries.Alignment.AlignAretomo` |
| `Jobs/Ts/Alignment/AlignEtomo` | `Jobs/TiltSeries/Alignment/AlignEtomo` | `Refund.Jobs.TiltSeries.Alignment.AlignEtomo` |
| `Jobs/Ts/Alignment/AlignMiss` | `Jobs/TiltSeries/Alignment/AlignMiss` | `Refund.Jobs.TiltSeries.Alignment.AlignMiss` |
| `Jobs/Ts/Alignment/AutoLevel` | `Jobs/TiltSeries/Alignment/AutoLevel` | `Refund.Jobs.TiltSeries.Alignment.AutoLevel` |
| `Jobs/Ts/Alignment/PeakAlign` | `Jobs/TiltSeries/Alignment/PeakAlign` | `Refund.Jobs.TiltSeries.Alignment.PeakAlign` |
| `Jobs/Ts/Alignment/StackTilts` | `Jobs/TiltSeries/Alignment/StackTilts` | `Refund.Jobs.TiltSeries.Alignment.StackTilts` |
| `Jobs/Ts/Alignment/ImportAlignments` | `Jobs/TiltSeries/Alignment/ImportAlignments` | `Refund.Jobs.TiltSeries.Alignment.ImportAlignments` |
| `Jobs/Ts/Ctf` | `Jobs/TiltSeries/Ctf` | `Refund.Jobs.TiltSeries.Ctf` |
| `Jobs/Ts/Reconstruction/ReconstructTomograms` | `Jobs/TiltSeries/Reconstruction/ReconstructTomograms` | `Refund.Jobs.TiltSeries.Reconstruction.ReconstructTomograms` |
| `Jobs/Ts/Reconstruction/ReconstructMap` | `Jobs/TiltSeries/Reconstruction/ReconstructMap` | `Refund.Jobs.TiltSeries.Reconstruction.ReconstructMap` |
| `Jobs/Ts/Denoising` | `Jobs/TiltSeries/Reconstruction/Denoising` | `Refund.Jobs.TiltSeries.Reconstruction.Denoising` |
| `Jobs/Ts/TemplateMatch` | `Jobs/TiltSeries/Picking/TemplateMatch` | `Refund.Jobs.TiltSeries.Picking.TemplateMatch` |
| `Jobs/Ts/ExtractParticles` | `Jobs/TiltSeries/Extraction/ExtractParticles` | `Refund.Jobs.TiltSeries.Extraction.ExtractParticles` |
| `Jobs/Ts/DeselectTilts` | `Jobs/TiltSeries/Selection/DeselectTilts` | `Refund.Jobs.TiltSeries.Selection.DeselectTilts` |
| `Jobs/Ts/SelectTomograms` | `Jobs/TiltSeries/Selection/SelectTomograms` | `Refund.Jobs.TiltSeries.Selection.SelectTomograms` |
| `Jobs/Ts/SelectParticles` | `Jobs/TiltSeries/Selection/SelectParticles` | `Refund.Jobs.TiltSeries.Selection.SelectParticles` |

> `AlignAretomo`'s old namespace is `Refund.Jobs.Ts.Alignment.AlignAreTomo` (note the `AreTomo` casing mismatch with its folder). The New namespace normalizes it to `...AlignAretomo` to match the folder — fix any `using ...AlignAreTomo` accordingly.

- [ ] **Step 1: Move folders**

```bash
mkdir -p Refund/Jobs/TiltSeries/Import Refund/Jobs/TiltSeries/Alignment Refund/Jobs/TiltSeries/Reconstruction Refund/Jobs/TiltSeries/Picking Refund/Jobs/TiltSeries/Extraction Refund/Jobs/TiltSeries/Selection
git mv Refund/Jobs/Import/ImportDataSetTs Refund/Jobs/TiltSeries/Import/ImportDataSetTs
git mv Refund/Jobs/Ts/Alignment/* Refund/Jobs/TiltSeries/Alignment/
git mv Refund/Jobs/Ts/Ctf Refund/Jobs/TiltSeries/Ctf
git mv Refund/Jobs/Ts/Reconstruction/* Refund/Jobs/TiltSeries/Reconstruction/
git mv Refund/Jobs/Ts/Denoising Refund/Jobs/TiltSeries/Reconstruction/Denoising
git mv Refund/Jobs/Ts/TemplateMatch Refund/Jobs/TiltSeries/Picking/TemplateMatch
git mv Refund/Jobs/Ts/ExtractParticles Refund/Jobs/TiltSeries/Extraction/ExtractParticles
git mv Refund/Jobs/Ts/DeselectTilts Refund/Jobs/TiltSeries/Selection/DeselectTilts
git mv Refund/Jobs/Ts/SelectTomograms Refund/Jobs/TiltSeries/Selection/SelectTomograms
git mv Refund/Jobs/Ts/SelectParticles Refund/Jobs/TiltSeries/Selection/SelectParticles
```

- [ ] **Step 2: Rewrite `namespace` lines** in all moved files per the table, then run the build loop to fix stale `using`s.

- [ ] **Step 3: Build until green** — `dotnet build Relay.sln`

- [ ] **Step 4: Safety-net test still passes** — `dotnet test Refund.Tests/Refund.Tests.csproj --filter "FullyQualifiedName~JobTaxonomyTests"`

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "refactor: move Tilt-series jobs into Jobs/TiltSeries"
```

### Task B3: Refinement folders

| Old folder | New folder | New namespace |
|---|---|---|
| `Jobs/3D/InitialReference3D` | `Jobs/Refinement/InitialModel/InitialReference3D` | `Refund.Jobs.Refinement.InitialModel.InitialReference3D` |
| `Jobs/2D/Class2D` | `Jobs/Refinement/Classes2D/Class2D` | `Refund.Jobs.Refinement.Classes2D.Class2D` |
| `Jobs/2D/Class2DSelect` | `Jobs/Refinement/Classes2D/Class2DSelect` | `Refund.Jobs.Refinement.Classes2D.Class2DSelect` |
| `Jobs/3D/Class3D` | `Jobs/Refinement/Classes3D/Class3D` | `Refund.Jobs.Refinement.Classes3D.Class3D` |
| `Jobs/3D/Class3DSelect` | `Jobs/Refinement/Classes3D/Class3DSelect` | `Refund.Jobs.Refinement.Classes3D.Class3DSelect` |
| `Jobs/3D/Refine3D` | `Jobs/Refinement/Refinement3D/Refine3D` | `Refund.Jobs.Refinement.Refinement3D.Refine3D` |
| `Jobs/Tools/CreateMask` | `Jobs/Refinement/Masks/CreateMask` | `Refund.Jobs.Refinement.Masks.CreateMask` |
| `Jobs/PostProcessing/PostProcess3D` | `Jobs/Refinement/PostProcess/PostProcess3D` | `Refund.Jobs.Refinement.PostProcess.PostProcess3D` |

> The `3D/Class3D` folder also contains `Class3DContinue` and `Class3DSupervised`; they move with the folder and share the `...Classes3D.Class3D` namespace.

- [ ] **Step 1: Move folders**

```bash
mkdir -p Refund/Jobs/Refinement/InitialModel Refund/Jobs/Refinement/Classes2D Refund/Jobs/Refinement/Classes3D Refund/Jobs/Refinement/Refinement3D Refund/Jobs/Refinement/Masks Refund/Jobs/Refinement/PostProcess
git mv Refund/Jobs/3D/InitialReference3D Refund/Jobs/Refinement/InitialModel/InitialReference3D
git mv Refund/Jobs/2D/Class2D            Refund/Jobs/Refinement/Classes2D/Class2D
git mv Refund/Jobs/2D/Class2DSelect      Refund/Jobs/Refinement/Classes2D/Class2DSelect
git mv Refund/Jobs/3D/Class3D            Refund/Jobs/Refinement/Classes3D/Class3D
git mv Refund/Jobs/3D/Class3DSelect      Refund/Jobs/Refinement/Classes3D/Class3DSelect
git mv Refund/Jobs/3D/Refine3D           Refund/Jobs/Refinement/Refinement3D/Refine3D
git mv Refund/Jobs/Tools/CreateMask      Refund/Jobs/Refinement/Masks/CreateMask
git mv Refund/Jobs/PostProcessing/PostProcess3D Refund/Jobs/Refinement/PostProcess/PostProcess3D
```

- [ ] **Step 2: Rewrite `namespace` lines** (note old `_2D`/`_3D` prefixes become `Refinement.Classes2D`/`Classes3D`/etc.), then run the build loop.

- [ ] **Step 3: Build until green** — `dotnet build Relay.sln`

- [ ] **Step 4: Safety-net test still passes** — `dotnet test Refund.Tests/Refund.Tests.csproj --filter "FullyQualifiedName~JobTaxonomyTests"`

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "refactor: move RELION refinement jobs into Jobs/Refinement"
```

### Task B4: Common folders + clean up empty/old dirs

`M/*` folders **do not move** (namespaces stay `Refund.Jobs.M.*`); only their `TypeCategory` changed in Phase A.

| Old folder | New folder | New namespace |
|---|---|---|
| `Jobs/Import/ImportMap` | `Jobs/Common/Import/ImportMap` | `Refund.Jobs.Common.Import.ImportMap` |
| `Jobs/Import/ImportMask` | `Jobs/Common/Import/ImportMask` | `Refund.Jobs.Common.Import.ImportMask` |
| `Jobs/Import/ImportParticles` | `Jobs/Common/Import/ImportParticles` | `Refund.Jobs.Common.Import.ImportParticles` |
| `Jobs/Import/ImportParticlePositions` | `Jobs/Common/Import/ImportParticlePositions` | `Refund.Jobs.Common.Import.ImportParticlePositions` |
| `Jobs/Tools/ThresholdStatistics` | `Jobs/Common/Tools/ThresholdStatistics` | `Refund.Jobs.Common.Tools.ThresholdStatistics` |
| `Jobs/Notes/Note` | `Jobs/Common/Notes/Note` | `Refund.Jobs.Common.Notes.Note` |
| `Jobs/Notes/Vibe` | `Jobs/Common/Notes/Vibe` | `Refund.Jobs.Common.Notes.Vibe` |

- [ ] **Step 1: Move folders**

```bash
mkdir -p Refund/Jobs/Common/Import Refund/Jobs/Common/Tools Refund/Jobs/Common/Notes
git mv Refund/Jobs/Import/ImportMap               Refund/Jobs/Common/Import/ImportMap
git mv Refund/Jobs/Import/ImportMask              Refund/Jobs/Common/Import/ImportMask
git mv Refund/Jobs/Import/ImportParticles         Refund/Jobs/Common/Import/ImportParticles
git mv Refund/Jobs/Import/ImportParticlePositions Refund/Jobs/Common/Import/ImportParticlePositions
git mv Refund/Jobs/Tools/ThresholdStatistics      Refund/Jobs/Common/Tools/ThresholdStatistics
git mv Refund/Jobs/Notes/Note                     Refund/Jobs/Common/Notes/Note
git mv Refund/Jobs/Notes/Vibe                     Refund/Jobs/Common/Notes/Vibe
```

- [ ] **Step 2: Remove now-empty old directories** (the `2D`, `3D`, `Import`, `Preprocessing`, `Ts`, `Tools`, `Notes`, `PostProcessing` containers, plus the empty `M/RemoveDataSource` and `M/RemoveSpecies` placeholders):

```bash
rmdir Refund/Jobs/M/RemoveDataSource Refund/Jobs/M/RemoveSpecies 2>/dev/null
find Refund/Jobs -type d -empty -delete
```

- [ ] **Step 3: Rewrite `namespace` lines** for the moved Common files, then run the build loop.

- [ ] **Step 4: Build until green** — `dotnet build Relay.sln`

- [ ] **Step 5: Full test suite passes** — `dotnet test Refund.Tests/Refund.Tests.csproj`
Expected: all tests PASS (taxonomy + pre-existing tests).

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "refactor: move shared utility jobs into Jobs/Common; drop empty dirs"
```

---

## Final verification

- [ ] **Step 1: Clean build** — `dotnet build Relay.sln` → succeeds with no warnings about unresolved `Refund.Jobs.*` namespaces.
- [ ] **Step 2: Full suite** — `dotnet test Refund.Tests/Refund.Tests.csproj` → all green.
- [ ] **Step 3: Folder layout sanity** — `find Refund/Jobs -maxdepth 2 -type d | sort` shows exactly `FrameSeries`, `TiltSeries`, `Refinement`, `M`, `Common` (plus `Abstract.cs` at the root) and no leftover `2D`/`3D`/`Ts`/`Preprocessing`/`Import`/`Tools`/`Notes`/`PostProcessing`.
- [ ] **Step 4: Smoke-test the menu** — launch the app, open the job-type menu, confirm the five top-level groups render with the new labels and every stage/leaf appears once (no duplicate groups from spelling drift).

## Notes / risks

- **Razor files:** job folders contain `.razor` + `.razor.cs` views. `git mv` moves them all; `.razor.cs` files need the same `namespace` edit. If a `.razor` declares `@namespace` or other files `@using Refund.Jobs.<old>`, the build/Razor compiler will flag them — fix in the same loop.
- **No `TypeGuid` edits**: confirmed none of the move steps touch `TypeGuid`, so saved projects deserialize unchanged. Do not "tidy" guids.
- **Menu ordering** is insertion-order (reflection order), unchanged by this work; if deterministic top-level ordering is desired later, that's a separate change.
- **`HideFromMenu`**: if any job carries `[HideFromMenu]` it is excluded from `TypeHierarchy` (`Job.cs:773`) but still in `Job.Types`; the `EveryJobType_HasExpectedTypeCategory` test covers it regardless, while `MenuHierarchy_HasFiveTopLevelGroups` only sees menu-visible groups (all five branches have visible jobs).
```
