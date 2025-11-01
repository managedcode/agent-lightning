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
        var rollout = new Rollout("rollout-1", "task", _timeProvider.GetUtcNow(), new RolloutConfig());

        await store.EnqueueRolloutAsync(rollout);
        var attempted = await store.DequeueRolloutAsync();

        attempted.ShouldNotBeNull();
        attempted!.Rollout.RolloutId.ShouldBe("rollout-1");
        attempted.Attempt.AttemptId.ShouldContain("rollout-1:attempt:1");
    }

    [Fact]
    public async Task AddSpan_ShouldStoreSpanData()
    {
        var store = new InMemoryLightningStore(_timeProvider);
        var rollout = new Rollout("rollout-2", "task", _timeProvider.GetUtcNow(), new RolloutConfig());

        await store.EnqueueRolloutAsync(rollout);
        var attempted = await store.DequeueRolloutAsync();
        attempted.ShouldNotBeNull();

        var span = SpanModel.FromAttributes(
            new Dictionary<string, object?>
            {
                ["gen_ai.prompt.0.role"] = "user",
                ["gen_ai.prompt.0.content"] = "hi",
            },
            rolloutId: "rollout-2",
            attemptId: attempted!.Attempt.AttemptId,
            sequenceId: attempted.Attempt.SequenceId,
            name: "completion");

        await store.AddSpanAsync("rollout-2", attempted.Attempt.AttemptId, span);

        var stored = await store.GetSpansAsync("rollout-2", attempted.Attempt.AttemptId);
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
}
