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
