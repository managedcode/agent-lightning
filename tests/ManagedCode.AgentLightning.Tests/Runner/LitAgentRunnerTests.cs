using System.Collections.Generic;
using ManagedCode.AgentLightning.AgentRuntime;
using ManagedCode.AgentLightning.AgentRuntime.Runner;
using ManagedCode.AgentLightning.Core.Models;
using ManagedCode.AgentLightning.Core.Stores;
using ManagedCode.AgentLightning.Core.Tracing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;
using ManagedCode.AgentLightning.Tests.TestHelpers;

namespace ManagedCode.AgentLightning.Tests.Runner;

public class LitAgentRunnerTests
{
    private static (LightningAgent Agent, ILoggerFactory LoggerFactory) CreateAgent(
        string agentName,
        IChatClient? chatClient = null,
        LightningAgentOptions? options = null,
        ILoggerFactory? loggerFactory = null)
    {
        var factory = loggerFactory ?? LoggerFactory.Create(builder => builder.AddProvider(new NullLoggerProvider()));
        var agentLogger = factory.CreateLogger<LightningAgent>();
        var client = chatClient ?? new LocalChatClient(factory.CreateLogger<LocalChatClient>());
        var agent = new LightningAgent(
            client,
            options ?? new LightningAgentOptions
            {
                AgentName = agentName,
            },
            hooks: null,
            logger: agentLogger);
        return (agent, factory);
    }

    [Fact]
    public async Task Runner_ShouldProcessQueuedRollouts()
    {
        var store = new InMemoryLightningStore();
        var (agent, loggerFactory) = CreateAgent("runner-agent");
        var runner = new LitAgentRunner(agent, store, loggerFactory.CreateLogger<LitAgentRunner>());

        var enqueued = new List<Rollout>();
        for (var i = 0; i < 2; i++)
        {
            var rollout = await store.EnqueueRolloutAsync($"task-{i}");
            enqueued.Add(rollout);
        }

        var results = await runner.RunBatchAsync(2);

        results.Count.ShouldBe(2);

        foreach (var rollout in enqueued)
        {
            var attemptId = $"{rollout.RolloutId}:attempt:1";
            var spans = await store.GetSpansAsync(rollout.RolloutId, attemptId);
            spans.Count.ShouldBe(1);
            spans[0].Attributes.ContainsKey("gen_ai.completion.0.content").ShouldBeTrue();
        }
    }

    [Fact]
    public async Task Runner_ShouldRetryFailedRolloutsWhenConfigured()
    {
        var store = new InMemoryLightningStore();
        var retryConfig = new RolloutConfig
        {
            MaxAttempts = 2,
            RetryOn = new HashSet<AttemptStatus> { AttemptStatus.Failed },
        };

        var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(new NullLoggerProvider()));
        var flakyClient = new FlakyChatClient(loggerFactory, failuresBeforeSuccess: 1, responseText: "recovered");
        var options = new LightningAgentOptions
        {
            AgentName = "flaky-agent",
            RolloutConfig = retryConfig,
        };
        var (agent, _) = CreateAgent("flaky-agent", flakyClient, options, loggerFactory);
        var runner = new LitAgentRunner(agent, store, loggerFactory.CreateLogger<LitAgentRunner>());

        var rollout = await store.EnqueueRolloutAsync("unstable-task", config: retryConfig);

        var results = await runner.RunBatchAsync(1);

        results.Count.ShouldBe(1);
        var attempts = await store.QueryAttemptsAsync(rollout.RolloutId);
        attempts.Count.ShouldBe(2);
        attempts[^1].Status.ShouldBe(AttemptStatus.Succeeded);

        var spans = await store.GetSpansAsync(rollout.RolloutId, $"{rollout.RolloutId}:attempt:2");
        spans.Count.ShouldBe(1);
        spans[0].Attributes["gen_ai.completion.0.content"].ShouldBe("recovered");
    }
}
