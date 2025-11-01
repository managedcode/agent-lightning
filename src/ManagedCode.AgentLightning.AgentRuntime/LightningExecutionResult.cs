using ManagedCode.AgentLightning.Core.Models;
using Microsoft.Extensions.AI;

namespace ManagedCode.AgentLightning.AgentRuntime;

/// <summary>
/// Result returned by <see cref="LightningAgent"/> after a rollout.
/// </summary>
public sealed record LightningExecutionResult(
    Rollout Rollout,
    Attempt Attempt,
    ChatResponse Response,
    Triplet Triplet);
