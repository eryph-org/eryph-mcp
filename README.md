# eryph MCP server

A [Model Context Protocol](https://modelcontextprotocol.io) server that lets LLM clients
create, run and inspect [eryph](https://www.eryph.io) catlets (Hyper-V VMs built from
genes and fodder).

The server runs locally over stdio with the user's eryph client credentials. It talks to
the eryph compute API through `Eryph.ComputeClient` and runs commands in guests through
`Eryph.GuestServices.Client` over the eryph remote channel. It needs no PowerShell, no
`ssh.exe` and no admin rights when an eryph client is configured.

- **Transport:** stdio
- **SDK:** `ModelContextProtocol` 2.2.0 on .NET 10
- **Package:** `Eryph.Mcp.Server` on nuget.org, command `eryph-mcp`

## Contents

- [Install](#install)
- [Use with Claude Code](#use-with-claude-code)
- [For MCP clients and agents](#for-mcp-clients-and-agents)
- [Tools reference](#tools-reference)
- [Writing catlet configurations](#writing-catlet-configurations)
- [Credentials](#credentials)
- [Projects](#projects)
- [Tests](#tests)
- [Contributing, security and license](#contributing-security-and-license)

## Install

The server ships as a [.NET tool](https://learn.microsoft.com/dotnet/core/tools/global-tools).
It requires the .NET 10 SDK or runtime and an eryph installation (eryph-zero or a
reachable eryph host with a configured client).

```
dotnet tool install -g Eryph.Mcp.Server
```

This puts `eryph-mcp` on the PATH. To update:

```
dotnet tool update -g Eryph.Mcp.Server
```

## Use with Claude Code

With the tool installed:

```
claude mcp add eryph -- eryph-mcp
```

To run a local build from source instead, point the command at the built executable
(replace `<repo>` with your checkout and adjust the configuration if needed):

```
dotnet build
claude mcp add eryph -- <repo>/src/Eryph.Mcp.Server/bin/Debug/net10.0/eryph-mcp.exe
```

Any other MCP client that can launch a stdio server works the same way: run `eryph-mcp`
(optionally with the [credential options](#credentials)) as the server command.

## For MCP clients and agents

This section summarizes what the server tells a client in its MCP `instructions` at
initialization. The tool names, descriptions and schemas are the contract; the
[tools reference](#tools-reference) below mirrors the source.

### Tools at a glance

| Area | Tools |
|---|---|
| Authoring | `validate_catlet` |
| Deployment | `deploy_catlet`, `get_operation` |
| Inventory | `list_catlets`, `get_catlet` |
| Lifecycle | `start_catlet`, `stop_catlet`, `remove_catlet` |
| Provisioning | `wait_for_provisioning`, `get_provisioning_log` |
| Guest access | `catlet_exec` |

### Workflow

```
validate_catlet -> deploy_catlet -> start_catlet -> wait_for_provisioning -> catlet_exec
```

1. **`validate_catlet`** with the catlet configuration. Fix every reported error before
   deploying. The result also contains the fully expanded configuration (parents resolved,
   inherited fodder merged, genes looked up).
2. **`deploy_catlet`** creates the catlet. It is created *stopped*. The result contains
   the `catletId`, or an operation id with status `running` if creation exceeded
   `waitSeconds`; in that case continue with **`get_operation`**.
3. **`start_catlet`** with the catlet id.
4. **`wait_for_provisioning`** until the guest reports provisioning state `completed`.
   Guest services reporting `available` is not enough; fodder may still be running.
   On `failed`, the failed steps are returned; **`get_provisioning_log`** has the full log.
5. **`catlet_exec`** runs non-interactive commands in the guest.

Rules that apply throughout:

- Address catlets by **catlet id**. Names are not unique across projects, and
  `deploy_catlet` rejects a name that already exists within the target project.
- Long operations return an **operation id** when they do not finish in time; continue
  with `get_operation` rather than repeating the original call.
- Develop fodder **incrementally**: start with a minimal catlet (parent only), verify it,
  then add one fodder item at a time and verify after each. Fodder runs as one chain; a
  failing item blocks all later ones.
- If `wait_for_provisioning` never completes or guest services never report, an earlier
  fodder item most likely failed. Read `get_provisioning_log` and simplify the fodder.
  Do not debug networking or try to reach the guest via SSH by IP.

### Error model

Every expected failure is returned as a tool error whose message starts with its category:

| Prefix | Meaning | What to do |
|---|---|---|
| `[content error]` | The catlet or fodder definition is wrong (schema error, unknown gene, invalid variable, ...). | Change the configuration and validate again. |
| `[state error]` | The system state does not allow the call (catlet stopped, name already exists, catlet or project not found, operation failed). | Change the state: start the catlet, pick another name, remove the existing catlet, use the right id. |
| `[transient error]` | A temporary failure in the API or the guest channel. | Retry, with the same input. |

## Tools reference

All ids are strings. Durations are integers in seconds. Optional parameters are marked
with their defaults.

### `validate_catlet` (read-only)

Validates a catlet configuration (YAML or JSON) without creating anything. Runs the
schema and semantic validation, then expands the configuration: parents are resolved,
inherited fodder is merged and referenced genes are looked up. Genes may be downloaded
from the genepool, which can take a while on first use. Always validate before
`deploy_catlet`.

| Parameter | Description |
|---|---|
| `config` | The catlet configuration as YAML (or JSON). |
| `variables` | Optional variable values by name (object of string to string). |
| `name` | Overrides the catlet name from the configuration, as `deploy_catlet` does. |

Returns `isValid`, `errors`, `unknownVariables` (names passed in `variables` that the
configuration does not declare) and `expandedConfig` (YAML) when valid.

### `deploy_catlet`

Creates a new catlet from a catlet configuration. The catlet is created stopped; call
`start_catlet` afterwards. Creation can take several minutes when genes have to be
downloaded. Catlet names must be unique within a project; if the configuration has no
name, `catlet` is used.

| Parameter | Description |
|---|---|
| `config` | The catlet configuration as YAML (or JSON). |
| `variables` | Optional variable values by name. |
| `name` | Overrides the catlet name from the configuration. |
| `project` | Overrides the project from the configuration (default `default`). |
| `waitSeconds` | Seconds to wait for the creation to finish (default 600). |

Returns `catletId`, `name`, `status` (`created`, or `running` when the wait expired),
`operation` (id, status, recent log, created resources), `unknownVariables` and a
`nextStep` hint.

### `get_operation` (read-only)

Gets, and optionally waits for, an eryph operation such as one returned by `deploy_catlet`.

| Parameter | Description |
|---|---|
| `operationId` | The operation id. |
| `waitSeconds` | Seconds to wait for the operation to finish (default 0, return immediately). |

### `list_catlets` (read-only)

Lists catlets, optionally filtered by project name. Each entry has `catletId`, `name`,
`project`, `status`, `provisioningStatus` and `vmId`.

| Parameter | Description |
|---|---|
| `project` | Optional project name. |

### `get_catlet` (read-only)

Gets a catlet by id, including its status and provisioning status.

| Parameter | Description |
|---|---|
| `catletId` | The catlet id. |

### `start_catlet`

Starts a catlet. Afterwards use `wait_for_provisioning` before running commands in it.

| Parameter | Description |
|---|---|
| `catletId` | The catlet id. |

### `stop_catlet`

Stops a catlet.

| Parameter | Description |
|---|---|
| `catletId` | The catlet id. |
| `mode` | `shutdown` (graceful, default), `hard` (power off) or `kill`. |

### `remove_catlet` (destructive)

Deletes a catlet including its disks. This cannot be undone.

| Parameter | Description |
|---|---|
| `catletId` | The catlet id. |

### `wait_for_provisioning` (read-only)

Waits until the guest of a started catlet has finished provisioning, that is, until the
provisioning state is `completed` or `failed`. Returns immediately with state `stopped`
if the catlet is not running.

| Parameter | Description |
|---|---|
| `catletId` | The catlet id. |
| `timeoutSeconds` | Seconds to wait (default 900). |

Returns `state`, `guestServicesStatus`, `guestServicesVersion`, `shell`, `finished`,
a `hint` and `failedSteps`. `finished: false` means the timeout expired; the hint says
whether guest services never reported (boot still in progress, or an early fodder step
blocked them) or provisioning is still running.

### `get_provisioning_log` (read-only)

Gets the provisioning log of a catlet guest (fodder / cloud-init execution), one line
per event, including failed steps. Long logs are truncated to their tail
(`truncated: true`).

| Parameter | Description |
|---|---|
| `catletId` | The catlet id. |

### `catlet_exec`

Runs a non-interactive command in a catlet guest through eryph guest services. Requires a
running catlet with guest services; call `wait_for_provisioning` first.

| Parameter | Description |
|---|---|
| `catletId` | The catlet id. |
| `command` | The command line to run. |
| `timeoutSeconds` | Seconds before the command is aborted (default 300). |

Returns `exitCode`, `output` (stdout and stderr merged), `truncated`, `timedOut` and
`error`.

- The command runs in the guest's configured shell: PowerShell by default on Windows
  (`sc` and `type` are PowerShell aliases there; use `sc.exe` or `Get-Content`), the
  default shell as root on Linux.
- There is no TTY and no stdin. A command that waits for input blocks until the timeout,
  so redirect stdin (Linux: `< /dev/null`) and use non-interactive flags
  (`apt-get -y`, `DEBIAN_FRONTEND=noninteractive`).
- Interactive work that needs a TTY belongs to real SSH
  (`ssh <catlet-id>.eryph.alt` after `egs-tool catlet add-ssh-config <catlet-id> --add-key`),
  not to this tool.

## Writing catlet configurations

The server accepts the regular eryph catlet configuration format as YAML or JSON. The
points below are the ones that matter most when a model authors configurations; see the
[catlet configuration reference](https://www.eryph.io/docs/refs/catlet-configuration-files)
for the full format.

- **Start minimal.** A catlet with only `parent` (for example a genepool base image) is
  the first thing to validate and deploy. Add fodder one item at a time afterwards.
- **Variables** use the `{{ variable }}` syntax inside the YAML. Declare them under
  `variables` as a list of objects with `name` and `value`; pass values with the
  `variables` parameter of `validate_catlet` and `deploy_catlet`.
- **Fodder genes** are referenced as `gene:org/geneset[/tag]:fodder-name`.
- **Inline fodder items** have `name`, `type` (`cloud-config` or `shellscript`) and
  `content`. On Windows, give a `shellscript` item a `filename` (for example
  `filename: setup.ps1`); the key is `filename`, not `file_name`, and the extension
  selects the interpreter.
- **Linux vs. Windows.** Linux catlets are provisioned by cloud-init; Windows catlets by
  eryph guest services. Windows accepts full cloud-config but ignores Linux-only keys such
  as `packages`; install software with scripts there. A failing Windows script does not
  stop later scripts, so always check the provisioning log.
- **One chain.** Fodder runs in order as a single chain; a failing item blocks all later
  ones, which usually shows up as `wait_for_provisioning` never completing. (Windows
  scripts are the exception noted above: a failing script does not stop later ones.)

## Credentials

By default the connection is resolved like the eryph CLI: the default client of the
default configuration. Without a configured client the server falls back to the local
system client, which requires an elevated process. To use a specific client:

```
eryph-mcp --eryph:clientId <id> [--eryph:configuration <name>]
```

When registering the server with an MCP client, append these options to the server
command.

## Projects

| Project | Purpose |
|---|---|
| `src/Eryph.Mcp.Tools` | Tool implementations and server instructions, independent of the host |
| `src/Eryph.Mcp.Server` | stdio host; packs as the .NET tool `eryph-mcp` (package `Eryph.Mcp.Server`) |
| `tests/Eryph.Mcp.Tools.Tests` | Unit tests |
| `tests/Eryph.Mcp.Server.Tests` | Contract and integration tests |
| `tests/Eryph.Mcp.Evals` | Model evaluations |

## Tests

| Level | Project | Needs | Run |
|---|---|---|---|
| 1 Unit | `tests/Eryph.Mcp.Tools.Tests` | nothing (compute clients are substituted) | `dotnet test --project tests/Eryph.Mcp.Tools.Tests` |
| 2 Contract | `tests/Eryph.Mcp.Server.Tests` (`ContractTests`) | nothing; starts `eryph-mcp.exe` over stdio with the SDK client | `dotnet test --project tests/Eryph.Mcp.Server.Tests` |
| 3 Integration | `tests/Eryph.Mcp.Server.Tests` (other classes) | eryph-zero on this host; creates and removes catlets | `ERYPH_MCP_INTEGRATION=1` (Linux), plus `ERYPH_MCP_WINDOWS=1` (Windows, slow) |
| 4 Evals | `tests/Eryph.Mcp.Evals` | eryph-zero and a logged-in `claude` CLI; costs API usage | `dotnet run --project tests/Eryph.Mcp.Evals -- --models sonnet,haiku` |

- **Snapshot:** the contract tests snapshot the tool definitions
  (`Snapshots/tools.json`), because names, descriptions and schemas are the API the
  model sees. To accept a deliberate change, run with `UPDATE_SNAPSHOTS=1` and review
  the diff.
- **What an eval run does:** for each scenario in `tests/Eryph.Mcp.Evals/scenarios`, it
  runs `claude -p` headless in an empty directory. Only the eryph MCP server is
  available: no built-in tools, no other MCP servers and no user/project settings.
  Every run is a fresh process with the current build.
- **How an eval is graded:** from the resulting catlet state (provisioning state and
  commands run in the catlet), not from what the model claims.
- **Eval output:** turns, tool calls, tool errors, cost and duration, plus a
  `summary.md` and the transcripts under `results/`.
- **Cleanup:** catlets created by a run are removed afterwards unless `--keep` is given.
- **Scenario selection:** scenarios marked `slow` (Windows) only run with
  `--include-slow` or `--scenarios <id>`.

## Contributing, security and license

- Contributions: see [CONTRIBUTING.md](CONTRIBUTING.md).
- Reporting vulnerabilities: see [SECURITY.md](SECURITY.md).
- License: [MIT](LICENSE).

Source: <https://github.com/eryph-org/eryph-mcp>
