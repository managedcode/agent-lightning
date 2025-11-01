using TrainerRuntime = ManagedCode.AgentLightning.AgentRuntime.Trainer.Trainer;
using System.Linq;
using ManagedCode.AgentLightning.AgentRuntime;
using ManagedCode.AgentLightning.AgentRuntime.Trainer;
using ManagedCode.AgentLightning.Core.Models;
using ManagedCode.AgentLightning.Core.Stores;
using ManagedCode.AgentLightning.Core.Tracing;
using Microsoft.Extensions.Logging;
using ManagedCode.AgentLightning.Tests.TestHelpers;
using Shouldly;
using Xunit;

namespace ManagedCode.AgentLightning.Tests.Trainer;

public class TrainerTests
{
    private static (LightningAgent Agent, ILoggerFactory LoggerFactory) CreateAgent(string name)
    {
        var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(new NullLoggerProvider()));
        var chatClientLogger = loggerFactory.CreateLogger<LocalChatClient>();
        var agentLogger = loggerFactory.CreateLogger<LightningAgent>();
        var chatClient = new LocalChatClient(chatClientLogger);
        var agent = new LightningAgent(
            chatClient,
            new LightningAgentOptions
            {
                AgentName = name,
            },
            hooks: null,
            logger: agentLogger);
        return (agent, loggerFactory);
    }

    [Fact]
    public async Task Trainer_ShouldProcessTaskBatch()
    {
        var store = new InMemoryLightningStore();
        var (agent, loggerFactory) = CreateAgent("trainer-agent");
        var trainer = new TrainerRuntime(store, agent, loggerFactory);

        var tasks = new object[] { "task-1", "task-2", "task-3" };
        var results = await trainer.TrainAsync(tasks);

        results.Count.ShouldBe(3);

        var rollouts = await store.QueryRolloutsAsync();
        rollouts.Count.ShouldBe(3);
        rollouts.All(r => r.Status == RolloutStatus.Succeeded).ShouldBeTrue();
    }
}
