using ManagedCode.AgentLightning.AgentRuntime.Runner;
using ManagedCode.AgentLightning.Core.Models;
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

    public async Task<IReadOnlyList<LightningExecutionResult>> TrainAsync(IEnumerable<object> tasks, TrainerOptions? options = null, CancellationToken cancellationToken = default)
    {
        var taskList = tasks?.ToList() ?? throw new ArgumentNullException(nameof(tasks));
        if (taskList.Count == 0)
        {
            return Array.Empty<LightningExecutionResult>();
        }

        foreach (var (payload, index) in taskList.Select((task, index) => (task, index)))
        {
            await _store.EnqueueRolloutAsync(
                payload,
                config: new RolloutConfig(),
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation("Enqueued {Count} rollouts for training.", taskList.Count);

        var results = await _runner.RunBatchAsync(taskList.Count, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Completed {Count} rollouts.", results.Count);

        return results;
    }
}
