using System;
using System.Collections.Generic;
using ManagedCode.AgentLightning.Core.Models;
using ManagedCode.AgentLightning.Core.Resources;
using ManagedCode.AgentLightning.Core.Tracing;

namespace ManagedCode.AgentLightning.Core.Stores;

/// <summary>
/// Persistence contract for coordinating rollouts, attempts, telemetry spans, and resource snapshots.
/// </summary>
public interface ILightningStore
{
    Task<AttemptedRollout> StartRolloutAsync(
        object input,
        RolloutMode? mode = null,
        string? resourcesId = null,
        RolloutConfig? config = null,
        IReadOnlyDictionary<string, object?>? metadata = null,
        CancellationToken cancellationToken = default);

    Task<Rollout> EnqueueRolloutAsync(
        object input,
        RolloutMode? mode = null,
        string? resourcesId = null,
        RolloutConfig? config = null,
        IReadOnlyDictionary<string, object?>? metadata = null,
        CancellationToken cancellationToken = default);

    Task<AttemptedRollout?> DequeueRolloutAsync(CancellationToken cancellationToken = default);

    Task<AttemptedRollout> StartAttemptAsync(string rolloutId, CancellationToken cancellationToken = default);

    Task UpdateAttemptAsync(Attempt attempt, CancellationToken cancellationToken = default);

    Task UpdateRolloutStatusAsync(string rolloutId, RolloutStatus status, DateTimeOffset? endTime = null, CancellationToken cancellationToken = default);

    Task<SpanModel> AddSpanAsync(SpanModel span, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SpanModel>> GetSpansAsync(string rolloutId, string? attemptId = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Rollout>> QueryRolloutsAsync(
        IReadOnlyList<RolloutStatus>? statuses = null,
        IReadOnlyList<string>? rolloutIds = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Attempt>> QueryAttemptsAsync(string rolloutId, CancellationToken cancellationToken = default);

    Task<Rollout?> GetRolloutByIdAsync(string rolloutId, CancellationToken cancellationToken = default);

    Task<Attempt?> GetLatestAttemptAsync(string rolloutId, CancellationToken cancellationToken = default);

    Task<ResourcesUpdate> SetResourcesAsync(NamedResources resources, CancellationToken cancellationToken = default);

    Task<ResourcesUpdate?> GetResourcesByIdAsync(string resourcesId, CancellationToken cancellationToken = default);

    Task<ResourcesUpdate?> GetLatestResourcesAsync(CancellationToken cancellationToken = default);

    Task<int> GetNextSpanSequenceIdAsync(string rolloutId, string attemptId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Rollout>> WaitForRolloutsAsync(
        IReadOnlyList<string> rolloutIds,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);
}
