# ManagedCode Agent Lightning C# Migration Plan

This document tracks the conversion of the original Python implementation to a .NET 9 / C# 13 codebase published under the ManagedCode namespace.
The Python reference lives in the `external/microsoft-agent-lightning` submodule and remains the
single source of truth for product behaviour until full parity is achieved.

## Repository Layout

- `external/microsoft-agent-lightning` &mdash; upstream Python sources used for reference and fixtures.
- `src/ManagedCode.AgentLightning.Core` &mdash; shared domain models and primitives.
- `src/ManagedCode.AgentLightning.AgentRuntime` &mdash; runtime orchestration for agents backed by `Microsoft.Extensions.AI`.
- `src/ManagedCode.AgentLightning.Cli` &mdash; self-hosted CLI harness that exercises the C# runtime.
- `tests/ManagedCode.AgentLightning.Tests` &mdash; xUnit test suite validating the C# port.

## Python Module Inventory

| Python Module                                                     | Summary                                                      | Migration Target                               | Status         | Notes |
|-------------------------------------------------------------------|--------------------------------------------------------------|-------------------------------------------------|----------------|-------|
| `agentlightning/types/core.py`, `types/tracer.py`                 | Core data models, spans, rollouts                           | `ManagedCode.AgentLightning.Core/Models`        | **In progress** | Triplet, Rollout, Attempt ported; tracer shapes pending. |
| `agentlightning/litagent/`                                        | Agent lifecycle, rollout orchestration                      | `ManagedCode.AgentLightning.AgentRuntime`       | **In progress** | `LightningAgent` implemented with `IChatClient`; advanced features pending. |
| `agentlightning/runner/`                                          | Runner coordination, hooks, parallel execution              | `ManagedCode.AgentLightning.AgentRuntime`       | Not started    | Need scheduling and multi-attempt coordination. |
| `agentlightning/tracer/`                                          | OpenTelemetry integrations                                   | `ManagedCode.AgentLightning.Core` / `ManagedCode.AgentLightning.AgentRuntime` | Not started    | Requires .NET OTEL pipeline. |
| `agentlightning/store/`                                           | Persistence layer                                             | TBD                                             | Not started    | Identify .NET storage abstraction. |
| `agentlightning/reward/`                                          | Reward calculation utilities                                 | TBD                                             | Not started    | Requires parity with RL tooling. |
| `agentlightning/adapter/`, `execution/`, `algorithm/`, `trainer/` | Training pipelines, adapters, algorithms                     | TBD                                             | Not started    | Dependent on runner and tracer ports. |
| `agentlightning/cli/`                                             | Python CLI entry points                                      | `ManagedCode.AgentLightning.Cli`                | **In progress** | New CLI harness available; parity with Python commands pending. |
| `agentlightning/server.py`, `client.py`                           | Legacy HTTP server/client                                    | Separate ASP.NET/API project (future)           | Not started    | Decide on long-term hosting story. |
| `agentlightning/logging.py`                                       | Logging helpers                                              | `.NET logging abstractions`                     | **In progress** | Logging routed through `Microsoft.Extensions.Logging`. |
| `tests/`                                                          | Integration and parity tests                                 | `tests/ManagedCode.AgentLightning.Tests`        | Not started    | Need fixtures mirroring upstream scenarios. |

## Completed Work

- Converted repository scaffold to .NET 9 / C# 13 solution with central package management.
- Implemented core rollout models (`Triplet`, `Attempt`, `Rollout`, hooks) in `ManagedCode.AgentLightning.Core`.
- Ported fundamental agent execution pipeline powered by `Microsoft.Extensions.AI.IChatClient`.
- Added `LocalChatClient` for offline validation and connected CLI harness.
- Established CI, CodeQL, and release workflows mirroring ManagedCode patterns.
- Added initial integration test covering `LightningAgent` execution against the local client.

## Near-Term Priorities

1. Port tracer data structures (`Span`, OTEL conversions) and integrate with .NET OpenTelemetry.
2. Bring over runner orchestration primitives (parallel workers, retry policies, resource slots).
3. Reproduce Python parity tests by sourcing fixtures from the submodule.
4. Introduce configurable connectors for real AI providers using `Microsoft.Extensions.AI` builders.
5. Document configuration and extension points in `README.md`.

## Tracking Progress

Update this file whenever:

- A Python module gains a parity implementation or has design decisions documented.
- Tests are ported or new coverage approaches are introduced.
- Workflow or packaging behaviours change.
- Deprecated guidance is removed or superseded.
