using ManagedCode.AgentLightning.Core.Models;
using ManagedCode.AgentLightning.Core.Resources;
using ManagedCode.AgentLightning.Core.Tracing;

namespace ManagedCode.AgentLightning.Core.Stores;

/// <summary>
/// Persistence contract for queuing rollouts, tracking attempts, and capturing spans.
/// </summary>
public interface ILightningStore
{
    Task EnqueueRolloutAsync(Rollout rollout, CancellationToken cancellationToken = default);

    Task<AttemptedRollout?> DequeueRolloutAsync(CancellationToken cancellationToken = default);

    Task UpdateAttemptAsync(Attempt attempt, CancellationToken cancellationToken = default);

    Task UpdateRolloutStatusAsync(string rolloutId, RolloutStatus status, DateTimeOffset? endTime = null, CancellationToken cancellationToken = default);

    Task AddSpanAsync(string rolloutId, string attemptId, SpanModel span, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SpanModel>> GetSpansAsync(string rolloutId, string attemptId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Rollout>> QueryRolloutsAsync(CancellationToken cancellationToken = default);

    Task<ResourcesUpdate> SetResourcesAsync(NamedResources resources, CancellationToken cancellationToken = default);

    Task<ResourcesUpdate?> GetLatestResourcesAsync(CancellationToken cancellationToken = default);
}
