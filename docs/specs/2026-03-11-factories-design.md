# Factories — Design Specification

## Overview

Factories are meta-jobs containing a pre-defined job graph. A **factory definition** is a space-level blueprint that can be instantiated multiple times. Each **factory instance** materializes the blueprint as real jobs, allowing users to run complex multi-step workflows with a single action while exposing only the parameters and ports they care about.

Factories sit between jobs (single processing steps) and full manual workflows, offering repeatable, configurable pipelines with frozen topology and selective parameter exposure.

---

## Core Concepts

### Factory Definition

A space-level entity containing:

- **Sub-job blueprints**: Serialized as regular jobs (using `Job.WritePolymorphicJson`), but NOT added to `Space._Jobs`. These serve as templates for instantiation.
- **Internal edges**: Connections between sub-jobs within the definition.
- **External edges**: Fixed references to existing jobs outside the factory (e.g., an Import Map job). In the factory builder, sub-job input ports can be connected to output ports of existing jobs in the space — these connections are captured as external edges. They are baked into the definition and cannot be changed after completion. When instantiated, real edges to the same external jobs are created automatically.
- **Exposed input ports**: Input ports on sub-jobs surfaced to the factory card, allowing users to connect external data at instance time.
- **Exposed output ports**: Output ports on sub-jobs surfaced to the factory card, allowing downstream jobs to consume factory results.
- **Exposed properties**: Sub-job parameters surfaced to the factory editor, configurable per instance.
- **Queue pre-selections**: Optional per-sub-job queue assignments used as defaults at runtime.
- **Diagram layout**: Frozen layout used for the factory builder, instance minimap, and instance browsing.

### Factory Instance

A space-level entity implementing `IFolderContent`:

- References a factory definition by ID.
- On creation, immediately clones all sub-job blueprints as real `Job` objects in `Space._Jobs` (all in Building status), creates all internal and fixed external edges, then opens the factory editor for configuration.
- Sub-jobs get regular space-level IDs and have `FactoryInstanceId` set.
- Sub-job aliases are inherited from the definition blueprints and are non-editable in instances.
- Topology and unexposed parameters are frozen.
- Can appear in views and folders (0 or 1 times per view, same as regular jobs).
- The definition's alias serves as the "type name" on the FactoryCard header (analogous to how "Refine3D" appears on a Refine3D JobCard). The instance's own optional alias appears as an overlay, same as job aliases.

---

## Data Model

### New Classes

#### `FactoryDefinition`

```
Properties:
  Id: int                                    // unique within space (own sequence)
  Alias: string                              // user-editable, pre-filled "Factory X"
  Status: FactoryDefinitionStatus            // Building | Complete
  SubJobs: List<Job>                         // blueprints with local IDs
  InternalEdges: List<FactoryEdge>           // simple record, see below
  ExternalEdges: List<FactoryExternalEdge>   // simple record, see below
  ExposedPortsIn: List<ExposedPort>          // exposed input ports
  ExposedPortsOut: List<ExposedPort>         // exposed output ports
  ExposedProperties: List<ExposedProperty>   // exposed sub-job parameters
  QueueAssignments: Dictionary<int, int?>    // sub-job blueprint ID → queue ID (optional)
  DiagramLayout: DiagramLayout               // frozen layout
```

#### `FactoryInstance`

```
Properties:
  Id: int                    // unique within space (own sequence)
  DefinitionId: int          // reference to FactoryDefinition
  Alias: string?             // optional user alias
  ColorTag: string?          // hex color
  Notes: string?             // user notes
  SubJobIds: List<int>       // references to real jobs in Space._Jobs
  UpdateDate: DateTime       // last modification timestamp
  Events: List<JobEvent>     // lifecycle event log (created, run started, etc.)

Computed:
  AggregateStatus: JobStatus // worst-of sub-job statuses
  Definition: FactoryDefinition
  SubJobs: List<Job>         // resolved from SubJobIds
```

Implements `IFolderContent` (has `int Id`). `ReadOnlyFactoryInstance` implements `IViewItem` (which extends `IIdentifiable`, `IAnnotated`, `IAudited`) — `HeroImage` returns null, `QualifiedName` returns `"FI{Id}"`, and audit properties delegate to `Events` and `UpdateDate`.

#### `ExposedPort`

```
  CustomName: string         // pre-filled with original port alias, user-editable in builder
  SubJobId: int              // blueprint-local sub-job ID
  PortName: string
  ResourceType: Type         // cached for rendering
```

Collection membership (ExposedPortsIn vs ExposedPortsOut) implies direction.

#### `ExposedProperty`

```
  CustomName: string         // pre-filled with original property label, user-editable in builder
  SubJobId: int              // blueprint-local sub-job ID
  PropertyName: string
```

#### `FactoryEdge` (record type)

```
  Source: string             // "subJobId.portName" format
  Target: string             // "subJobId.portName" format
```

#### `FactoryExternalEdge` (record type)

```
  SubJobId: int              // blueprint-local sub-job ID
  SubJobPort: string         // port name on the sub-job
  ExternalJobId: int         // space-level job ID of the external job
  ExternalPort: string       // port name on the external job
```

### Modified Classes

#### `Space`

- Add `_FactoryDefinitions: List<FactoryDefinition>` with `ReadOnlyCollection` accessor.
- Add `_FactoryInstances: List<FactoryInstance>` with `ReadOnlyCollection` accessor.
- Remove old `_Factories: List<Factory>` and the `Factory` class.
- Add methods: `CreateFactoryDefinition()`, `DeleteFactoryDefinition()`, `CreateFactoryInstance()`, `DeleteFactoryInstance()`.

#### `View`

- Add `_FactoryInstances: List<FactoryInstance>` — flat list of all factory instances in this view, regardless of folder placement (same role as `_Jobs`).
- Support `FactoryInstance` in `_RootItems` and `Folder._Items` (both are `List<IFolderContent>`).
- Add `AddFactoryInstance()`, `RemoveFactoryInstance()`, `MoveFactoryInstanceToFolder()`.
- Update `FolderContentExtensions.AsReadOnlyViewItem()` to handle `FactoryInstance` (currently only matches `Job` and `Folder`).
- Update serialization: `Items` arrays in Views and Folders must handle `"FactoryInstance"` type string alongside `"Job"` and `"Folder"`. Both `View.ReadFromJson()` and `Folder.ResolveItems()` need updating.

#### `Job`

- Add `FactoryInstanceId: int?` — marks this job as a sub-job of a factory instance. `null` for regular jobs. Used to exclude from `_RootItems` and identify ownership.

#### `ItemType` enum

- Add `FactoryInstance` value.

#### `IFolderContent`

- `FactoryInstance` implements it (already has `int Id`).

### ReadOnly Wrappers

- `ReadOnlyFactoryDefinition` (sealed) — following existing `ConditionalWeakTable` caching pattern.
- `ReadOnlyFactoryInstance` (sealed, implements `IViewItem`) — same pattern.

### ID Sequences

- `FactoryDefinition.Id`: separate sequence within space (`max + 1` of definitions).
- `FactoryInstance.Id`: separate sequence within space (`max + 1` of instances).
- Sub-job blueprint IDs: local to the definition (1, 2, 3...).
- Materialized sub-job IDs: regular `Job.Id` sequence (shared with all jobs in the space).

---

## Serialization

### In `space.relay`

```json
{
  "Jobs": [ /* regular jobs + factory sub-jobs (with FactoryInstanceId set) */ ],
  "Edges": [ /* all edges including factory internal + external */ ],
  "Views": [ /* views referencing factory instances via Items array */ ],
  "FactoryDefinitions": [
    {
      "Id": 1,
      "Alias": "Preprocessing Pipeline",
      "Status": "Complete",
      "SubJobs": [
        { "Type": "guid1, Import.Fs", "Job": { /* RelayProperty fields */ } },
        { "Type": "guid2, Motion.MotionCorr", "Job": { /* ... */ } }
      ],
      "InternalEdges": [
        { "Source": "1.Frames", "Target": "2.InputFrames" }
      ],
      "ExternalEdges": [
        { "SubJobId": 2, "SubJobPort": "InputMap", "ExternalJobId": 42, "ExternalPort": "Map" }
      ],
      "ExposedPortsIn": [
        { "CustomName": "Raw Frames", "SubJobId": 1, "PortName": "Frames" }
      ],
      "ExposedPortsOut": [
        { "CustomName": "Corrected Micrographs", "SubJobId": 2, "PortName": "Micrographs" }
      ],
      "ExposedProperties": [
        { "CustomName": "Pixel Size", "SubJobId": 1, "PropertyName": "PixelSize" }
      ],
      "QueueAssignments": { "2": 3 },
      "DiagramLayout": { /* standard DiagramLayout */ }
    }
  ],
  "FactoryInstances": [
    {
      "Id": 1,
      "DefinitionId": 1,
      "Alias": "Run #3",
      "ColorTag": "#FF6B6B",
      "SubJobIds": [55, 56]
    }
  ]
}
```

### Key Decisions

- **Sub-job blueprints**: Use `Job.WritePolymorphicJson()` / `Job.CreateFromPolymorphicJson()`. On deserialization, blueprints get their `Space` reference set (for port reconstruction) but are NOT added to `Space._Jobs`.
- **Internal edges**: Use `"jobId.portName"` format, referencing blueprint-local IDs.
- **Blueprint ID → real ID mapping**: During instantiation, a mapping is built to wire up edges correctly.
- **View Items array**: Extends existing format with `{"Type":"FactoryInstance","Id":N}`.
- **Deserialization order**: FactoryDefinitions → Jobs → FactoryInstances → Edges → Views.

---

## UI Components

### New Components

#### `FactoryCard`

Unified component for both definition cards (space panel) and instance cards (view).

**Visual design:**
- Double border (outline + border with gap) for "nested container" feel.
- JobCard-style header: status dot + instance/definition ID + definition name.
- Optional alias overlay below header.
- Exposed port dots: inputs on left edge, outputs on right edge, colored by resource type.
- Minimap showing sub-job graph with per-job status coloring.
- 1 square wide, dynamic height based on `max(exposed input ports, exposed output ports)`.

**Mode parameter:**
- Definition mode (space panel): no ports, shows Building/Complete status, double-click opens builder.
- Instance mode (view): exposed ports, aggregate status, double-click browses into instance.

#### `FactoryEditor`

Right panel component for factory instance editing.

- Exposed properties grouped by sub-job in accordion sections.
- Properties disabled for sub-jobs not in Building status.
- Exposed input ports with connection display (like `JobPortDisplay`).
- "Queue..." button opening the queue wizard.

#### `FactoryQueueWizard`

Replaces editor content when triggered via the "Queue..." button.

- Table: sub-job name | queue type required | queue dropdown.
- Local queue jobs are auto-assigned and do not appear in the table (there is only one local queue).
- Pre-filled from definition's `QueueAssignments` as defaults.
- Flagged rows for queue type mismatches (e.g., exposed parameter change made a CPU job need GPU).
- Confirm button disabled while mismatches or unassigned queues exist.
- On confirm, all Building sub-jobs are queued with their assigned queues.

#### `FactoryOutputPortDisplay`

New section for the JobEditor in factory builder mode.

- Lists output ports of the selected sub-job.
- Exposure toggle + custom name field per port.
- Only rendered in builder mode.

#### `FactoryDefinitionPanel`

Collapsible panel on the left side of the space screen.

- Vertically scrolling list of `FactoryCard` components in definition mode.
- "New factory" button.
- Click to select, double-click to open builder.
- Context menu: Clone, Delete.

### Modified Components

#### `ViewScreen`

Refactored with `IViewScreenMode` abstraction:

- `RegularViewMode`: current behavior (folder browsing is a state within this).
- `FactoryBuilderMode`: restricted context menus, builder toolbar, exposure-augmented editor, no job execution.
- `FactoryInstanceBrowseMode`: frozen topology, restricted parameter editing, individual sub-job Run/Abort/Clear allowed.

Each mode defines: `GetItems()`, toolbar controls, context menu filtering, editor configuration, editability flags.

#### `ViewToolbar`

Mode-aware:

- Regular: current sorting/view controls.
- Builder: "Complete" button (with validation), definition alias editor.

#### `JobEditor`

Mode-aware additions in factory builder mode:

- Property rows: favorite button replaced with exposure toggle + custom name field.
- Input ports section: exposure toggle + custom name per port.
- Output ports section added (via `FactoryOutputPortDisplay`).
- Run/queue buttons hidden.

#### `JobTypeMenu`

Add "Factories" category populated from `Space.FactoryDefinitions.Where(d => d.Status == Complete)`.

#### `MenuActionService`

Split into focused builders:

- `JobMenuActionBuilder`: existing logic + "Create factory from selection" for multi-job selection.
- `FolderMenuActionBuilder`: existing logic extracted.
- `FactoryInstanceMenuActionBuilder`: Run, Abort, Clear failed/aborted, Clear all, Convert to folder, Clone, Delete, Color, Add/Move to view.
- `FactoryDefinitionMenuActionBuilder`: Clone, Delete.

#### `Breadcrumbs`

New path segments:

- Builder: `Project > Space > [Factory Definition Name]`
- Instance browse: `Project > Space > View > [Folder? >] [Factory Instance Name]`

#### `DiagramView`

Render virtual edges for FactoryCard exposed ports based on underlying sub-job real edges.

#### `JobCard`

In FactoryInstanceBrowseMode: context menu allows Run, Abort, Clear for individual sub-jobs. Topology-modifying actions (delete, connect) disabled.

---

## Services & Events

### New Services

#### `EditorService` (replaces `JobEditorService`)

Unified right panel editor management.

- `CurrentTarget: ReadOnlyJob | ReadOnlyFactoryInstance | null`
- `EditorMode: Regular | FactoryBuilder | FactoryInstanceBrowse`
- Events: `OnTargetChanged`, `OnTargetUpdated`
- Prevents conflicts between job and factory instance editing.

#### `FactoryBuilderService`

Tracks factory definition being built.

- `CurrentDefinition: ReadOnlyFactoryDefinition`
- `IsBuilding: bool`
- Methods: `OpenBuilder(definition)`, `CloseBuilder()`, `CompleteDefinition()`
- Validation for completion: at least one sub-job, no cycles, external edge targets exist.

### New DataManager Partials

#### `DataManager.FactoryDefinition.cs`

- `CreateFactoryDefinition(user, space)` → empty definition, Building status.
- `CreateFactoryDefinitionFromJobs(user, space, jobs, edges)` → clones selected jobs/edges, captures incoming external edges automatically.
- `UpdateFactoryDefinition(user, definition, updateAction)` → parameter changes, exposure toggling.
- `CompleteFactoryDefinition(user, definition)` → validates, transitions to Complete.
- `CloneFactoryDefinition(user, definition)` → deep copy, Building status.
- `DeleteFactoryDefinition(user, definition)` → blocked if instances exist.

#### `DataManager.FactoryInstance.cs`

- `CreateFactoryInstance(user, view, definitionId, targetFolder?)` → clones sub-jobs into `Space._Jobs` (all in Building status), creates internal edges, creates fixed external edges, returns instance.
- `DeleteFactoryInstance(user, instance)` → validates each sub-job deletion would succeed, deletes sub-jobs leaves→roots, removes instance from all views.
- `ConvertFactoryInstanceToFolder(user, instance, view)` → creates Folder, moves sub-jobs into it (clearing their `FactoryInstanceId`), removes factory instance. No edge rewiring needed since edges already point directly at sub-jobs.
- `RunFactoryInstance(user, instance, queueAssignments)` → queues each Building sub-job with assigned queue.
- `AbortFactoryInstance(user, instance)` → aborts all active sub-jobs.
- `ClearFailedFactoryInstance(user, instance)` → clears only Failed/Aborted sub-jobs.
- `ClearFactoryInstance(user, instance)` → clears all sub-jobs.
- `CloneFactoryInstance(user, instance, view)` → deep copy with current sub-job state.

#### `DataManager.Job.cs` (modified)

- `DeleteJob` validation: check if job is referenced by any factory definition's external edges. Block deletion if referenced.

### New Events

```
FactoryDefinitionCreated:  GroupEvent<ReadOnlyFactoryDefinition>
FactoryDefinitionUpdated:  GroupEvent<ReadOnlyFactoryDefinition>
FactoryDefinitionDeleted:  GroupEvent<ReadOnlyFactoryDefinition>

FactoryInstanceCreated:    GroupEvent<ReadOnlyFactoryInstance>
FactoryInstanceUpdated:    GroupEvent<ReadOnlyFactoryInstance>
FactoryInstanceDeleted:    GroupEvent<ReadOnlyFactoryInstance>
```

Group naming: `P{projectId}_S{spaceId}_FD{definitionId}` and `P{projectId}_S{spaceId}_FI{instanceId}` with wildcards.

### Modified Services

#### `RelaySession`

- Add `FactoryDefinition: ReadOnlyFactoryDefinition?` (builder context, mutually exclusive with View).
- Add `FactoryInstance: ReadOnlyFactoryInstance?` (browse context, within a View like Folder).
- Add `OnFactoryDefinitionChanged`, `OnFactoryInstanceChanged` events.
- Navigation methods for `Sx/FDx` and `Sx/Vx/[Fx/]FIx` routes.

#### `CardSelectionService`

- Add `FactoryInstance` to supported `ItemType`s.
- Add `SelectionKey.ForFactoryInstance(int id)` factory method.
- Auto-clear on factory context changes.

#### `DataRepository`

- Add serialization/deserialization for `FactoryDefinitions` and `FactoryInstances` within `SaveSpace`/`LoadSpace`.

---

## Behavioral Rules

### Factory Definition Lifecycle

1. **Creation**: Empty (space panel) or from job selection (context menu). Status = Building.
2. **Editing**: Full topology and parameter editing in factory builder. Add/remove sub-jobs (same right-click-on-empty-space and click-on-port interactions as regular views), create/delete edges, toggle exposure mappings, set queue pre-selections, edit sub-job aliases.
3. **Completion**: Validates: at least one sub-job, no cycles, external edge targets exist. Status = Complete.
4. **Immutability**: Once at least one instance exists, the definition cannot be edited or deleted. User can clone the definition to create a modified version.
5. **Deletion**: Blocked while instances exist.

### Factory Instance Lifecycle

1. **Creation**: From job creation menu → Factories. Clones blueprints as real jobs (Building), creates edges.
2. **Configuration**: Factory editor in right panel. Set exposed parameters (only for Building sub-jobs), connect exposed input ports.
3. **Queue assignment**: "Queue..." → wizard with sub-job table and queue dropdowns.
4. **Execution**: Building sub-jobs queued. `IsReadyToStage()` handles dependency ordering naturally.
5. **Individual sub-job control**: Run, Abort, Clear individual sub-jobs from within instance browsing.
6. **Re-running**: Clear all/failed → modify exposed parameters on Building sub-jobs → queue again.
7. **Conversion to folder**: One-way. Container becomes Folder, sub-jobs become regular children (their `FactoryInstanceId` cleared), all parameters become editable, definition reference lost. Since edges already point directly at sub-jobs, no edge rewiring needed — the sub-jobs simply become visible as folder contents.

### Deletion Guards

| Entity | Guard |
|--------|-------|
| Job referenced by factory definition external edge | Block deletion |
| Factory definition with existing instances | Block deletion |
| Factory instance sub-job (direct deletion from view) | Block — must delete through factory instance |
| Factory instance | Validates each sub-job deletion succeeds, deletes leaves→roots |

### Edge Behavior

- **Internal edges**: Created during instantiation from blueprints. Frozen in instances.
- **External fixed edges**: From definition, pointing to existing external jobs. Created during instantiation. Frozen.
- **User-connected edges**: Created at instance time via FactoryCard exposed port dots. Clicking an exposed port dot on the FactoryCard creates a real edge to/from the underlying sub-job port. These edges can be modified or deleted by the user.
- **Virtual edge rendering**: DiagramView draws visual edges from external jobs to the FactoryCard (and vice versa) based on actual sub-job edge data. The real edges exist on the sub-jobs; the FactoryCard only visualizes them as if they were its own.
- **Convert to folder**: When a factory instance is converted to a folder, exposed port proxy edges disappear (folders don't have ports). Since the edges already point directly at the sub-jobs, and the sub-jobs become visible folder contents, the edges are naturally correct — no rewiring needed.

### Sub-Job Visibility

- Sub-jobs are in `Space._Jobs` and `View._Jobs` but NOT in `View._RootItems` or `Folder._Items`.
- `CreateFactoryInstance` adds sub-jobs to both `Space._Jobs` and the target `View._Jobs` (needed for edge resolution and job lookup).
- Only visible when browsing into a factory instance.
- `FactoryInstanceId` marks ownership.
- Excluded from view-level diagram layout computation (the FactoryCard is the single node representing the instance in the view's layout, like a Folder).
- Visible in queue monitoring (OverlayQueues) like regular jobs.
- When a factory instance is added to another view, its sub-jobs must also be added to that view's `_Jobs` list.

### Aggregate Status

Computed on-the-fly from sub-job statuses (not stored). The FactoryCard subscribes to its sub-jobs' status change events and recomputes.

Priority (worst wins): `Failed > Aborting > Aborted > Running > Finalizing > Staging > Waiting > Clearing > Building > Finished`

Transient states (`Aborting`, `Finalizing`, `Clearing`) are included since sub-jobs can be observed in these states during daemon ticks. `Deleted` is excluded — a deleted sub-job indicates a broken factory instance.

### ViewScreen Mode Behavior

| Capability | Regular View | Factory Builder | Instance Browse |
|-----------|-------------|----------------|-----------------|
| Add/delete jobs | Yes | Yes | No |
| Create edges | Yes | Yes | No |
| Edit all parameters | Yes | Yes | No |
| Run/Abort/Clear individual jobs | Yes | No | Yes |
| Edit exposed params (Building sub-jobs) | N/A | N/A | Yes |
| Edit unexposed params | N/A | N/A | No (disabled) |
| Folder creation | Yes | No | No |
| Factory instance creation | Yes | No | No |
| Exposure toggles in editor | No | Yes | No |
| Completion button in toolbar | No | Yes | No |
| Diagram/list mode toggle | Yes | Yes | Yes |

### URL Navigation

| Context | URL Pattern | Example |
|---------|------------|---------|
| Factory builder | `/P{pid}/S{sid}/FD{defId}` | `/P1/S2/FD3` |
| Factory instance browse | `/P{pid}/S{sid}/V{vid}/FI{instId}` | `/P1/S2/V1/FI5` |
| Instance in folder | `/P{pid}/S{sid}/V{vid}/F{fid}/FI{instId}` | `/P1/S2/V1/F3/FI5` |

### Create Factory From Selection

1. User selects one or more jobs in a view (with connecting edges).
2. Right-click → "Create factory from selection".
3. Selected jobs are cloned into a new factory definition as blueprints (originals remain untouched in the view). All parameters including aliases are copied.
4. Edges between selected jobs become internal edges.
5. Incoming edges from non-selected parent jobs become fixed external edges.
6. Outgoing edges to non-selected children are not captured.
7. Factory builder opens for the new definition with a pre-filled default name.

---

## Architectural Refactoring

### ViewScreen Mode Abstraction

Extract `IViewScreenMode` interface with concrete implementations:

```
IViewScreenMode
├── RegularViewMode (current behavior, folder browsing is state within)
├── FactoryBuilderMode (restricted actions, completion controls, exposure toggles)
└── FactoryInstanceBrowseMode (frozen topology, individual sub-job control)
```

Each mode defines: item source, toolbar configuration, context menu filtering, editor behavior, editability flags.

### EditorService Unification

Replace `JobEditorService` with unified `EditorService`:

- Tracks `CurrentTarget: Job | FactoryInstance | null`
- `EditorMode: Regular | FactoryBuilder | FactoryInstanceBrowse`
- Right panel renders `JobEditor` or `FactoryEditor` based on target type.
- Single service manages panel open/close, preventing conflicts.

### MenuActionService Decomposition

Split into focused builders:

```
MenuActionService (orchestrator)
├── JobMenuActionBuilder (+ "Create factory from selection")
├── FolderMenuActionBuilder
├── FactoryInstanceMenuActionBuilder
└── FactoryDefinitionMenuActionBuilder
```

---

## Constraints & Non-Goals

- **No sub-factories**: Factory definitions cannot contain other factory instances.
- **No sub-folders inside factories**: Factory definitions cannot contain folders.
- **No nesting**: Single level of containment only.
- **No factory-to-factory conversion**: Only factory-to-folder (one-way).
- **No definition editing after instances exist**: Clone and modify instead.
- **Existing queue/daemon system unchanged**: Sub-jobs are real jobs; `IsReadyToStage()` handles dependency ordering naturally.
- **Existing edge system unchanged**: Real edges to sub-job ports; FactoryCard renders virtual proxy edges for display.