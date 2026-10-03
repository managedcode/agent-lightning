# ManagedCode Agent Lightning for .NET

ManagedCode Agent Lightning is an embedded .NET port of Microsoft Agent Lightning. It provides in-process rollouts, tracing, evaluation, and textual Automatic Prompt Optimization (APO) using `Microsoft.Extensions.AI`, `Microsoft.Extensions.AI.Evaluation`, and Microsoft Agent Framework. Applications supply their own official MEAI `IChatClient` or MAF `AIAgent` and evaluator; this library does not select a provider, deployment, billing route, or model.

The released implementation does not require a Python process or remote optimization service. Upstream project identity and the exact parity source revision are recorded in [`UPSTREAM-PROVENANCE.md`](./UPSTREAM-PROVENANCE.md); upstream Python source is not part of the active .NET tree or a build/runtime dependency.

This port implements textual prompt optimization and measured evaluation. It does not implement GPU/tensor training, VERL/vLLM execution, model-weight updates, or native checkpoint resume. See the parity matrix for implemented and unsupported upstream components.

## Install

The runtime package depends on the core package and is the normal entry point:

```bash
dotnet add package ManagedCode.AgentLightning.AgentRuntime --version 0.0.3
```

Use `ManagedCode.AgentLightning.Core` directly only when an application needs the data, store, tracing, or resource contracts without the in-process runtime:

```bash
dotnet add package ManagedCode.AgentLightning.Core --version 0.0.3
```

The model and evaluator remain application-owned. Configure them through Microsoft.Extensions.AI or Microsoft Agent Framework and pass those clients into the runtime; the library does not create provider-specific clients or make network calls itself.

## Build and test from source

Requirements: .NET SDK 9.0.300 or later. Building and using the NuGet packages does not require Python or an upstream source checkout.

```bash
git clone https://github.com/managedcode/agent-lightning.git
cd agent-lightning
dotnet restore
dotnet format --verify-no-changes
dotnet test
```

The active implementation is under `src/ManagedCode.AgentLightning.*`; xUnit tests are under `tests/ManagedCode.AgentLightning.Tests`. CI builds, formats, and tests the .NET solution.

## Scope and parity

The package supports in-process rollouts and the released textual APO loop. APO requires an explicit named objective and direction, or a caller-provided score selector, and rejects failed or missing evaluation evidence. It retains prompt versions and evaluation/reward traces. It is not a general-purpose model-training framework: upstream GPU/RL, tensor training, VERL/vLLM backends, and provider-specific instrumentation are not included. Remaining pinned-v0.2 differences are tracked in [`MIGRATION_PLAN.md`](./MIGRATION_PLAN.md); no complete upstream parity claim is made.

## Contributing

Run `dotnet format --verify-no-changes` and the full `dotnet test` suite before submitting changes. Update `MIGRATION_PLAN.md` when parity status changes.

## License

This project is licensed under the [MIT License](./LICENSE).
