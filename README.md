# ManagedCode Agent Lightning ⚡

> **ℹ️ This repository hosts the ManagedCode-maintained C# 13 / .NET 9 port of Microsoft’s Agent Lightning project.**  
> The original Python implementation remains the functional reference and is mirrored under `external/microsoft-agent-lightning` as a read-only git submodule. All active development happens in the `ManagedCode.AgentLightning.*` C# projects.

## Overview

ManagedCode Agent Lightning brings Microsoft’s reinforcement-learning toolkit for AI agents to .NET.  
The port mirrors the behaviour of the upstream Python implementation while taking advantage of `Microsoft.Extensions.AI`, the generic host, and modern C# features.

No Python code is executed within this solution; the Python sources live purely for reference and parity validation.

## Repository Layout

| Path | Description |
|------|-------------|
| `src/ManagedCode.AgentLightning.Core` | Core domain models (`Rollout`, `Attempt`, hooks, metadata primitives). |
| `src/ManagedCode.AgentLightning.AgentRuntime` | Execution pipeline built around `IChatClient` and dependency injection helpers. |
| `src/ManagedCode.AgentLightning.Cli` | Minimal CLI host that wires up the runtime for interactive experimentation. |
| `tests/ManagedCode.AgentLightning.Tests` | xUnit tests for the C# implementation (no Python dependencies). |
| `external/microsoft-agent-lightning` | Upstream Python repository (git submodule, kept read-only). |
| `MIGRATION_PLAN.md` | Module-by-module parity tracker for the migration process. |
| `AGENTS.md` | Working agreements and guardrails specific to this port. |

## Prerequisites

- .NET SDK 9.0.300 or later (C# 13 preview enabled).
- Git with submodule support if you want to inspect the upstream Python sources.

## Getting Started

```bash
git clone https://github.com/managedcode/agent-lightning.git
cd agent-lightning
git submodule update --init --recursive   # optional – only if you need to inspect the Python reference

dotnet restore
dotnet format
dotnet test
```

## Run the CLI Sample

```bash
dotnet run --project src/ManagedCode.AgentLightning.Cli
```

Type into the prompt to exercise the local `IChatClient` loop, or replace `LocalChatClient` with a production chat client via `LightningServiceCollectionExtensions`.

## Project Status

- ✔ Core rollout/attempt models ported to C# with parity-focused semantics.  
- ✔ Initial agent runtime executing rollouts against any `IChatClient`.  
- ✔ CI, CodeQL, and release workflows based entirely on .NET tooling.  
- 🛠 Migration progress tracked in [`MIGRATION_PLAN.md`](./MIGRATION_PLAN.md).  
- 🗺 Upcoming work: tracer/span parity, runner orchestration, real AI provider adapters.

## Contributing

1. Fork the repository and branch from `main`.
2. Run `dotnet format` followed by `dotnet test` before submitting changes.
3. Update `MIGRATION_PLAN.md` with any new parity milestones or design decisions.
4. Submit a pull request describing the ported functionality and tests.

## License

This project is licensed under the [MIT License](./LICENSE).
