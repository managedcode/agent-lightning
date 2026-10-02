
# ManagedCode Agent Lightning parity plan

This document tracks behavior against the read-only reference submodule `external/microsoft-agent-lightning`, pinned at `v0.2.0-39-g4cd09ec` (`4cd09ec`). The matrix describes that pinned v0.2 API and implementation; it does not claim parity with all of Microsoft Agent Lightning.

The upstream v1 line is a larger GPU/RL rearchitecture. This repository is an embedded .NET library with no required Python process or remote optimization service, so GPU tensor training, VERL/vLLM backends, and their runtime orchestration are not implemented or claimed here.

## Parity matrix

| Pinned v0.2 component | Status | C# coverage and boundary |
| --- | --- | --- |
| Core rollout, attempt, triplet, hook, tracing, and resource types | ✅ | `ManagedCode.AgentLightning.Core` types, serialization, and tests. |
| Trace/span conversion and trace-to-message/triplet adapters | ✅ | System.Diagnostics/OpenTelemetry adapters with focused tests; instrumentation remains caller-owned. |
| Store contract and in-memory store | ✅ | Rollout, attempt, span, and resource lifecycle is available in process. Durable/distributed store parity is not included. |
| Embedded runner and trainer | 🚧 | `LitAgentRunner` and `Trainer` execute local batches and bounded parallel rollouts. Python execution strategies, queue recovery, and the client/server store bridge are not equivalent. |
| Runtime model invocation | ✅ | Caller supplies official MEAI `IChatClient` or MAF `AIAgent`. The port does not choose a provider, model deployment, billing route, or implement provider transports. |
| APO textual-gradient and beam-search loop | ✅ | In-process prompt edit and validation loop; prompt templates are versioned in the result. It requires explicit named objective/direction or a caller score selector and rejects evaluation errors/missing metrics. |
| Evaluation and reward evidence | ✅ | `Microsoft.Extensions.AI.Evaluation` metrics are recorded as reward spans; evaluator failures cannot be compared as zero. |
| Algorithm lifecycle/base classes, registry, CLI, and legacy trainer compatibility | 🚧 | Not ported; APO is exposed directly over the in-process trainer. |
| Python AgentOps/LiteLLM/vLLM instrumentation | 🚧 | Not ported; consumers use their supported MEAI/MAF instrumentation and OpenTelemetry setup. |
| Python VERL/GPU training, tensor rollout, and remote orchestration | Out of scope | No Python process, GPU backend, or optimization service is required by this library. |

## Acceptance matrix

| Boundary | Acceptance evidence | State |
| --- | --- | --- |
| Source provenance | Submodule remains pinned to `v0.2.0-39-g4cd09ec`; CI initializes the submodule recursively. | Verified in repository metadata/workflow. |
| Production AI client boundary | No custom/local client in production; runtime accepts MEAI or MAF abstractions. | Covered by package references and runtime construction paths. |
| APO objective correctness | Explicit objective, direction, finite scalar result, missing objective rejection, and evaluator error rejection. | Covered by optimizer tests. |
| APO data safety | Resource templates substitute only tokens authored in the embedded prompt file; caller prompt contents remain literal. | Covered by optimizer regression tests. |
| Build/runtime compatibility | Target remains `net9.0`; build on SDK 9 CI. | Local verification uses SDK 10 with runtime roll-forward when SDK/runtime 9 is unavailable; this is not native runtime-9 execution proof. |
| Release evidence | Exact expected package artifacts must publish/verify successfully before the release job runs. | Enforced by release workflow; actual feed availability is verified after publication. |

Update this matrix whenever a behavior changes status. Mark parity only for exercised behavior, and keep infrastructure or runtime limitations explicit.
