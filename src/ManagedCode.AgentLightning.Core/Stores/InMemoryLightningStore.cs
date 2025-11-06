using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ManagedCode.AgentLightning.Core.Models;
using ManagedCode.AgentLightning.Core.Resources;
using ManagedCode.AgentLightning.Core.Tracing;

namespace ManagedCode.AgentLightning.Core.Stores;

/// <summary>
/// In-memory implementation of <see cref="ILightningStore"/> suitable for testing and local execution.
/// </summary>
public sealed class InMemoryLightningStore : ILightningStore
{
    private readonly Channel<string> _queue;
    private readonly Dictionary<string, RolloutState> _rollouts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ResourcesUpdate> _resources = new(StringComparer.Ordinal);
    private readonly List<ResourcesUpdate> _resourcesHistory = new();
    private readonly object _sync = new();
    private readonly TimeProvider _timeProvider;

    public InMemoryLightningStore(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false,
        });
    }

    public Task<AttemptedRollout> StartRolloutAsync(
        object input,
        RolloutMode? mode = null,
        string? resourcesId = null,
        RolloutConfig? config = null,
        IReadOnlyDictionary<string, object?>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        if (input is null)
        {
            throw new ArgumentNullException(nameof(input));
        }

        var now = _timeProvider.GetUtcNow();
        var resolvedResourcesId = ResolveResourcesId(resourcesId);
        var rolloutId = GenerateRolloutId();
        var rollout = new Rollout(
            rolloutId,
            input,
            now,
            CloneConfig(config),
            mode,
            resolvedResourcesId,
            CopyMetadata(metadata));
        rollout.TransitionTo(RolloutStatus.Preparing);

        var state = new RolloutState(rollout);
        var attempted = state.CreateAttempt(now, AttemptStatus.Preparing);

        lock (_sync)
        {
            _rollouts.Add(rolloutId, state);
        }

        return Task.FromResult(attempted);
    }

    public async Task<Rollout> EnqueueRolloutAsync(
        object input,
        RolloutMode? mode = null,
        string? resourcesId = null,
        RolloutConfig? config = null,
        IReadOnlyDictionary<string, object?>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        if (input is null)
        {
            throw new ArgumentNullException(nameof(input));
        }

        var now = _timeProvider.GetUtcNow();
        var resolvedResourcesId = ResolveResourcesId(resourcesId);
        var rolloutId = GenerateRolloutId();
        var rollout = new Rollout(
            rolloutId,
            input,
            now,
            CloneConfig(config),
            mode,
            resolvedResourcesId,
            CopyMetadata(metadata));

        var state = new RolloutState(rollout);

        lock (_sync)
        {
            _rollouts.Add(rolloutId, state);
        }

        await _queue.Writer.WriteAsync(rolloutId, cancellationToken).ConfigureAwait(false);
        return rollout;
    }

    public async Task<AttemptedRollout?> DequeueRolloutAsync(CancellationToken cancellationToken = default)
    {
        while (await _queue.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (_queue.Reader.TryRead(out var rolloutId))
            {
                AttemptedRollout? attempted = null;

                lock (_sync)
                {
                    if (_rollouts.TryGetValue(rolloutId, out var state))
                    {
                        if (state.Attempts.Count >= state.Rollout.Config.MaxAttempts)
                        {
                            continue;
                        }

                        state.Rollout.TransitionTo(RolloutStatus.Preparing);
                        attempted = state.CreateAttempt(_timeProvider.GetUtcNow(), AttemptStatus.Preparing);
                    }
                }

                if (attempted is not null)
                {
                    return attempted;
                }
            }
        }

        return null;
    }

    public Task<AttemptedRollout> StartAttemptAsync(string rolloutId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rolloutId))
        {
            throw new ArgumentException("Rollout identifier must be provided.", nameof(rolloutId));
        }

        lock (_sync)
        {
            if (!_rollouts.TryGetValue(rolloutId, out var state))
            {
                throw new InvalidOperationException($"Rollout '{rolloutId}' is not registered.");
            }

            if (state.Attempts.Count >= state.Rollout.Config.MaxAttempts)
            {
                throw new InvalidOperationException($"Rollout '{rolloutId}' exhausted its max attempts ({state.Rollout.Config.MaxAttempts}).");
            }

            state.Rollout.TransitionTo(RolloutStatus.Preparing);
            return Task.FromResult(state.CreateAttempt(_timeProvider.GetUtcNow(), AttemptStatus.Preparing));
        }
    }

    public Task UpdateAttemptAsync(Attempt attempt, CancellationToken cancellationToken = default)
    {
        if (attempt is null)
        {
            throw new ArgumentNullException(nameof(attempt));
        }

        lock (_sync)
        {
            if (_rollouts.TryGetValue(attempt.RolloutId, out var state))
            {
                var index = state.Attempts.FindIndex(a => string.Equals(a.AttemptId, attempt.AttemptId, StringComparison.Ordinal));
                if (index >= 0)
                {
                    state.Attempts[index] = attempt;
                }
                else
                {
                    state.Attempts.Add(attempt);
                }
            }
        }

        return Task.CompletedTask;
    }

    public async Task UpdateRolloutStatusAsync(string rolloutId, RolloutStatus status, DateTimeOffset? endTime = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rolloutId))
        {
            throw new ArgumentException("Rollout identifier must be provided.", nameof(rolloutId));
        }

        bool shouldRequeue = false;
        lock (_sync)
        {
            if (_rollouts.TryGetValue(rolloutId, out var state))
            {
                state.Rollout.TransitionTo(status);

                if (IsTerminal(status))
                {
                    if (endTime is { } timestamp)
                    {
                        state.Rollout.Complete(timestamp);
                    }
                    else
                    {
                        state.Rollout.Complete(_timeProvider.GetUtcNow());
                    }
                }
                else if (status is RolloutStatus.Queuing or RolloutStatus.Requeuing)
                {
                    shouldRequeue = true;
                }
            }
        }

        if (shouldRequeue)
        {
            await _queue.Writer.WriteAsync(rolloutId, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task<SpanModel> AddSpanAsync(SpanModel span, CancellationToken cancellationToken = default)
    {
        if (span is null)
        {
            throw new ArgumentNullException(nameof(span));
        }

        SpanModel stored;

        lock (_sync)
        {
            if (!_rollouts.TryGetValue(span.RolloutId, out var state))
            {
                throw new InvalidOperationException($"Rollout '{span.RolloutId}' is not registered.");
            }

            if (!state.Spans.TryGetValue(span.AttemptId, out var collection))
            {
                collection = new List<SpanModel>();
                state.Spans[span.AttemptId] = collection;
            }

            if (!state.SpanCounters.TryGetValue(span.AttemptId, out var counter))
            {
                counter = 0;
            }

            if (span.SequenceId <= 0)
            {
                counter += 1;
                span = span with { SequenceId = counter };
            }
            else
            {
                counter = Math.Max(counter + 1, span.SequenceId);
            }

            state.SpanCounters[span.AttemptId] = counter;
            collection.Add(span);
            stored = span;

            var now = _timeProvider.GetUtcNow();
            var attempt = state.Attempts.FirstOrDefault(a => string.Equals(a.AttemptId, span.AttemptId, StringComparison.Ordinal));
            if (attempt is null)
            {
                throw new InvalidOperationException($"Attempt '{span.AttemptId}' was not found for rollout '{span.RolloutId}'.");
            }

            attempt.Touch(now);
            if (attempt.Status is AttemptStatus.Preparing or AttemptStatus.Unresponsive)
            {
                attempt.UpdateStatus(AttemptStatus.Running);
            }

            if (state.Rollout.Status is RolloutStatus.Preparing or RolloutStatus.Requeuing or RolloutStatus.Queuing)
            {
                state.Rollout.TransitionTo(RolloutStatus.Running);
            }
        }

        return Task.FromResult(stored);
    }

    public Task<IReadOnlyList<SpanModel>> GetSpansAsync(string rolloutId, string? attemptId = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rolloutId))
        {
            throw new ArgumentException("Rollout identifier must be provided.", nameof(rolloutId));
        }

        lock (_sync)
        {
            if (!_rollouts.TryGetValue(rolloutId, out var state))
            {
                return Task.FromResult<IReadOnlyList<SpanModel>>(Array.Empty<SpanModel>());
            }

            if (string.IsNullOrEmpty(attemptId))
            {
                return Task.FromResult<IReadOnlyList<SpanModel>>(state.Spans.Values
                    .SelectMany(list => list)
                    .OrderBy(span => span.SequenceId)
                    .ToArray());
            }

            var targetAttemptId = string.Equals(attemptId, "latest", StringComparison.OrdinalIgnoreCase)
                ? state.Attempts.LastOrDefault()?.AttemptId
                : attemptId;

            if (targetAttemptId is null)
            {
                return Task.FromResult<IReadOnlyList<SpanModel>>(Array.Empty<SpanModel>());
            }

            if (!state.Spans.TryGetValue(targetAttemptId, out var collection))
            {
                return Task.FromResult<IReadOnlyList<SpanModel>>(Array.Empty<SpanModel>());
            }

            return Task.FromResult<IReadOnlyList<SpanModel>>(collection.OrderBy(span => span.SequenceId).ToArray());
        }
    }

    public Task<IReadOnlyList<Rollout>> QueryRolloutsAsync(
        IReadOnlyList<RolloutStatus>? statuses = null,
        IReadOnlyList<string>? rolloutIds = null,
        CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            IEnumerable<RolloutState> states = _rollouts.Values;

            if (statuses is { Count: > 0 })
            {
                var filter = new HashSet<RolloutStatus>(statuses);
                states = states.Where(state => filter.Contains(state.Rollout.Status));
            }

            if (rolloutIds is { Count: > 0 })
            {
                var filter = new HashSet<string>(rolloutIds, StringComparer.Ordinal);
                states = states.Where(state => filter.Contains(state.Rollout.RolloutId));
            }

            return Task.FromResult<IReadOnlyList<Rollout>>(states.Select(state => state.Rollout).ToArray());
        }
    }

    public Task<IReadOnlyList<Attempt>> QueryAttemptsAsync(string rolloutId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rolloutId))
        {
            throw new ArgumentException("Rollout identifier must be provided.", nameof(rolloutId));
        }

        lock (_sync)
        {
            if (!_rollouts.TryGetValue(rolloutId, out var state))
            {
                throw new InvalidOperationException($"Rollout '{rolloutId}' is not registered.");
            }

            return Task.FromResult<IReadOnlyList<Attempt>>(state.Attempts.Select(CloneAttempt).ToArray());
        }
    }

    public Task<Rollout?> GetRolloutByIdAsync(string rolloutId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rolloutId))
        {
            throw new ArgumentException("Rollout identifier must be provided.", nameof(rolloutId));
        }

        lock (_sync)
        {
            return Task.FromResult<Rollout?>(_rollouts.TryGetValue(rolloutId, out var state) ? state.Rollout : null);
        }
    }

    public Task<Attempt?> GetLatestAttemptAsync(string rolloutId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rolloutId))
        {
            throw new ArgumentException("Rollout identifier must be provided.", nameof(rolloutId));
        }

        lock (_sync)
        {
            if (_rollouts.TryGetValue(rolloutId, out var state) && state.Attempts.Count > 0)
            {
                return Task.FromResult<Attempt?>(CloneAttempt(state.Attempts[^1]));
            }

            return Task.FromResult<Attempt?>(null);
        }
    }

    public Task<ResourcesUpdate> SetResourcesAsync(NamedResources resources, CancellationToken cancellationToken = default)
    {
        if (resources is null)
        {
            throw new ArgumentNullException(nameof(resources));
        }

        var update = new ResourcesUpdate(Guid.NewGuid().ToString("N"), resources);
        lock (_resourcesHistory)
        {
            _resources[update.ResourcesId] = update;
            _resourcesHistory.Add(update);
        }

        return Task.FromResult(update);
    }

    public Task<ResourcesUpdate?> GetResourcesByIdAsync(string resourcesId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(resourcesId))
        {
            throw new ArgumentException("Resource identifier must be provided.", nameof(resourcesId));
        }

        lock (_resourcesHistory)
        {
            return Task.FromResult<ResourcesUpdate?>(_resources.TryGetValue(resourcesId, out var update) ? update : null);
        }
    }

    public Task<ResourcesUpdate?> GetLatestResourcesAsync(CancellationToken cancellationToken = default)
    {
        lock (_resourcesHistory)
        {
            return Task.FromResult<ResourcesUpdate?>(_resourcesHistory.Count == 0 ? null : _resourcesHistory[^1]);
        }
    }

    public Task<int> GetNextSpanSequenceIdAsync(string rolloutId, string attemptId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rolloutId))
        {
            throw new ArgumentException("Rollout identifier must be provided.", nameof(rolloutId));
        }

        if (string.IsNullOrWhiteSpace(attemptId))
        {
            throw new ArgumentException("Attempt identifier must be provided.", nameof(attemptId));
        }

        lock (_sync)
        {
            if (!_rollouts.TryGetValue(rolloutId, out var state))
            {
                throw new InvalidOperationException($"Rollout '{rolloutId}' is not registered.");
            }

            var next = state.SpanCounters.TryGetValue(attemptId, out var counter) ? counter + 1 : 1;
            state.SpanCounters[attemptId] = next;
            return Task.FromResult(next);
        }
    }

    public async Task<IReadOnlyList<Rollout>> WaitForRolloutsAsync(
        IReadOnlyList<string> rolloutIds,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (rolloutIds is null || rolloutIds.Count == 0)
        {
            return Array.Empty<Rollout>();
        }

        var pending = new HashSet<string>(rolloutIds, StringComparer.Ordinal);
        var completed = new List<Rollout>();
        var deadline = timeout.HasValue ? _timeProvider.GetUtcNow() + timeout.Value : (DateTimeOffset?)null;

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            List<string>? finished = null;

            lock (_sync)
            {
                foreach (var id in pending)
                {
                    if (!_rollouts.TryGetValue(id, out var state))
                    {
                        throw new InvalidOperationException($"Rollout '{id}' is not registered.");
                    }

                    if (state.IsTerminal)
                    {
                        completed.Add(state.Rollout);
                        finished ??= new List<string>();
                        finished.Add(id);
                    }
                }
            }

            if (finished is not null)
            {
                foreach (var id in finished)
                {
                    pending.Remove(id);
                }
            }

            if (pending.Count == 0)
            {
                break;
            }

            if (deadline.HasValue && _timeProvider.GetUtcNow() >= deadline.Value)
            {
                break;
            }

            var remaining = deadline.HasValue ? deadline.Value - _timeProvider.GetUtcNow() : TimeSpan.FromMilliseconds(200);
            var delay = remaining > TimeSpan.Zero ? TimeSpan.FromMilliseconds(Math.Min(200, remaining.TotalMilliseconds)) : TimeSpan.Zero;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                break;
            }
        }

        return completed;
    }

    private string? ResolveResourcesId(string? resourcesId)
    {
        if (string.IsNullOrEmpty(resourcesId))
        {
            lock (_resourcesHistory)
            {
                return _resourcesHistory.Count == 0 ? null : _resourcesHistory[^1].ResourcesId;
            }
        }

        lock (_resourcesHistory)
        {
            if (!_resources.ContainsKey(resourcesId))
            {
                throw new InvalidOperationException($"Resources '{resourcesId}' are not registered.");
            }

            return resourcesId;
        }
    }

    private static string GenerateRolloutId() => $"rollout-{Guid.NewGuid():N}";

    private static RolloutConfig CloneConfig(RolloutConfig? config)
    {
        var source = config ?? new RolloutConfig();
        return new RolloutConfig
        {
            Timeout = source.Timeout,
            UnresponsiveTimeout = source.UnresponsiveTimeout,
            MaxAttempts = source.MaxAttempts,
            RetryOn = new HashSet<AttemptStatus>(source.RetryOn),
        };
    }

    private static IReadOnlyDictionary<string, object?> CopyMetadata(IReadOnlyDictionary<string, object?>? metadata)
    {
        return metadata is null
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            : new Dictionary<string, object?>(metadata, StringComparer.Ordinal);
    }

    private static Attempt CloneAttempt(Attempt attempt) =>
        new(
            attempt.RolloutId,
            attempt.AttemptId,
            attempt.SequenceId,
            attempt.StartTime,
            attempt.Status,
            attempt.WorkerId,
            attempt.LastHeartbeatTime,
            attempt.Metadata.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal));

    private static bool IsTerminal(RolloutStatus status) =>
        status is RolloutStatus.Succeeded or RolloutStatus.Failed or RolloutStatus.Cancelled;

    private sealed class RolloutState
    {
        internal RolloutState(Rollout rollout)
        {
            Rollout = rollout;
        }

        internal Rollout Rollout { get; }

        internal List<Attempt> Attempts { get; } = new();

        internal Dictionary<string, List<SpanModel>> Spans { get; } = new(StringComparer.Ordinal);

        internal Dictionary<string, int> SpanCounters { get; } = new(StringComparer.Ordinal);

        internal bool IsTerminal => IsTerminalStatus(Rollout.Status);

        internal AttemptedRollout CreateAttempt(DateTimeOffset startTime, AttemptStatus status)
        {
            var sequence = Attempts.Count + 1;
            var attemptId = $"{Rollout.RolloutId}:attempt:{sequence}";
            var attempt = new Attempt(Rollout.RolloutId, attemptId, sequence, startTime, status);
            Attempts.Add(attempt);
            Spans[attemptId] = new List<SpanModel>();
            SpanCounters[attemptId] = 0;
            return new AttemptedRollout(Rollout, attempt);
        }

        private static bool IsTerminalStatus(RolloutStatus status) =>
            status is RolloutStatus.Succeeded or RolloutStatus.Failed or RolloutStatus.Cancelled;
    }
}
