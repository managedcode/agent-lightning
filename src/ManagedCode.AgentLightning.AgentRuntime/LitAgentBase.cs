using ManagedCode.AgentLightning.AgentRuntime.Runner;
using ManagedCode.AgentLightning.Core.Models;
using ManagedCode.AgentLightning.Core.Resources;

namespace ManagedCode.AgentLightning.AgentRuntime;

/// <summary>
/// Base class for user agents that participate in the Lightning trainer workflow.
/// </summary>
public abstract class LitAgentBase<TTask>
{
    private Trainer.Trainer? _trainer;
    private LitAgentRunner? _runner;

    public Trainer.Trainer Trainer => _trainer ?? throw new InvalidOperationException("Trainer has not been attached to the agent.");

    public LitAgentRunner Runner => _runner ?? throw new InvalidOperationException("Runner has not been attached to the agent.");

    internal void AttachTrainer(Trainer.Trainer trainer)
    {
        _trainer = trainer ?? throw new ArgumentNullException(nameof(trainer));
    }

    internal void AttachRunner(LitAgentRunner runner)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
    }

    public virtual bool IsAsync => true;

    public Task<LightningExecutionResult> ExecuteAsync(TTask task, CancellationToken cancellationToken = default) =>
        ExecuteAsync(task, resources: null, mode: null, resourcesId: null, cancellationToken);

    public Task<LightningExecutionResult> ExecuteAsync(
        TTask task,
        NamedResources? resources,
        RolloutMode? mode,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(task, resources, mode, resourcesId: null, cancellationToken);

    public async Task<LightningExecutionResult> ExecuteAsync(
        TTask task,
        NamedResources? resources,
        RolloutMode? mode,
        string? resourcesId,
        CancellationToken cancellationToken = default)
    {
        await OnRolloutStartAsync(task, cancellationToken).ConfigureAwait(false);
        var result = await RolloutAsync(task, resources, mode, resourcesId, cancellationToken).ConfigureAwait(false);
        await OnRolloutEndAsync(task, result, cancellationToken).ConfigureAwait(false);
        return result;
    }

    protected virtual Task OnRolloutStartAsync(TTask task, CancellationToken cancellationToken) => Task.CompletedTask;

    protected virtual Task OnRolloutEndAsync(TTask task, LightningExecutionResult result, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    protected abstract Task<LightningExecutionResult> RolloutAsync(
        TTask task,
        NamedResources? resources,
        RolloutMode? mode,
        string? resourcesId,
        CancellationToken cancellationToken);
}
