# ManagedCode Agent Lightning ⚡

> **ℹ️ This repository hosts the ManagedCode-maintained C# 13 / .NET 9 embedded port of Microsoft Agent Lightning.**
> The original Python implementation remains the functional reference and is mirrored under `external/microsoft-agent-lightning` as a read-only git submodule. All active development happens in the `ManagedCode.AgentLightning.*` C# projects.

## Overview

ManagedCode Agent Lightning provides in-process rollouts, tracing, evaluation, and textual Automatic Prompt Optimization (APO) for .NET agents. It uses `Microsoft.Extensions.AI`, `Microsoft.Extensions.AI.Evaluation`, and Microsoft Agent Framework. The caller supplies the runtime and evaluation clients, so the library does not choose a provider, deployment, or model on the caller's behalf.

No Python process or remote optimization service is required. The Python sources are retained as a read-only reference at the pinned submodule revision documented in [`MIGRATION_PLAN.md`](./MIGRATION_PLAN.md).

This is not a GPU or tensor-training backend. The upstream v1 GPU/RL stack and provider-specific instrumentation are not implemented here; parity is tracked component by component rather than claimed for the entire Python repository.

## Repository Layout

| Path | Description |
|------|-------------|
| `src/ManagedCode.AgentLightning.Core` | Core domain models (`Rollout`, `Attempt`, hooks, metadata primitives). |
| `src/ManagedCode.AgentLightning.AgentRuntime` | In-process rollout/trainer, MEAI and MAF runtime adapters, evaluation recording, and textual APO. |
| `tests/ManagedCode.AgentLightning.Tests` | xUnit tests for the C# implementation (no Python dependencies). |
| `external/microsoft-agent-lightning` | Upstream Python repository (git submodule, kept read-only). |
| `MIGRATION_PLAN.md` | Module-by-module parity tracker for the migration process. |
| `AGENTS.md` | Working agreements and guardrails specific to this port. |

## Prerequisites

- .NET SDK 9.0.300 or later.
- Git with submodule support to check out the pinned parity reference used by CI.

## Getting Started

```bash
git clone https://github.com/managedcode/agent-lightning.git
cd agent-lightning
git submodule update --init --recursive

dotnet restore
dotnet format --verify-no-changes
dotnet test
```

## Project Status

- ✔ Core rollout/attempt models ported to C# with parity-focused semantics.  
- ✔ In-process rollouts with caller-supplied MEAI `IChatClient` or MAF `AIAgent`.
- ✔ MEAI.Evaluation-based APO with explicit objective selection, validation, versioned prompt history, and trace rewards.
- ✔ CI, CodeQL, and NuGet release workflows based on .NET tooling.
- 🛠 Remaining pinned-v0.2 parity and deliberately excluded upstream v1 GPU features are listed in [`MIGRATION_PLAN.md`](./MIGRATION_PLAN.md).

## Contributing

1. Fork the repository and branch from `main`.
2. Run `dotnet format --verify-no-changes` followed by the full `dotnet test` suite before submitting changes.
3. Update `MIGRATION_PLAN.md` with any new parity milestones or design decisions.
4. Submit a pull request describing the ported functionality and tests.

## License

This project is licensed under the [MIT License](./LICENSE).
