using ManagedCode.AgentLightning.AgentRuntime.Runner;
using ManagedCode.AgentLightning.Core.Models;
using ManagedCode.AgentLightning.Core.Resources;
using ManagedCode.AgentLightning.Core.Tracing;
using ManagedCode.AgentLightning.Core.Stores;
using Microsoft.Extensions.Logging;

namespace ManagedCode.AgentLightning.AgentRuntime.Trainer;

public sealed record TrainerOptions
{
    public int MaxDegreeOfParallelism { get; init; } = 1;
}

/// <summary>
/// Coordinates rollouts between the store and runner.
/// </summary>
public sealed class Trainer
{
    private readonly ILightningStore _store;
    private readonly LightningAgent _agent;
    private readonly LitAgentRunner _runner;
    private readonly ILogger<Trainer> _logger;
    private readonly TimeProvider _timeProvider;

    public string PromptResourceName => _agent.PromptResourceName;

    public Trainer(ILightningStore store, LightningAgent agent, ILoggerFactory loggerFactory, TimeProvider? timeProvider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
        _logger = (loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory))).CreateLogger<Trainer>();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _runner = new LitAgentRunner(agent, store, loggerFactory.CreateLogger<LitAgentRunner>(), _timeProvider);
        agent.AttachTrainer(this);
        agent.AttachRunner(_runner);
    }

    public Task<IReadOnlyList<LightningExecutionResult>> TrainAsync(
        IEnumerable<object> tasks,
        TrainerOptions? options = null,
        CancellationToken cancellationToken = default) =>
        RunRolloutsAsync(tasks, resources: null, RolloutMode.Train, options, cancellationToken);

    public async Task<IReadOnlyList<LightningExecutionResult>> RunRolloutsAsync(
        IEnumerable<object> tasks,
        NamedResources? resources,
        RolloutMode mode,
        TrainerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var taskList = tasks?.ToList() ?? throw new ArgumentNullException(nameof(tasks));
        if (taskList.Count == 0)
        {
            return Array.Empty<LightningExecutionResult>();
        }

        string? resourcesId = null;
        if (resources is not null)
        {
            resourcesId = (await _store.SetResourcesAsync(resources, cancellationToken).ConfigureAwait(false)).ResourcesId;
        }

        foreach (var payload in taskList)
        {
            await _store.EnqueueRolloutAsync(
                payload,
                mode: mode,
                resourcesId: resourcesId,
                config: new RolloutConfig(),
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation("Enqueued {Count} rollouts in {Mode} mode.", taskList.Count, mode);

        var parallelism = options?.MaxDegreeOfParallelism ?? 1;
        if (parallelism < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxDegreeOfParallelism must be at least 1.");
        }

        var results = await _runner
            .RunBatchAsync(taskList.Count, parallelism, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation("Completed {Count} rollouts in {Mode} mode.", results.Count, mode);
        return results;
    }

    public Task<IReadOnlyList<SpanModel>> GetSpansAsync(
        string rolloutId,
        CancellationToken cancellationToken = default) =>
        _store.GetSpansAsync(rolloutId, cancellationToken: cancellationToken);

    public async Task RecordEvaluationAsync(
        LightningExecutionResult execution,
        Microsoft.Extensions.AI.Evaluation.EvaluationResult evaluation,
        double reward,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentNullException.ThrowIfNull(evaluation);
        if (!double.IsFinite(reward))
        {
            throw new ArgumentOutOfRangeException(nameof(reward), "Evaluation rewards must be finite.");
        }

        var attributes = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["gen_ai.reward.value"] = reward,
        };

        foreach (var (name, metric) in evaluation.Metrics)
        {
            if (metric is Microsoft.Extensions.AI.Evaluation.NumericMetric numeric)
            {
                attributes[$"agentlightning.evaluation.{name}.value"] = numeric.Value;
            }
            else if (metric is Microsoft.Extensions.AI.Evaluation.BooleanMetric boolean)
            {
                attributes[$"agentlightning.evaluation.{name}.value"] = boolean.Value;
            }

            if (!string.IsNullOrWhiteSpace(metric.Reason))
            {
                attributes[$"agentlightning.evaluation.{name}.reason"] = metric.Reason;
            }
        }

        var sequence = await _store.GetNextSpanSequenceIdAsync(
            execution.Rollout.RolloutId,
            execution.Attempt.AttemptId,
            cancellationToken).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds() / 1000d;
        var span = SpanModel.FromAttributes(
            attributes,
            execution.Rollout.RolloutId,
            execution.Attempt.AttemptId,
            sequence,
            "agentlightning.reward",
            startTime: now,
            endTime: now);
        await _store.AddSpanAsync(span, cancellationToken).ConfigureAwait(false);
    }

}
