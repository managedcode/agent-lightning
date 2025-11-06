
# ManagedCode Agent Lightning Migration Plan

This plan tracks parity work between `external/microsoft-agent-lightning` (Python) and the C# port.

## Status Legend

- ✅ Complete in C#
- 🚧 Planned / not yet ported
- ❓ Needs investigation / decide if we port

## Core Building Blocks

| Component | Python Source | Status | Notes |
| --- | --- | --- | --- |
| Domain models | `agentlightning/types/core.py` | ✅ | `ManagedCode.AgentLightning.Core/Models` – rollout/attempt/triplet + hooks |
| Tracing models | `agentlightning/types/tracer.py` | ✅ | `ManagedCode.AgentLightning.Core/Tracing` – span DTOs & helpers with OpenTelemetry tests |
| Resources | `agentlightning/types/resources.py` | ✅ | `ManagedCode.AgentLightning.Core/Resources` – LLM/proxy/prompt resources mirrored |
| LitAgent base | `agentlightning/litagent/litagent.py` | ✅ | `LitAgentBase<T>` with hook lifecycle, `LightningAgent` derives from it |
| Adapter infrastructure | `agentlightning/adapter/base.py` | ✅ | `Adapters/Adapter`, `TraceAdapter`, `TraceToMessagesAdapter` implemented with tests |
| Runner infrastructure | `agentlightning/runner/base.py` | ✅ | `LitAgentRunner` processes rollouts via LightningAgent and stores spans |
| Store interface | `agentlightning/store/base.py` | ✅ | `ILightningStore` contract + `InMemoryLightningStore` covering queue/attempt/span lifecycle |
| Trainer orchestration | `agentlightning/trainer/trainer.py` | ✅ | `Trainer` orchestrates batches via store + runner (tested) |

## Span & Resource Adapters

| Adapter | Python Source | Status | Notes |
| --- | --- | --- | --- |
| Trace → messages | `adapter/messages.py` | ✅ | `TraceToMessagesAdapter` translates GenAI spans into OpenAI chat payloads |
| Trace → triplets | `adapter/triplet.py` | ✅ | `TracerTraceToTripletAdapter` exports triplets with reward policies |
| OTEL trace adapter | `adapter/base.py` | 🚧 | Hook Activity -> SpanModel bridging |

## Execution & Store Layers

| Component | Python Source | Status | Notes |
| --- | --- | --- | --- |
| LightningStore (async) | `store/base.py` | ✅ | `ILightningStore` exposes start/enqueue/start-attempt, span sequencing, and wait semantics |
| In-memory store | `store/memory.py` | ✅ | Expanded store handles attempts, spans, resources, and polling waits with thread-safe state |
| Client/server bridge | `store/client_server.py` | ❓ | Decide ASP.NET hosting approach |
| Runner execution strategies | `execution/*` | 🚧 | C# runner supports single-step execution, retry-aware polling, and resource hydration; parallel orchestration still pending |

## Algorithms & Training Pipelines

| Component | Python Source | Status | Notes |
| --- | --- | --- | --- |
| Algorithm base class | `algorithm/base.py` | 🚧 | Define async lifecycle (`SetupAsync`, `TrainAsync`, `TeardownAsync`) with dataset plumbing |
| Fast baseline algorithms | `algorithm/fast.py` | ❓ | Decide whether to port representative baseline for smoke tests |
| APO (Automatic Prompt Optimization) | `algorithm/apo/apo.py` | 🚧 | Requires prompt diffing, versioned templates, and evaluation harness |
| VERL distributed trainer | `algorithm/verl/*` | ❓ | Needs multi-process orchestration design in .NET (potential future phase) |
| Trainer legacy compat | `trainer/legacy.py` | 🚧 | Implement legacy hooks while aligning with new runner/store abstractions |
| Trainer orchestration | `trainer/trainer.py` | 🚧 | Port training loop, scheduler, and algorithm/run coordination |
| Registry/config utilities | `trainer/registry.py`, `trainer/init_utils.py` | 🚧 | Recreate component registration and config binding over `Options` |

## Reward & Instrumentation

| Component | Python Source | Status | Notes |
| --- | --- | --- | --- |
| Reward emitters | `emitter/reward.py`, `reward.py` | 🚧 | Implement reward span helpers with OTEL integration |
| Message/object emitters | `emitter/message.py`, `emitter/object.py`, `emitter/utils.py` | 🚧 | Required for parity in trace adapters |
| Instrumentation (AgentOps, LiteLLM, vLLM) | `instrumentation/*` | ❓ | Determine .NET bindings and optionality |
| Logging utilities | `logging.py` | ✅ | Replaced with `Microsoft.Extensions.Logging` configuration helpers |
| LLM proxy instrumentation | `llm_proxy.py` | ❓ | Evaluate hosting (ASP.NET Core) and span exporter support |

## Fixtures, Docs & Tooling

| Area | Status | Notes |
| --- | --- | --- |
| Python fixture import | 🚧 | Need harness to reuse JSON/SQLite fixtures from submodule |
| Integration test parity | 🚧 | Blocked until adapters, store, runner port complete |
| Docs & README updates | 🚧 | Document hosting, configuration, and migration progress |
| Packaging & CI | ✅ | .NET solution, format/test gates, and workflows in place |

## External Interfaces

| Component | Python Source | Status | Notes |
| --- | --- | --- | --- |
| LLM proxy service | `llm_proxy.py` | ❓ | Evaluate ASP.NET Core standalone proxy |
| Logging helpers | `logging.py` | ✅ | Using `Microsoft.Extensions.Logging` |
| Legacy server/client | `server.py`, `client.py` | ❓ | Decide on support for legacy flows |

## Test Parity

| Area | Status | Notes |
| --- | --- | --- |
| Core models & resources | ✅ | Unit tests in `ManagedCode.AgentLightning.Tests` |
| Span conversions | ✅ | `Tracing/SpanModelTests` |
| Resource helper coverage | ✅ | `Resources/ResourceModelTests` |
| Adapter tests | 🚧 | Need to mirror upstream fixtures |
| Runner/store/trainer integration | 🚧 | Blocked until components ported |

## Completed Work

- .NET 9 solution scaffolding with central package management
- Core rollout/attempt models and runtime scaffolding (`LightningAgent` + `LocalChatClient`)
- CI/CodeQL/release workflows (ManagedCode templates)
- Span/resource models with OpenTelemetry conversions and unit coverage

## Near-Term Priorities

1. Expand runner execution strategies (parallel workers, retries, resource coordination).
2. Reproduce key Python fixtures/tests for adapters, store logic, and integration flows.
3. Plan hosting story for LLM proxy / legacy endpoints and capture decisions.
4. Map persistence backends beyond in-memory (client/server bridge, durable stores).

## Tracking Guidance

Update this document whenever a component moves between statuses or when new design decisions affect parity.
