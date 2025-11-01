using System.Collections.Generic;
using ManagedCode.AgentLightning.Core.Adapters;
using ManagedCode.AgentLightning.Core.Tracing;
using Shouldly;
using Xunit;

namespace ManagedCode.AgentLightning.Tests.Adapters;

public class TraceToMessagesAdapterTests
{
    [Fact]
    public void Adapt_ShouldProduceBasicConversation()
    {
        var adapter = new TraceToMessagesAdapter();

        var span = SpanModel.FromAttributes(
            new Dictionary<string, object?>
            {
                ["gen_ai.prompt.0.role"] = "user",
                ["gen_ai.prompt.0.content"] = "Hello",
                ["gen_ai.completion.0.role"] = "assistant",
                ["gen_ai.completion.0.content"] = "Hi there!",
            },
            rolloutId: "rollout-1",
            attemptId: "attempt-1",
            sequenceId: 1);

        var result = adapter.Adapt(new[] { span });

        result.Count.ShouldBe(1);
        var conversation = result[0];
        conversation.Tools.ShouldBeNull();
        conversation.Messages.Count.ShouldBe(2);
        conversation.Messages[0].Role.ShouldBe("user");
        conversation.Messages[0].Content.ShouldBe("Hello");
        conversation.Messages[1].Role.ShouldBe("assistant");
        conversation.Messages[1].Content.ShouldBe("Hi there!");
    }

    [Fact]
    public void Adapt_ShouldCaptureToolsAndFunctionDefinitions()
    {
        var adapter = new TraceToMessagesAdapter();

        var completionSpan = SpanModel.FromAttributes(
            new Dictionary<string, object?>
            {
                ["gen_ai.prompt.0.role"] = "user",
                ["gen_ai.prompt.0.content"] = "Search for weather",
                ["gen_ai.completion.0.role"] = "assistant",
                ["gen_ai.completion.0.content"] = "Let me check.",
                ["gen_ai.request.functions.0.name"] = "weather_lookup",
                ["gen_ai.request.functions.0.description"] = "Retrieve current weather",
                ["gen_ai.request.functions.0.parameters"] = "{\"type\":\"object\"}",
            },
            rolloutId: "rollout-2",
            attemptId: "attempt-5",
            sequenceId: 10);

        var toolSpan = SpanModel.FromAttributes(
            new Dictionary<string, object?>
            {
                ["tool.name"] = "weather_lookup",
                ["tool.parameters"] = "{\"location\":\"NYC\"}",
                ["tool.call.id"] = "call-123",
                ["tool.call.type"] = "function",
            },
            rolloutId: "rollout-2",
            attemptId: "attempt-5",
            sequenceId: 11,
            parentId: completionSpan.SpanId);

        var result = adapter.Adapt(new[] { completionSpan, toolSpan });

        result.Count.ShouldBe(1);
        var conversation = result[0];
        conversation.Messages.Count.ShouldBe(2);
        conversation.Messages[1].ToolCalls.ShouldNotBeNull();
        conversation.Messages[1].ToolCalls!.Count.ShouldBe(1);
        conversation.Messages[1].ToolCalls![0].Name.ShouldBe("weather_lookup");
        conversation.Messages[1].ToolCalls![0].Arguments.ShouldContain("NYC");
        conversation.Tools.ShouldNotBeNull();
        conversation.Tools!.Count.ShouldBe(1);
        conversation.Tools![0].Name.ShouldBe("weather_lookup");
    }
}
