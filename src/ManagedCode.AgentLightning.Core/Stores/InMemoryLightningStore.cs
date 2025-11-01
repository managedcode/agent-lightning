using System.Collections.Concurrent;
using System.Threading.Channels;
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
    private readonly ConcurrentDictionary<string, RolloutState> _rollouts = new(StringComparer.Ordinal);
    private readonly List<ResourcesUpdate> _resourcesHistory = new();
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

    public async Task EnqueueRolloutAsync(Rollout rollout, CancellationToken cancellationToken = default)
    {
        if (rollout is null)
        {
            throw new ArgumentNullException(nameof(rollout));
        }

        var state = new RolloutState(rollout);
        if (!_rollouts.TryAdd(rollout.RolloutId, state))
        {
            throw new InvalidOperationException($"Rollout '{rollout.RolloutId}' is already enqueued.");
        }

        rollout.TransitionTo(RolloutStatus.Queuing);
        await _queue.Writer.WriteAsync(rollout.RolloutId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AttemptedRollout?> DequeueRolloutAsync(CancellationToken cancellationToken = default)
    {
        while (await _queue.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (_queue.Reader.TryRead(out var rolloutId))
            {
                if (_rollouts.TryGetValue(rolloutId, out var state))
                {
                    return state.CreateAttempt(_timeProvider.GetUtcNow());
                }
            }
        }

        return null;
    }

    public Task UpdateAttemptAsync(Attempt attempt, CancellationToken cancellationToken = default)
    {
        if (attempt is null)
        {
            throw new ArgumentNullException(nameof(attempt));
        }

        if (_rollouts.TryGetValue(attempt.RolloutId, out var state))
        {
            state.UpdateAttempt(attempt);
        }

        return Task.CompletedTask;
    }

    public Task UpdateRolloutStatusAsync(string rolloutId, RolloutStatus status, DateTimeOffset? endTime = null, CancellationToken cancellationToken = default)
    {
        if (rolloutId is null)
        {
            throw new ArgumentNullException(nameof(rolloutId));
        }

        if (_rollouts.TryGetValue(rolloutId, out var state))
        {
            state.UpdateStatus(status, endTime ?? _timeProvider.GetUtcNow());
        }

        return Task.CompletedTask;
    }

    public Task AddSpanAsync(string rolloutId, string attemptId, SpanModel span, CancellationToken cancellationToken = default)
    {
        if (span is null)
        {
            throw new ArgumentNullException(nameof(span));
        }

        if (_rollouts.TryGetValue(rolloutId, out var state))
        {
            state.AddSpan(attemptId, span);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SpanModel>> GetSpansAsync(string rolloutId, string attemptId, CancellationToken cancellationToken = default)
    {
        if (_rollouts.TryGetValue(rolloutId, out var state))
        {
            return Task.FromResult(state.GetSpans(attemptId));
        }

        return Task.FromResult<IReadOnlyList<SpanModel>>(Array.Empty<SpanModel>());
    }

    public Task<IReadOnlyList<Rollout>> QueryRolloutsAsync(CancellationToken cancellationToken = default)
    {
        var result = _rollouts.Values
            .Select(state => state.RolloutSnapshot())
            .ToArray();
        return Task.FromResult<IReadOnlyList<Rollout>>(result);
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
            _resourcesHistory.Add(update);
        }

        return Task.FromResult(update);
    }

    public Task<ResourcesUpdate?> GetLatestResourcesAsync(CancellationToken cancellationToken = default)
    {
        lock (_resourcesHistory)
        {
            return Task.FromResult<ResourcesUpdate?>(_resourcesHistory.Count == 0 ? null : _resourcesHistory[^1]);
        }
    }

    private sealed class RolloutState
    {
        private readonly Rollout _rollout;
        private readonly List<Attempt> _attempts = new();
        private readonly Dictionary<string, List<SpanModel>> _spans = new(StringComparer.Ordinal);

        internal RolloutState(Rollout rollout)
        {
            _rollout = rollout;
        }

        internal AttemptedRollout CreateAttempt(DateTimeOffset startTime)
        {
            var sequence = _attempts.Count + 1;
            var attemptId = $"{_rollout.RolloutId}:attempt:{sequence}";
            var attempt = new Attempt(_rollout.RolloutId, attemptId, sequence, startTime, AttemptStatus.Running);
            _attempts.Add(attempt);
            _spans[attemptId] = new List<SpanModel>();
            _rollout.TransitionTo(RolloutStatus.Running);
            return new AttemptedRollout(_rollout, attempt);
        }

        internal void UpdateAttempt(Attempt attempt)
        {
            var index = _attempts.FindIndex(a => string.Equals(a.AttemptId, attempt.AttemptId, StringComparison.Ordinal));
            if (index >= 0)
            {
                _attempts[index] = attempt;
            }
            else
            {
                _attempts.Add(attempt);
            }
        }

        internal void UpdateStatus(RolloutStatus status, DateTimeOffset? endTime)
        {
            _rollout.TransitionTo(status);
            if (status is RolloutStatus.Succeeded or RolloutStatus.Failed or RolloutStatus.Cancelled)
            {
                if (endTime is { } timestamp)
                {
                    _rollout.Complete(timestamp);
                }
                else
                {
                    _rollout.Complete(DateTimeOffset.UtcNow);
                }
            }
        }

        internal void AddSpan(string attemptId, SpanModel span)
        {
            if (!_spans.TryGetValue(attemptId, out var collection))
            {
                collection = new List<SpanModel>();
                _spans[attemptId] = collection;
            }

            collection.Add(span);
        }

        internal IReadOnlyList<SpanModel> GetSpans(string attemptId)
        {
            if (_spans.TryGetValue(attemptId, out var collection))
            {
                return collection.ToArray();
            }

            return Array.Empty<SpanModel>();
        }

        internal Rollout RolloutSnapshot() => _rollout;
    }
}
