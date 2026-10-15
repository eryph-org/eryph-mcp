# eryph MCP server (prototype)

A standalone [Model Context Protocol](https://modelcontextprotocol.io) server that lets
LLM clients create, run and inspect eryph catlets.

- **Transport:** stdio. The server runs locally with the user's eryph client credentials.
- **Backend:** the eryph compute API through `Eryph.ComputeClient`, plus
  `Eryph.GuestServices.Client` for running commands in guests over the eryph remote
  channel. No PowerShell, no `ssh.exe`, and no admin rights when an eryph client is
  configured.
- **SDK:** `ModelContextProtocol` 2.2.0 on .NET 10.

## Projects

| Project | Purpose |
|---|---|
| `src/Eryph.Mcp.Tools` | Tool implementations, independent of the host |
| `src/Eryph.Mcp.Server` | stdio host (`eryph-mcp.exe`) |

## Tools

| Tool | Purpose |
|---|---|
| `validate_catlet` | Validate a catlet config and expand it (resolves parents and genes) |
| `deploy_catlet` | Create a catlet; returns the catlet id, or an operation id if creation is still running |
| `get_operation` | Get or wait for an operation |
| `list_catlets`, `get_catlet` | Inventory |
| `start_catlet`, `stop_catlet`, `remove_catlet` | Lifecycle |
| `wait_for_provisioning` | Wait for the guest provisioning state `completed` or `failed` |
| `get_provisioning_log` | Guest provisioning log |
| `catlet_exec` | Run a non-interactive command in the guest (merged output, exit code, timeout) |

Errors are prefixed with a category: `[content error]`, `[state error]` or
`[transient error]`. The server instructions carry the workflow and the incremental
fodder rules.

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

## Credentials

By default the connection is resolved like the eryph CLI: the default client of the
default configuration. Without a configured client it falls back to the local
system client, which requires an elevated process. To use a specific client:

```
eryph-mcp --eryph:clientId <id> [--eryph:configuration <name>]
```

## Use with Claude Code

```
dotnet build
claude mcp add eryph -- D:\Source\Repos\eryph\mcp\src\Eryph.Mcp.Server\bin\Debug\net10.0\eryph-mcp.exe
```
