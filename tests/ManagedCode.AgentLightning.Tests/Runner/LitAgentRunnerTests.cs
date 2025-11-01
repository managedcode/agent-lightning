using ManagedCode.AgentLightning.AgentRuntime;
using ManagedCode.AgentLightning.AgentRuntime.Runner;
using ManagedCode.AgentLightning.Core.Models;
using ManagedCode.AgentLightning.Core.Stores;
using ManagedCode.AgentLightning.Core.Tracing;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;
using ManagedCode.AgentLightning.Tests.TestHelpers;

namespace ManagedCode.AgentLightning.Tests.Runner;

public class LitAgentRunnerTests
{
    private static (LightningAgent Agent, ILoggerFactory LoggerFactory) CreateAgent(string agentName)
    {
        var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(new NullLoggerProvider()));
        var chatClientLogger = loggerFactory.CreateLogger<LocalChatClient>();
        var agentLogger = loggerFactory.CreateLogger<LightningAgent>();
        var chatClient = new LocalChatClient(chatClientLogger);
        var agent = new LightningAgent(
            chatClient,
            new LightningAgentOptions
            {
                AgentName = agentName,
            },
            hooks: null,
            logger: agentLogger);
        return (agent, loggerFactory);
    }

    [Fact]
    public async Task Runner_ShouldProcessQueuedRollouts()
    {
        var store = new InMemoryLightningStore();
        var (agent, loggerFactory) = CreateAgent("runner-agent");
        var runner = new LitAgentRunner(agent, store, loggerFactory.CreateLogger<LitAgentRunner>());

        var now = TimeProvider.System.GetUtcNow();
        for (var i = 0; i < 2; i++)
        {
            var rollout = new Rollout($"rollout-{i}", $"task-{i}", now);
            await store.EnqueueRolloutAsync(rollout);
        }

        var results = await runner.RunBatchAsync(2);

        results.Count.ShouldBe(2);

        var spans = await store.GetSpansAsync("rollout-0", "rollout-0:attempt:1");
        spans.Count.ShouldBe(1);
        spans[0].Attributes.ContainsKey("gen_ai.completion.0.content").ShouldBeTrue();
    }
}
