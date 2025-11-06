using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ManagedCode.AgentLightning.Core.Adapters;
using ManagedCode.AgentLightning.Core.Models;
using ManagedCode.AgentLightning.Core.Resources;
using ManagedCode.AgentLightning.Core.Stores;
using ManagedCode.AgentLightning.Core.Tracing;
using Microsoft.Extensions.Logging;

namespace ManagedCode.AgentLightning.AgentRuntime.Runner;

/// <summary>
/// Processes rollouts fetched from an <see cref="ILightningStore"/> using a <see cref="LightningAgent"/>.
/// </summary>
public sealed class LitAgentRunner
{
    private readonly LightningAgent _agent;
    private readonly ILightningStore _store;
    private readonly ILogger<LitAgentRunner> _logger;
    private readonly TimeProvider _timeProvider;
    private string? _workerId;

    public LitAgentRunner(
        LightningAgent agent,
        ILightningStore store,
        ILogger<LitAgentRunner> logger,
        TimeProvider? timeProvider = null)
    {
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<IReadOnlyList<LightningExecutionResult>> RunBatchAsync(int expectedRollouts, CancellationToken cancellationToken = default) =>
        RunBatchAsync(expectedRollouts, degreeOfParallelism: 1, cancellationToken);

    public async Task<IReadOnlyList<LightningExecutionResult>> RunBatchAsync(
        int expectedRollouts,
        int degreeOfParallelism,
        CancellationToken cancellationToken = default)
    {
        if (expectedRollouts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedRollouts));
        }

        if (degreeOfParallelism < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(degreeOfParallelism));
        }

        var workerCount = Math.Min(degreeOfParallelism, expectedRollouts);
        var results = new ConcurrentBag<LightningExecutionResult>();
        var slots = expectedRollouts;
        var tasks = new List<Task>(workerCount);

        for (var i = 0; i < workerCount; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (!TryReserveSlot(ref slots))
                    {
                        break;
                    }

                    try
                    {
                        var attempted = await _store.DequeueRolloutAsync(cancellationToken).ConfigureAwait(false);
                        if (attempted is null)
                        {
                            ReleaseSlot(ref slots);
                            break;
                        }

                        var execution = await ExecuteAttemptAsync(attempted, cancellationToken).ConfigureAwait(false);
                        if (execution is null)
                        {
                            ReleaseSlot(ref slots);
                            continue;
                        }

                        results.Add(execution);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        ReleaseSlot(ref slots);
                        break;
                    }
                    catch
                    {
                        ReleaseSlot(ref slots);
                        throw;
                    }
                }
            }, cancellationToken));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.ToArray();
    }

    public Task RunAsync(
        int? maxRollouts = null,
        TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default) =>
        RunAsync(maxRollouts, pollInterval, degreeOfParallelism: 1, cancellationToken);

    public async Task RunAsync(
        int? maxRollouts,
        TimeSpan? pollInterval,
        int degreeOfParallelism,
        CancellationToken cancellationToken = default)
    {
        if (maxRollouts is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRollouts));
        }

        if (degreeOfParallelism < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(degreeOfParallelism));
        }

        if (pollInterval is { } interval && interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(pollInterval), "Poll interval must be positive.");
        }

        var delay = pollInterval ?? TimeSpan.FromSeconds(5);
        var workerCount = Math.Max(1, degreeOfParallelism);
        var completionSlots = maxRollouts ?? int.MaxValue;
        var tasks = new List<Task>(workerCount);

        for (var i = 0; i < workerCount; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (maxRollouts is not null && !TryReserveSlot(ref completionSlots))
                    {
                        break;
                    }

                    var slotReserved = maxRollouts is not null;

                    try
                    {
                        var attempted = await _store.DequeueRolloutAsync(cancellationToken).ConfigureAwait(false);
                        if (attempted is null)
                        {
                            if (slotReserved)
                            {
                                ReleaseSlot(ref completionSlots);
                                slotReserved = false;
                            }

                            if (delay > TimeSpan.Zero)
                            {
                                try
                                {
                                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                                }
                                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                                {
                                }
                            }

                            if (maxRollouts is null)
                            {
                                continue;
                            }

                            break;
                        }

                        var execution = await ExecuteAttemptAsync(attempted, cancellationToken).ConfigureAwait(false);
                        if (execution is null)
                        {
                            if (slotReserved)
                            {
                                ReleaseSlot(ref completionSlots);
                                slotReserved = false;
                            }

                            continue;
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        if (slotReserved)
                        {
                            ReleaseSlot(ref completionSlots);
                        }

                        break;
                    }
                    catch
                    {
                        if (slotReserved)
                        {
                            ReleaseSlot(ref completionSlots);
                        }

                        throw;
                    }
                }
            }, cancellationToken));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public async Task<Rollout> RunStepAsync(
        object input,
        NamedResources? resources = null,
        RolloutMode? mode = null,
        CancellationToken cancellationToken = default)
    {
        if (input is null)
        {
            throw new ArgumentNullException(nameof(input));
        }

        string? resourcesId = null;
        if (resources is not null)
        {
            var update = await _store.SetResourcesAsync(resources, cancellationToken).ConfigureAwait(false);
            resourcesId = update.ResourcesId;
        }

        var attempted = await _store.StartRolloutAsync(
            input,
            mode,
            resourcesId,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var execution = await ExecuteAttemptAsync(attempted, cancellationToken).ConfigureAwait(false);
        if (execution is null)
        {
            throw new InvalidOperationException($"Runner failed to complete rollout {attempted.Rollout.RolloutId}.");
        }

        var rollout = await _store.GetRolloutByIdAsync(attempted.Rollout.RolloutId, cancellationToken).ConfigureAwait(false);
        if (rollout is null)
        {
            throw new InvalidOperationException($"Rollout {attempted.Rollout.RolloutId} was not found after execution.");
        }

        return rollout;
    }

    private async Task<LightningExecutionResult?> ExecuteAttemptAsync(AttemptedRollout attempted, CancellationToken cancellationToken)
    {
        try
        {
            var workerId = GetWorkerId();
            attempted.Attempt.AttachWorker(workerId);
            attempted.Attempt.UpdateStatus(AttemptStatus.Running);
            attempted.Attempt.Touch(_timeProvider.GetUtcNow());
            await _store.UpdateAttemptAsync(attempted.Attempt, cancellationToken).ConfigureAwait(false);

            var resources = await ResolveResourcesAsync(attempted.Rollout, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(attempted.Rollout.ResourcesId) && resources is null)
            {
                _logger.LogError(
                    "Resources '{ResourcesId}' referenced by rollout {RolloutId} were not found.",
                    attempted.Rollout.ResourcesId,
                    attempted.Rollout.RolloutId);
                await FailRolloutAsync(attempted, cancellationToken).ConfigureAwait(false);
                return null;
            }

            var execution = await _agent.ExecuteAsync(
                attempted.Rollout.Input,
                resources,
                attempted.Rollout.Mode,
                attempted.Rollout.ResourcesId,
                cancellationToken).ConfigureAwait(false);

            CopyMetadata(execution.Attempt.Metadata, attempted.Attempt);
            CopyMetadata(execution.Rollout.Metadata, attempted.Rollout);

            attempted.Attempt.UpdateStatus(execution.Attempt.Status, execution.Attempt.EndTime ?? _timeProvider.GetUtcNow());
            await _store.UpdateAttemptAsync(attempted.Attempt, cancellationToken).ConfigureAwait(false);

            if (execution.Attempt.Status == AttemptStatus.Succeeded)
            {
                await _store.UpdateRolloutStatusAsync(
                    attempted.Rollout.RolloutId,
                    execution.Rollout.Status,
                    execution.Rollout.EndTime ?? _timeProvider.GetUtcNow(),
                    cancellationToken).ConfigureAwait(false);

                var sequenceId = await _store.GetNextSpanSequenceIdAsync(
                    attempted.Rollout.RolloutId,
                    attempted.Attempt.AttemptId,
                    cancellationToken).ConfigureAwait(false);

                var span = BuildSpanFromResult(attempted, execution, sequenceId);
                await _store.AddSpanAsync(span, cancellationToken).ConfigureAwait(false);

                return execution;
            }

            await HandleAttemptFailureAsync(attempted, cancellationToken).ConfigureAwait(false);
            _logger.LogWarning(
                "Rollout {RolloutId} attempt {AttemptId} completed with status {Status}.",
                attempted.Rollout.RolloutId,
                attempted.Attempt.AttemptId,
                execution.Attempt.Status);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            attempted.Attempt.UpdateStatus(AttemptStatus.Failed, _timeProvider.GetUtcNow());
            await _store.UpdateAttemptAsync(attempted.Attempt, cancellationToken).ConfigureAwait(false);
            await HandleAttemptFailureAsync(attempted, cancellationToken).ConfigureAwait(false);
            _logger.LogError(ex, "Runner failed while executing rollout {RolloutId}.", attempted.Rollout.RolloutId);
        }

        return null;
    }

    private string GetWorkerId() =>
        _workerId ??= $"runner-{Environment.CurrentManagedThreadId}";

    private async Task HandleAttemptFailureAsync(AttemptedRollout attempted, CancellationToken cancellationToken)
    {
        var shouldRetry = ShouldRetry(attempted.Rollout, attempted.Attempt);
        var status = shouldRetry ? RolloutStatus.Requeuing : RolloutStatus.Failed;
        var endTime = shouldRetry ? (DateTimeOffset?)null : _timeProvider.GetUtcNow();

        await _store.UpdateRolloutStatusAsync(
            attempted.Rollout.RolloutId,
            status,
            endTime,
            cancellationToken).ConfigureAwait(false);

        if (shouldRetry)
        {
            _logger.LogInformation(
                "Rollout {RolloutId} requeued after attempt {AttemptId} failed.",
                attempted.Rollout.RolloutId,
                attempted.Attempt.AttemptId);
        }
        else
        {
            _logger.LogWarning(
                "Rollout {RolloutId} failed after attempt {AttemptId}.",
                attempted.Rollout.RolloutId,
                attempted.Attempt.AttemptId);
        }
    }

    private static bool ShouldRetry(Rollout rollout, Attempt attempt)
    {
        if (attempt.SequenceId >= rollout.Config.MaxAttempts)
        {
            return false;
        }

        return rollout.Config.RetryOn.Contains(attempt.Status);
    }

    private static void CopyMetadata(IReadOnlyDictionary<string, object?>? source, Attempt target)
    {
        if (source is null)
        {
            return;
        }

        foreach (var (key, value) in source)
        {
            target.AddMetadata(key, value);
        }
    }

    private static void CopyMetadata(IReadOnlyDictionary<string, object?>? source, Rollout target)
    {
        if (source is null)
        {
            return;
        }

        foreach (var (key, value) in source)
        {
            target.AddMetadata(key, value);
        }
    }

    private async Task<NamedResources?> ResolveResourcesAsync(Rollout rollout, CancellationToken cancellationToken)
    {
        ResourcesUpdate? update = null;
        if (!string.IsNullOrEmpty(rollout.ResourcesId))
        {
            update = await _store.GetResourcesByIdAsync(rollout.ResourcesId, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            update = await _store.GetLatestResourcesAsync(cancellationToken).ConfigureAwait(false);
        }

        return update?.Resources;
    }

    private async Task FailRolloutAsync(AttemptedRollout attempted, CancellationToken cancellationToken)
    {
        attempted.Attempt.UpdateStatus(AttemptStatus.Failed, _timeProvider.GetUtcNow());
        await _store.UpdateAttemptAsync(attempted.Attempt, cancellationToken).ConfigureAwait(false);
        await _store.UpdateRolloutStatusAsync(
            attempted.Rollout.RolloutId,
            RolloutStatus.Failed,
            _timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);
    }

    private static SpanModel BuildSpanFromResult(AttemptedRollout attempted, LightningExecutionResult execution, int sequenceId)
    {
        var attributes = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (execution.Triplet.Prompt is IEnumerable<object?> prompts)
        {
            var index = 0;
            foreach (var prompt in prompts)
            {
                var type = prompt?.GetType();
                var role = type?.GetProperty("Role")?.GetValue(prompt)?.ToString() ?? "user";
                var text = type?.GetProperty("Text")?.GetValue(prompt)?.ToString() ?? string.Empty;
                attributes[$"gen_ai.prompt.{index}.role"] = role;
                attributes[$"gen_ai.prompt.{index}.content"] = text;
                index++;
            }
        }

        attributes["gen_ai.completion.0.role"] = "assistant";
        attributes["gen_ai.completion.0.content"] = execution.Response.Text;

        if (execution.Response.ResponseId is { Length: > 0 } responseId)
        {
            attributes["gen_ai.response.id"] = responseId;
        }

        if (execution.Triplet.Reward is { } reward)
        {
            attributes["gen_ai.reward.value"] = reward;
        }

        return SpanModel.FromAttributes(
            attributes,
            rolloutId: attempted.Rollout.RolloutId,
            attemptId: attempted.Attempt.AttemptId,
            sequenceId: sequenceId,
            name: "agentlightning.completion",
            startTime: attempted.Attempt.StartTime.ToUnixTimeSeconds(),
            endTime: attempted.Attempt.EndTime?.ToUnixTimeSeconds());
    }

    private static bool TryReserveSlot(ref int slots)
    {
        while (true)
        {
            var current = Volatile.Read(ref slots);
            if (current <= 0)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref slots, current - 1, current) == current)
            {
                return true;
            }
        }
    }

    private static void ReleaseSlot(ref int slots) =>
        Interlocked.Increment(ref slots);
}
