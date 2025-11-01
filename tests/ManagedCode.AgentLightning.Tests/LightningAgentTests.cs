using ManagedCode.AgentLightning.AgentRuntime;
using ManagedCode.AgentLightning.Core.Models;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace ManagedCode.AgentLightning.Tests;

public sealed class LightningAgentTests
{
    [Fact]
    public async Task ExecuteAsync_ProducesResponseAndTriplet()
    {
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));
        var agentLogger = loggerFactory.CreateLogger<LightningAgent>();
        var chatClientLogger = loggerFactory.CreateLogger<LocalChatClient>();

        using var chatClient = new LocalChatClient(chatClientLogger);

        var options = new LightningAgentOptions
        {
            AgentName = "test-agent",
            SystemPrompt = "Echo the latest instruction.",
        };

        using var agent = new LightningAgent(chatClient, options, Enumerable.Empty<Hook>(), agentLogger);

        var result = await agent.ExecuteAsync("Explain reinforcement learning briefly.");

        result.Attempt.Status.ShouldBe(AttemptStatus.Succeeded);
        result.Rollout.Status.ShouldBe(RolloutStatus.Succeeded);
        result.Response.Text.ShouldContain("Explain reinforcement learning", Case.Insensitive);
        result.Triplet.Prompt.ShouldNotBeNull();
        result.Triplet.Response.ShouldNotBeNull();
    }
}
