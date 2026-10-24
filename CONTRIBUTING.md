# Contributing

Thanks for your interest in improving the eryph MCP server.

## Prerequisites

- [.NET SDK 10](https://dotnet.microsoft.com/download) (see `global.json` for the
  exact version). The repo restores from nuget.org and the dbosoft public feed
  (configured in `nuget.config`).
- For integration and eval tests: a running [eryph-zero](https://eryph.io) on
  this host. These are not required for the unit and contract tests.

## Build and test

```
dotnet build
dotnet test tests/Eryph.Mcp.Tools.Tests     # level 1: unit, needs nothing
dotnet test tests/Eryph.Mcp.Server.Tests     # level 2: contract, needs nothing
```

The higher test levels (integration against a real eryph-zero, and the
model-in-the-loop evals) are described in the [README](README.md#tests) and are
gated behind environment variables so they never run by accident.

## Conventions

- Code style is enforced by `.editorconfig`; a ReSharper cleanup profile
  (`Mechanical`) is checked in. Please run a build with zero warnings before
  opening a pull request.
- The tool names, descriptions and JSON schemas are the API the model sees. Any
  change to them shows up as a diff in the contract snapshot
  (`tests/Eryph.Mcp.Server.Tests/Snapshots/tools.json`). Review the diff and,
  when the change is intended, accept it by rerunning the contract tests with
  `UPDATE_SNAPSHOTS=1`.
- Add or update tests for behaviour changes.

## Releases

Releases and the package version are handled by the maintainers; pull
requests do not need to change the version number.
