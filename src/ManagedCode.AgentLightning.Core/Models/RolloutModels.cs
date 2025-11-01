using System.Collections.Concurrent;
using System.Collections.ObjectModel;

namespace ManagedCode.AgentLightning.Core.Models;

/// <summary>
/// Possible lifecycle states for a rollout.
/// Mirrors <c>agentlightning.types.core.RolloutStatus</c>.
/// </summary>
public enum RolloutStatus
{
    Queuing,
    Preparing,
    Running,
    Failed,
    Succeeded,
    Cancelled,
    Requeuing,
}

/// <summary>
/// Execution status reported by a rollout attempt.
/// Mirrors <c>agentlightning.types.core.AttemptStatus</c>.
/// </summary>
public enum AttemptStatus
{
    Preparing,
    Running,
    Failed,
    Succeeded,
    Unresponsive,
    Timeout,
}

/// <summary>
/// Execution modes supported by the runtime.
/// Mirrors <c>agentlightning.types.core.RolloutMode</c>.
/// </summary>
public enum RolloutMode
{
    Train,
    Val,
    Test,
}

/// <summary>
/// Structured feedback captured during training.
/// </summary>
public sealed record Triplet
{
    public required object Prompt { get; init; }

    public required object Response { get; init; }

    public double? Reward { get; init; }

    public IReadOnlyDictionary<string, object?> Metadata { get; init; } =
        new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>());
}

/// <summary>
/// Configuration controlling retry semantics for a rollout.
/// </summary>
public sealed class RolloutConfig
{
    public TimeSpan? Timeout { get; init; }

    public TimeSpan? UnresponsiveTimeout { get; init; }

    public int MaxAttempts { get; init; } = 1;

    public IReadOnlySet<AttemptStatus> RetryOn { get; init; } =
        new HashSet<AttemptStatus>();

    public RolloutConfig Normalize()
    {
        if (MaxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxAttempts), "MaxAttempts must be at least 1.");
        }

        return this;
    }
}

/// <summary>
/// Execution attempt for a rollout.
/// </summary>
public sealed class Attempt
{
    private readonly ConcurrentDictionary<string, object?> _metadata;

    public Attempt(
        string rolloutId,
        string attemptId,
        int sequenceId,
        DateTimeOffset startTime,
        AttemptStatus status = AttemptStatus.Preparing,
        string? workerId = null,
        DateTimeOffset? lastHeartbeat = null,
        IReadOnlyDictionary<string, object?>? metadata = null)
    {
        RolloutId = rolloutId ?? throw new ArgumentNullException(nameof(rolloutId));
        AttemptId = attemptId ?? throw new ArgumentNullException(nameof(attemptId));
        SequenceId = sequenceId;
        if (SequenceId < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(sequenceId), "SequenceId must be at least 1.");
        }

        StartTime = startTime;
        Status = status;
        WorkerId = workerId;
        LastHeartbeatTime = lastHeartbeat;
        _metadata = new ConcurrentDictionary<string, object?>(metadata ?? new Dictionary<string, object?>());
    }

    public string RolloutId { get; }

    public string AttemptId { get; }

    public int SequenceId { get; }

    public DateTimeOffset StartTime { get; }

    public DateTimeOffset? EndTime { get; private set; }

    public AttemptStatus Status { get; private set; }

    public string? WorkerId { get; private set; }

    public DateTimeOffset? LastHeartbeatTime { get; private set; }

    public IReadOnlyDictionary<string, object?> Metadata => _metadata;

    public bool IsTerminal => Status is AttemptStatus.Failed or AttemptStatus.Succeeded or AttemptStatus.Timeout;

    public void AttachWorker(string workerId)
    {
        WorkerId = workerId ?? throw new ArgumentNullException(nameof(workerId));
        Touch();
    }

    public void Touch(DateTimeOffset? heartbeat = null)
    {
        LastHeartbeatTime = heartbeat ?? DateTimeOffset.UtcNow;
    }

    public void UpdateStatus(AttemptStatus status, DateTimeOffset? endTime = null)
    {
        Status = status;
        if (status is AttemptStatus.Failed or AttemptStatus.Succeeded or AttemptStatus.Timeout)
        {
            EndTime = endTime ?? DateTimeOffset.UtcNow;
        }
    }

    public void AddMetadata(string key, object? value)
    {
        _metadata[key] = value;
    }
}

/// <summary>
/// Rollout request served to an agent.
/// </summary>
public sealed class Rollout
{
    private readonly ConcurrentDictionary<string, object?> _metadata;

    public Rollout(
        string rolloutId,
        object input,
        DateTimeOffset startTime,
        RolloutConfig? config = null,
        RolloutMode? mode = null,
        string? resourcesId = null,
        IReadOnlyDictionary<string, object?>? metadata = null)
    {
        RolloutId = rolloutId ?? throw new ArgumentNullException(nameof(rolloutId));
        Input = input ?? throw new ArgumentNullException(nameof(input));
        StartTime = startTime;
        Config = (config ?? new RolloutConfig()).Normalize();
        Mode = mode;
        ResourcesId = resourcesId;
        Status = RolloutStatus.Queuing;
        _metadata = new ConcurrentDictionary<string, object?>(metadata ?? new Dictionary<string, object?>());
    }

    public string RolloutId { get; }

    public object Input { get; }

    public DateTimeOffset StartTime { get; }

    public DateTimeOffset? EndTime { get; private set; }

    public RolloutMode? Mode { get; }

    public string? ResourcesId { get; }

    public RolloutStatus Status { get; private set; }

    public RolloutConfig Config { get; }

    public IReadOnlyDictionary<string, object?> Metadata => _metadata;

    public void TransitionTo(RolloutStatus status)
    {
        Status = status;
        if (status is RolloutStatus.Failed or RolloutStatus.Succeeded or RolloutStatus.Cancelled)
        {
            EndTime ??= DateTimeOffset.UtcNow;
        }
    }

    public void Complete(DateTimeOffset? timestamp = null)
    {
        EndTime = timestamp ?? DateTimeOffset.UtcNow;
        Status = RolloutStatus.Succeeded;
    }

    public void AddMetadata(string key, object? value)
    {
        _metadata[key] = value;
    }
}

/// <summary>
/// Pairing between a rollout and the attempt processing it.
/// </summary>
public sealed class AttemptedRollout
{
    public AttemptedRollout(Rollout rollout, Attempt attempt)
    {
        Rollout = rollout ?? throw new ArgumentNullException(nameof(rollout));
        Attempt = attempt ?? throw new ArgumentNullException(nameof(attempt));

        if (!string.Equals(Rollout.RolloutId, Attempt.RolloutId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Attempt rollout id does not match rollout.", nameof(attempt));
        }
    }

    public Rollout Rollout { get; }

    public Attempt Attempt { get; }
}

/// <summary>
/// Generic response used by compatibility endpoints.
/// </summary>
public sealed record GenericResponse(
    string Status,
    string? Message,
    IReadOnlyDictionary<string, object?>? Data);

/// <summary>
/// Base class for workloads executed across multiple worker processes.
/// </summary>
public abstract class ParallelWorkerBase
{
    public int? WorkerId { get; private set; }

    public virtual Task InitAsync(CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public virtual Task InitWorkerAsync(int workerId, CancellationToken cancellationToken = default)
    {
        WorkerId = workerId;
        return Task.CompletedTask;
    }

    public abstract Task RunAsync(CancellationToken cancellationToken = default);

    public virtual Task TeardownWorkerAsync(CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public virtual Task TeardownAsync(CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

/// <summary>
/// The general interface for an indexed dataset.
/// </summary>
public interface IDataset<out T>
{
    T this[Index index] { get; }

    int Count { get; }
}

/// <summary>
/// Lifecycle hooks emitted by the agent runner.
/// </summary>
public abstract class Hook : ParallelWorkerBase
{
    public virtual ValueTask OnTraceStartAsync(LightningContext context, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    public virtual ValueTask OnTraceEndAsync(LightningContext context, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    public virtual ValueTask OnRolloutStartAsync(LightningContext context, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    public virtual ValueTask OnRolloutEndAsync(
        LightningContext context,
        IReadOnlyList<object> spans,
        CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;
}

/// <summary>
/// Ambient context for hook invocations.
/// </summary>
public sealed record LightningContext(
    object Agent,
    object Runner,
    Rollout Rollout);
