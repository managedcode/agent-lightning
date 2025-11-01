using ManagedCode.AgentLightning.Core.Adapters;
using ManagedCode.AgentLightning.Core.Models;
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

    public async Task<IReadOnlyList<LightningExecutionResult>> RunBatchAsync(int expectedRollouts, CancellationToken cancellationToken = default)
    {
        if (expectedRollouts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedRollouts));
        }

        var results = new List<LightningExecutionResult>(expectedRollouts);

        for (var i = 0; i < expectedRollouts; i++)
        {
            var attempted = await _store.DequeueRolloutAsync(cancellationToken).ConfigureAwait(false);
            if (attempted is null)
            {
                break;
            }

            try
            {
                var execution = await _agent.ExecuteAsync(attempted.Rollout.Input, cancellationToken).ConfigureAwait(false);
                results.Add(execution);

                attempted.Attempt.UpdateStatus(execution.Attempt.Status, execution.Attempt.EndTime ?? _timeProvider.GetUtcNow());
                await _store.UpdateAttemptAsync(attempted.Attempt, cancellationToken).ConfigureAwait(false);
                await _store.UpdateRolloutStatusAsync(attempted.Rollout.RolloutId, execution.Rollout.Status, execution.Rollout.EndTime, cancellationToken).ConfigureAwait(false);

                var span = BuildSpanFromResult(attempted, execution);
                await _store.AddSpanAsync(attempted.Rollout.RolloutId, attempted.Attempt.AttemptId, span, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                attempted.Attempt.UpdateStatus(AttemptStatus.Failed, _timeProvider.GetUtcNow());
                await _store.UpdateAttemptAsync(attempted.Attempt, cancellationToken).ConfigureAwait(false);
                await _store.UpdateRolloutStatusAsync(attempted.Rollout.RolloutId, RolloutStatus.Failed, _timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
                _logger.LogError(ex, "Runner failed while executing rollout {RolloutId}.", attempted.Rollout.RolloutId);
            }
        }

        return results;
    }

    private static SpanModel BuildSpanFromResult(AttemptedRollout attempted, LightningExecutionResult execution)
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
            sequenceId: attempted.Attempt.SequenceId,
            name: "agentlightning.completion",
            startTime: attempted.Attempt.StartTime.ToUnixTimeSeconds(),
            endTime: attempted.Attempt.EndTime?.ToUnixTimeSeconds());
    }
}
