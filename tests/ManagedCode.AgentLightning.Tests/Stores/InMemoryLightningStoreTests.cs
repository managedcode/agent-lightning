using System;
using System.Collections.Generic;
using ManagedCode.AgentLightning.Core.Models;
using ManagedCode.AgentLightning.Core.Resources;
using ManagedCode.AgentLightning.Core.Stores;
using ManagedCode.AgentLightning.Core.Tracing;
using Shouldly;
using Xunit;

namespace ManagedCode.AgentLightning.Tests.Stores;

public class InMemoryLightningStoreTests
{
    private readonly TimeProvider _timeProvider = TimeProvider.System;

    [Fact]
    public async Task EnqueueAndDequeue_ShouldReturnAttemptedRollout()
    {
        var store = new InMemoryLightningStore(_timeProvider);

        var rollout = await store.EnqueueRolloutAsync("task");
        var attempted = await store.DequeueRolloutAsync();

        attempted.ShouldNotBeNull();
        attempted!.Rollout.RolloutId.ShouldStartWith("rollout-");
        attempted.Attempt.AttemptId.ShouldContain(":attempt:1");
        attempted.Rollout.Input.ShouldBe("task");
    }

    [Fact]
    public async Task AddSpan_ShouldStoreSpanData()
    {
        var store = new InMemoryLightningStore(_timeProvider);

        var rollout = await store.EnqueueRolloutAsync("task");
        var attempted = await store.DequeueRolloutAsync();
        attempted.ShouldNotBeNull();

        var span = SpanModel.FromAttributes(
            new Dictionary<string, object?>
            {
                ["gen_ai.prompt.0.role"] = "user",
                ["gen_ai.prompt.0.content"] = "hi",
            },
            rolloutId: attempted!.Rollout.RolloutId,
            attemptId: attempted!.Attempt.AttemptId,
            sequenceId: attempted.Attempt.SequenceId,
            name: "completion");

        await store.AddSpanAsync(span);

        var stored = await store.GetSpansAsync(rollout.RolloutId, attempted.Attempt.AttemptId);
        stored.Count.ShouldBe(1);
        stored[0].Attributes.ContainsKey("gen_ai.prompt.0.content").ShouldBeTrue();
    }

    [Fact]
    public async Task SetResources_ShouldReturnLatest()
    {
        var store = new InMemoryLightningStore(_timeProvider);
        var resources = new NamedResources(new Dictionary<string, ResourceDefinition>
        {
            ["llm"] = new LlmResource { Endpoint = "https://example", Model = "test-model" },
        });

        var update = await store.SetResourcesAsync(resources);
        update.ResourcesId.ShouldNotBeNull();

        var latest = await store.GetLatestResourcesAsync();
        latest.ShouldNotBeNull();
        latest!.Resources.ShouldContainKey("llm");
    }

    [Fact]
    public async Task StartRollout_ShouldCreatePreparingAttempt()
    {
        var store = new InMemoryLightningStore(_timeProvider);

        var attempted = await store.StartRolloutAsync("immediate");

        attempted.Attempt.Status.ShouldBe(AttemptStatus.Preparing);
        var attempts = await store.QueryAttemptsAsync(attempted.Rollout.RolloutId);
        attempts.Count.ShouldBe(1);
    }

    [Fact]
    public async Task StartAttempt_ShouldIncrementSequence()
    {
        var store = new InMemoryLightningStore(_timeProvider);
        var rollout = await store.EnqueueRolloutAsync("task", config: new RolloutConfig { MaxAttempts = 2 });
        var first = await store.DequeueRolloutAsync();
        first.ShouldNotBeNull();

        first!.Attempt.UpdateStatus(AttemptStatus.Failed, _timeProvider.GetUtcNow());
        await store.UpdateAttemptAsync(first.Attempt);
        await store.UpdateRolloutStatusAsync(rollout.RolloutId, RolloutStatus.Requeuing);

        var second = await store.StartAttemptAsync(rollout.RolloutId);

        second.Attempt.SequenceId.ShouldBe(2);
    }

    [Fact]
    public async Task WaitForRollouts_ShouldReturnCompletedRollouts()
    {
        var store = new InMemoryLightningStore(_timeProvider);
        var rollout = await store.EnqueueRolloutAsync("task");
        var attempted = await store.DequeueRolloutAsync();
        attempted.ShouldNotBeNull();

        attempted!.Attempt.UpdateStatus(AttemptStatus.Succeeded, _timeProvider.GetUtcNow());
        await store.UpdateAttemptAsync(attempted.Attempt);
        await store.UpdateRolloutStatusAsync(rollout.RolloutId, RolloutStatus.Succeeded, _timeProvider.GetUtcNow());

        var completed = await store.WaitForRolloutsAsync(new[] { rollout.RolloutId }, TimeSpan.FromMilliseconds(50));

        completed.Count.ShouldBe(1);
        completed[0].Status.ShouldBe(RolloutStatus.Succeeded);
    }

    [Fact]
    public async Task GetNextSpanSequenceId_ShouldBeMonotonicPerAttempt()
    {
        var store = new InMemoryLightningStore(_timeProvider);
        var rollout = await store.EnqueueRolloutAsync("task");
        var attempted = await store.DequeueRolloutAsync();
        attempted.ShouldNotBeNull();

        var first = await store.GetNextSpanSequenceIdAsync(rollout.RolloutId, attempted!.Attempt.AttemptId);
        var second = await store.GetNextSpanSequenceIdAsync(rollout.RolloutId, attempted.Attempt.AttemptId);

        first.ShouldBe(1);
        second.ShouldBe(2);
    }

    [Fact]
    public async Task QueryRollouts_ShouldFilterByStatus()
    {
        var store = new InMemoryLightningStore(_timeProvider);
        await store.EnqueueRolloutAsync("complete");
        var pending = await store.EnqueueRolloutAsync("pending");

        var attempted = await store.DequeueRolloutAsync();
        attempted.ShouldNotBeNull();
        attempted!.Attempt.UpdateStatus(AttemptStatus.Succeeded, _timeProvider.GetUtcNow());
        await store.UpdateAttemptAsync(attempted.Attempt);
        await store.UpdateRolloutStatusAsync(attempted.Rollout.RolloutId, RolloutStatus.Succeeded, _timeProvider.GetUtcNow());

        var rollouts = await store.QueryRolloutsAsync(new[] { RolloutStatus.Succeeded });

        rollouts.Count.ShouldBe(1);
        rollouts[0].RolloutId.ShouldBe(attempted.Rollout.RolloutId);
        rollouts[0].Status.ShouldBe(RolloutStatus.Succeeded);

        var lookup = await store.GetRolloutByIdAsync(pending.RolloutId);
        lookup.ShouldNotBeNull();
        lookup!.Status.ShouldBe(RolloutStatus.Queuing);
    }
}
