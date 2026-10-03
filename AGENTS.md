# Agent Lightning .NET Migration Notes

## Overview

This document captures the working agreements for the ManagedCode C# 13 / .NET 9 port of Microsoft Agent Lightning, powered by Microsoft Agent Framework plus `Microsoft.Extensions.AI`. The upstream source project, MIT license, and exact parity reference commit are recorded in `UPSTREAM-PROVENANCE.md`. Upstream Python code is not part of the active .NET tree or a build/runtime dependency.

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
- for Agent Lightning parity tasks, compare behavior with the upstream source at the revision recorded in `UPSTREAM-PROVENANCE.md`; preserve upstream attribution while keeping Python implementation files, examples, tests, and tooling out of the active .NET repository
- for Agent Lightning migration tasks, NEVER ship mocks, stubs, or placeholder implementations—port real behaviour before closing a task
- for Agent Lightning runtime work, use only Microsoft Agent Framework and `Microsoft.Extensions.AI`; use `Microsoft.Extensions.AI.Evaluation` for evaluation, and keep every fake or local test client in test-only projects
- for Agent Lightning migration tasks, pursue the migration plan end-to-end without pausing to ask for clarification mid-task; deliver completed work unless the user explicitly redirects
- for Agent Lightning migration tasks, when the user says "продовжити" or "continue", step through the requested workflow without further confirmation prompts
- for Agent Lightning migration tasks, proactively capture improvement ideas directly in the working file (e.g., TODO comments) and implement them within the same task
- for Agent Lightning migration tasks, default to progressing the migration even without an explicit "продовжити"/"continue"; halt only when the user gives new instructions
- for Agent Lightning migration tasks, record any future follow-up items as `TODO:` comments in the relevant files so they are not lost
- for Agent Lightning test coverage, include negative/error scenarios alongside positive cases to validate failure paths
- maintain .NET central package management and preserve the .NET 9 / C# 13 consumer target unless a deliberate compatibility change is reviewed
- always run `dotnet format --verify-no-changes` before `dotnet test` and make sure the full suite is green
- reuse the ManagedCode Communication workflows for CI, CodeQL, and release automation; keep publish pipelines ready to push NuGet packages
- avoid template artifacts (e.g., `Class1.cs`, `UnitTest1.cs`); name files and types according to their Agent Lightning domain responsibilities
- keep documentation, code comments, and commit messaging in English
- for .NET tests, feel free to use Shouldly assertions when they improve readability

## Solution Layout

- `src/ManagedCode.AgentLightning.Core` – shared domain models (`Rollout`, `Attempt`, hooks, etc.).
- `src/ManagedCode.AgentLightning.AgentRuntime` – runtime orchestration built atop `Microsoft.Extensions.AI`.
- `tests/ManagedCode.AgentLightning.Tests` – xUnit suite validating runtime behaviour and parity scenarios.
- `.github/workflows` – CI/CodeQL/release pipelines mirrored from ManagedCode Communication.
- `MIGRATION_PLAN.md` – upstream provenance and module-by-module parity tracker.

## Current Status

- Upstream source URL, license, and source revision are pinned in `UPSTREAM-PROVENANCE.md` and source Git history.
- .NET solution scaffolding in place with central package management and packaging metadata.
- Core rollout and attempt models ported to C# with concurrency-safe metadata.
- The runtime uses caller-supplied MEAI `IChatClient` or MAF `AIAgent`; deterministic echo clients exist only in tests.
- Textual APO runs in-process using MEAI chat clients and MEAI.Evaluation; it does not implement tensor/GPU RL.
- CI, CodeQL, and release workflows added; `dotnet format` and `dotnet test` succeed locally.
- `MIGRATION_PLAN.md` documents coverage and remaining workstreams.

## Next Steps

1. Use `MIGRATION_PLAN.md` to select the next applicable pinned-v0.2 behavior and add end-to-end parity cases.
2. Keep v1 GPU/RL and provider-specific instrumentation clearly marked as not implemented; do not imply full upstream parity.
3. Keep caller-supplied model clients and deployment choices intact throughout runtime and optimization.
