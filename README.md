# Relay

Relay is a web-based platform for cryo-EM data processing workflows. It provides a visual interface for building, running, and monitoring computational pipelines that process electron microscopy data — from raw micrographs through to 3D reconstructions.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Python 3.10+ (for the Bakery visualization package)

## Clone

```bash
git clone --recurse-submodules https://github.com/warpem/relay.git
cd relay
```

If you already cloned without `--recurse-submodules`:

```bash
git submodule update --init --recursive
```

## Build

```bash
dotnet build
```

To publish a self-contained deployment:

```bash
dotnet publish Relay -c Release -o publish/
```

## Install Bakery (visualization)

Bakery generates plots and thumbnails for job results. Install it into a Python environment:

```bash
pip install -e Bakery
```

Ensure the `bakery` command is on your `PATH` when running Relay.

## Configure

Relay loads configuration from two sources:

1. **Built-in defaults** — `Relay/appsettings.json` (ships with the application)
2. **Local overrides** — `relay.json` in the working directory (your site-specific settings)

### Authentication

Relay supports two authentication modes:

- **`native`** (default) — built-in username/password authentication, no external dependencies
- **`sso`** — OpenID Connect single sign-on with an external identity provider

To enable SSO, add to your `relay.json`:

```json
{
  "Authentication": {
    "AuthenticationType": "sso"
  },
  "AuthService": {
    "Authority": "https://your-idp.example.com",
    "ClientId": "your-client-id"
  }
}
```

## Run

From your project directory (where `relay.json` lives):

```bash
dotnet /path/to/publish/Relay.dll
```

Or during development:

```bash
dotnet run --project Relay
```

Relay will start on `http://localhost:5001` by default.

## Deploy (Linux server)

A management script is provided at `scripts/relay.sh`. It expects an environment file at `~/relay/relay.env` (or the path in `$RELAY_ENV_FILE`).

Create `~/relay/relay.env`:

```bash
RELAY_HOME="$HOME/relay"
RELAY_PORT=5001
RELAY_CERT_PATH="/path/to/cert.pfx"
RELAY_CERT_PASSWORD="your-password"

# Module system (if using Lmod)
LMOD_INIT="/path/to/lmod/init/bash"
CONDA_MODULE="miniconda3"
CONDA_ENV="relay"

# .NET and Warp native libraries
DOTNET_ROOT="/path/to/dotnet"
WARP_LIB_PATH="/path/to/warp/native/libs"
```

Then:

```bash
scripts/relay.sh start    # Start in background
scripts/relay.sh stop     # Stop gracefully
scripts/relay.sh status   # Check if running
scripts/relay.sh restart  # Stop + start
```

## Configure a cluster queue

Queues are managed by an admin under **Users → Queue configuration**. Each queue has a name, a type (CPU, GPU, or Mixed), a scheduler, and several command templates.

### Scheduler

The **Scheduler** setting tells Relay how to read job IDs and job states out of your commands' output. Pick `Slurm`, `Lsf`, `Pbs`, `Sge` or `Flux` to use the built-in parser for that scheduler, or `Custom` to supply your own patterns — the job ID regular expression and the pending/running/failed status patterns in the **Advanced settings** tab apply only to `Custom` queues, and are hidden otherwise.

Queues default to `Slurm`. Note that the parser no longer falls back across schedulers: a queue set to `Slurm` will reject output in another scheduler's format rather than guessing, so a non-SLURM queue must have its scheduler selected explicitly.

### Managed queues (no external scheduler)

Setting **Scheduler** to `Managed` makes Relay run jobs itself, as local processes on the Relay host, instead of submitting them to SLURM, Flux or anything else. It starts a job only when the CPU cores, memory and GPUs it asks for are free. This suits a single workstation, where installing a scheduler purely to arbitrate one machine's resources is a large dependency for a small need.

Configure **CPU cores**, **Memory (GB)** and **GPUs** to describe the host. The submission script template is still used — for module blocks and `{{ command }}` — but drops the scheduler directives:

```bash
#!/bin/bash
{{ warp }}
ml warptools/latest
{{ /warp }}

umask 007

{{ command }}
```

Things worth knowing:

- **Cores and memory are accounting, not enforcement.** Relay will not start a job unless its declared requirements fit, but nothing stops a running job from exceeding them. GPUs are the exception: each job sees only its assigned devices via `CUDA_VISIBLE_DEVICES`.
- **One managed queue per host.** A second would double-book the same machine, so Relay refuses to create or configure one.
- **Jobs do not survive a Relay restart.** They are killed on shutdown, and anything left behind by a crash is killed at the next startup. Jobs that were running are marked failed.
- **Worker pools cannot use a managed queue.** Pools submit bare scripts with no resource request attached, so there is nothing to admit against.
- **Jobs run in order of fit, not strictly in order of submission.** A small job may start ahead of a queued larger one. On a multi-GPU host a large job can in principle be held off indefinitely by a stream of small ones.
- **macOS is a development-only configuration.** `setsid` is absent there, so Relay cannot clean up compute processes left behind by a crash — only by a graceful shutdown. On Linux both work.

The cores, memory and GPU totals can only be changed while the queue is idle. If jobs are still running on it, the edit is refused rather than leaving the accounting to disagree with what is on the host.

### Submission script template

The script template is a shell script submitted to your scheduler for each job. It uses `{{ variable }}` placeholders that Relay fills in at submission time:

| Variable | Description |
|---|---|
| `{{ command }}` | The actual job command to run |
| `{{ job_id }}` | Relay's internal job identifier |
| `{{ n_cores }}` | CPU cores requested |
| `{{ n_processes }}` | MPI process count |
| `{{ memory_gb }}` | Memory in GB |
| `{{ n_gpus }}` | GPU count (GPU/Mixed queues) |
| `{{ gpu_memory_gb }}` | GPU memory in GB |
| `{{ run_directory }}` | Job working directory |
| `{{ std_out }}` / `{{ std_err }}` | Paths for stdout/stderr logs |

#### Module blocks

Relay uses conditional blocks to load the right software modules depending on the job type. A block is only included in the script when that job type is being run:

```bash
{{ gpu }}
# included only for GPU jobs
{{ /gpu }}

{{ cpu }}
# included only for CPU jobs
{{ /cpu }}

{{ warp }}
# included only for Warp jobs
{{ /warp }}

{{ relion }}
# included only for RELION jobs
{{ /relion }}

{{ relion-pool }}
# included instead of {{ relion }} when a RELION job runs through the disk-based
# worker pool (CPU-only manager + CPU worker fleet). Load a RELION build that
# provides the relion_refine_pool binary. Requested by both the manager and the
# workers, alongside {{ cpu }} for the CPU partition directives.
{{ /relion-pool }}

{{ imod }}
# included only for IMOD jobs
{{ /imod }}

{{ aretomo2 }}
# included only for AreTomo2 jobs
{{ /aretomo2 }}

{{ missalignment }}
# included only for MisAlignment jobs
{{ /missalignment }}

{{ mpi }}
# included only for MPI-parallel jobs
{{ /mpi }}
```

Use these blocks to call your site's module system (e.g. `ml modulename`) so each job loads only what it needs.

A minimal SLURM example structure:

```bash
#!/bin/bash
#SBATCH -J {{ job_id }}
{{ gpu }}
#SBATCH -p <your-gpu-partition>
#SBATCH --gres=gpu:{{ n_gpus }}
{{ /gpu }}
{{ cpu }}
#SBATCH -p <your-cpu-partition>
{{ /cpu }}
#SBATCH -e {{ std_err }}
#SBATCH -o {{ std_out }}
#SBATCH --cpus-per-task {{ n_cores }}
#SBATCH --mem {{ memory_gb }}GB
#SBATCH --nodes 1
#SBATCH --ntasks-per-node {{ n_processes }}

{{ warp }}
ml warptools/latest
{{ /warp }}

{{ relion }}
ml relion/5.0
{{ /relion }}

# Preserve parent directory permissions for group members
umask 007

{{ command }}
```

> **Why `umask 007`:** Without it, jobs create output files with default permissions that exclude the group, breaking access for other project members who share the same group. Setting `umask 007` ensures files land as `660` and directories as `770`, so the group can always read and write job outputs regardless of who submitted the job.

A minimal Flux example:

```bash
#!/bin/bash
#FLUX: --job-name={{ job_id }}
#FLUX: -N 1
#FLUX: -n 1
#FLUX: -x
#FLUX: --output={{ std_out }}
#FLUX: --error={{ std_err }}
{{ gpu }}
#FLUX: -g {{ n_gpus }}
{{ /gpu }}

{{ warp }}
ml warptools/latest
{{ /warp }}

umask 007

{{ command }}
```

Flux specifics worth knowing:

- **All `#FLUX:` directives must be grouped at the top.** Flux errors out on a directive that follows any non-blank line which isn't a comment, so module blocks that emit `ml ...` have to come after them. The blank lines left behind by stripped `{{ gpu }}` tags are fine.
- **`-g` is GPUs *per slot*, not per job.** With `-n {{ n_processes }}`, a 3-rank job asking for 1 GPU would request 3. Pairing `-n 1` with `-x` (exclusive) sidesteps the arithmetic and gives each job the whole node — a good fit for single-node installations, at the cost of running jobs strictly one at a time.
- **Flux's `{{id}}` mustache cannot be used in output paths**, because Relay's own templating strips unrecognised `{{ ... }}` tags. Use `{{ std_out }}` / `{{ std_err }}`, which are already per-job absolute paths.
- Relay must be able to reach your Flux instance. A systemd-managed system instance works out of the box; a per-terminal `flux start` will not, since `FLUX_URI` won't be in the Relay service's environment.

Matching command templates:

| Template | Value |
|---|---|
| Send command | `{{ command }}` |
| Submit job | `flux batch {{ script_path_abs }}` |
| Status job | `flux jobs -no "{status}" {{ job_id }}` |
| Abort job | `flux cancel {{ job_id }} \|\| true` |
| List jobs *(pools only)* | `flux jobs -no "{id.f58},{status}"` |
| Cancel many jobs *(pools only)* | `flux cancel {{ job_ids }} \|\| true` |

Single braces such as `{status}` are Flux's own format fields and pass through Relay's templating untouched.

### Command templates

| Template | Variable | Purpose |
|---|---|---|
| **Send command** | `{{ command }}` | How to run a command on the cluster host, e.g. `ssh user@host {{ command }}` |
| **Submit job** | `{{ script_path_abs }}` | How to submit the generated script, e.g. `sbatch {{ script_path_abs }}` |
| **Status job** | `{{ job_id }}` | How to check a job's state; output must be parseable by Relay's status patterns |
| **Abort job** | `{{ job_id }}` | How to cancel a single job, e.g. `scancel {{ job_id }}` |
| **List jobs** *(GPU worker pools only)* | — | Lists running jobs as `<id>,<state>` one per line; use a comma separator (not quoted) so state survives a remote SSH hop |
| **Cancel many jobs** *(GPU worker pools only)* | `{{ job_ids }}` | Cancels a batch of jobs at once |

## Project structure

| Directory | Description |
|-----------|-------------|
| `Relay/` | ASP.NET Blazor Server application (entry point) |
| `Refund/` | Core library: data model, job definitions, services |
| `Bakery/` | Python package for generating visualizations |
| `Warp/` | Submodule: Warp cryo-EM processing library |
| `ElkSharp/` | Submodule: graph layout engine for workflow diagrams |
| `Emoji/` | Submodule: Fluent UI emoji assets |
| `scripts/` | Deployment and management scripts |

## License

MIT License. See [LICENSE](LICENSE) for details.
