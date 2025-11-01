# Agent Lightning .NET Migration Notes

## Overview

This document captures the working agreements for porting Microsoft Agent Lightning from Python to a C# 13 / .NET 9 implementation maintained under the ManagedCode namespace and powered by Microsoft Agent Framework plus `Microsoft.Extensions.AI`. The canonical Python sources live in the `external/microsoft-agent-lightning` submodule and remain the reference until feature parity is achieved.

# Conversations

any resulting updates to agents.md should go under the section "## Rules to follow"
When you see a convincing argument from me on how to solve or do something. add a summary for this in agents.md. so you learn what I want over time.
If I say any of the following point, you do this: add the context to agents.md, and associate this with a specific type of task.
if I say "never do x" in some way.
if I say "always do x" in some way.
if I say "the process is x" in some way.
If I tell you to remember something, you do the same, update
if I say "do/don’t", define a process, or confirms success/failure, add a concise rule tied to the relevant task type.
if I say "always/never X", "prefer X over Y", "I like/dislike X", or "remember this", update this file.
When a mistake is corrected, capture the new rule and remove obsolete guidance.
When a workflow is defined or refined, document it here.
Strong negative language indicates a critical mistake; add an emphatic rule immediately.

Update guidelines:
- Actionable rules tied to task types.
- Capture why, not just what.
- One clear instruction per bullet.
- Group related rules.
- Remove obsolete rules entirely.

---

## Rules To Follow
- for Agent Lightning migration tasks, ALWAYS mirror functionality, tests, and docs from `external/microsoft-agent-lightning`; treat the submodule as the canonical specification and never modify it directly
- for Agent Lightning migration tasks, NEVER ship mocks, stubs, or placeholder implementations—port real behaviour before closing a task
- for Agent Lightning runtime work, build on Microsoft Agent Framework components and `Microsoft.Extensions.AI`; prefer first-party packages under permissive (MIT) licenses
- for Agent Lightning migration tasks, pursue the migration plan end-to-end without pausing to ask for clarification mid-task; deliver completed work unless the user explicitly redirects
- for Agent Lightning migration tasks, when the user says "продовжити" or "continue", step through the requested workflow without further confirmation prompts
- for Agent Lightning migration tasks, proactively capture improvement ideas directly in the working file (e.g., TODO comments) and implement them within the same task
- for Agent Lightning migration tasks, default to progressing the migration even without an explicit "продовжити"/"continue"; halt only when the user gives new instructions
- for Agent Lightning migration tasks, record any future follow-up items as `TODO:` comments in the relevant files so they are not lost
- for Agent Lightning test coverage, include negative/error scenarios alongside positive cases to validate failure paths
- maintain .NET central package management and keep the solution on .NET 9 / C# 13 with preview features enabled until GA guidance changes
- always run `dotnet format --verify-no-changes` before `dotnet test` and make sure the full suite is green
- reuse the ManagedCode Communication workflows for CI, CodeQL, and release automation; keep publish pipelines ready to push NuGet packages
- avoid template artifacts (e.g., `Class1.cs`, `UnitTest1.cs`); name files and types according to their Agent Lightning domain responsibilities
- keep documentation, code comments, and commit messaging in English
- for .NET tests, feel free to use Shouldly assertions when they improve readability

## Solution Layout

- `external/microsoft-agent-lightning` – vendored Python sources used for parity checks and fixtures.
- `src/ManagedCode.AgentLightning.Core` – shared domain models (`Rollout`, `Attempt`, hooks, etc.).
- `src/ManagedCode.AgentLightning.AgentRuntime` – runtime orchestration built atop `Microsoft.Extensions.AI`.
- `tests/ManagedCode.AgentLightning.Tests` – xUnit suite validating runtime behaviour and parity scenarios.
- `.github/workflows` – CI/CodeQL/release pipelines mirrored from ManagedCode Communication.
- `MIGRATION_PLAN.md` – evergreen tracker for module-by-module parity progress.

## Current Status

- Python repository wired in as a submodule for reference.
- .NET solution scaffolding in place with central package management and packaging metadata.
- Core rollout and attempt models ported to C# with concurrency-safe metadata.
- Initial `LightningAgent` runtime implemented using `IChatClient`, plus `LocalChatClient` for offline smoke testing.
- CI, CodeQL, and release workflows added; `dotnet format` and `dotnet test` succeed locally.
- `MIGRATION_PLAN.md` documents coverage and remaining workstreams.

## Next Steps

1. Port tracer/span representations and hook into .NET OpenTelemetry exporters.
2. Reproduce runner orchestration (parallel workers, retries, resource allocation).
3. Import Python fixtures into the C# test suite to exercise parity scenarios end-to-end.
4. Implement adapters for real AI providers (OpenAI, Azure OpenAI, etc.) using Microsoft Agent Framework primitives.
5. Design persistence abstractions mirroring the Python `store` package.
6. Document configuration expectations and hosting guidance for the new runtime.
