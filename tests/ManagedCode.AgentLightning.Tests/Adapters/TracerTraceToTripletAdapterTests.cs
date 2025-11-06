using System.Collections.Generic;
using ManagedCode.AgentLightning.Core.Adapters;
using ManagedCode.AgentLightning.Core.Models;
using ManagedCode.AgentLightning.Core.Tracing;
using Shouldly;
using Xunit;

namespace ManagedCode.AgentLightning.Tests.Adapters;

public class TracerTraceToTripletAdapterTests
{
    [Fact]
    public void Adapt_ShouldProjectTokensAndRewards()
    {
        var agentSpan = SpanModel.FromAttributes(
            new Dictionary<string, object?>
            {
                ["agent.name"] = "planner",
            },
            rolloutId: "rollout-1",
            attemptId: "attempt-1",
            sequenceId: 1,
            spanId: "root",
            name: "agent.session",
            startTime: 1,
            endTime: 100);

        var llmSpan = SpanModel.FromAttributes(
            new Dictionary<string, object?>
            {
                ["prompt_token_ids"] = new[] { 1, 2, 3 },
                ["response_token_ids"] = new[] { 4, 5 },
                ["gen_ai.response.id"] = "resp-1",
            },
            rolloutId: "rollout-1",
            attemptId: "attempt-1",
            sequenceId: 2,
            spanId: "llm-1",
            parentId: "root",
            name: "openai.chat.completion",
            startTime: 2,
            endTime: 3);

        var rewardSpan = SpanModel.FromAttributes(
            new Dictionary<string, object?>
            {
                ["reward"] = 1.25,
            },
            rolloutId: "rollout-1",
            attemptId: "attempt-1",
            sequenceId: 3,
            spanId: "reward-1",
            parentId: "root",
            name: "agentlightning.reward",
            startTime: 4,
            endTime: 5);

        var adapter = new TracerTraceToTripletAdapter();
        var result = adapter.Adapt(new[] { agentSpan, llmSpan, rewardSpan });

        result.Count.ShouldBe(1);
        var triplet = result[0];

        var prompt = triplet.Prompt.ShouldBeOfType<Dictionary<string, object?>>();
        prompt.ShouldContainKey("token_ids");
        prompt["token_ids"].ShouldBeAssignableTo<IReadOnlyList<int>>().ShouldBe(new[] { 1, 2, 3 });

        var response = triplet.Response.ShouldBeOfType<Dictionary<string, object?>>();
        response.ShouldContainKey("token_ids");
        response["token_ids"].ShouldBeAssignableTo<IReadOnlyList<int>>().ShouldBe(new[] { 4, 5 });

        triplet.Reward.ShouldBe(1.25);
        triplet.Metadata.ContainsKey("agent_name").ShouldBeTrue();
        triplet.Metadata["agent_name"].ShouldBe("planner");
        triplet.Metadata["response_id"].ShouldBe("resp-1");
    }

    [Fact]
    public void Adapt_ShouldDeduplicateByResponseId()
    {
        var agentSpan = SpanModel.FromAttributes(
            new Dictionary<string, object?>
            {
                ["agent.name"] = "deduper",
            },
            rolloutId: "rollout-2",
            attemptId: "attempt-2",
            sequenceId: 1,
            spanId: "root",
            name: "agent.session",
            startTime: 1,
            endTime: 20);

        var firstCall = SpanModel.FromAttributes(
            new Dictionary<string, object?>
            {
                ["prompt_token_ids"] = new[] { 7, 8 },
                ["response_token_ids"] = new[] { 9 },
                ["gen_ai.response.id"] = "shared-response",
            },
            rolloutId: "rollout-2",
            attemptId: "attempt-2",
            sequenceId: 2,
            spanId: "llm-1",
            parentId: "root",
            name: "openai.chat.completion",
            startTime: 2,
            endTime: 3);

        var duplicateCall = SpanModel.FromAttributes(
            new Dictionary<string, object?>
            {
                ["prompt_token_ids"] = new[] { 11 },
                ["response_token_ids"] = new[] { 12 },
                ["gen_ai.response.id"] = "shared-response",
            },
            rolloutId: "rollout-2",
            attemptId: "attempt-2",
            sequenceId: 3,
            spanId: "llm-2",
            parentId: "root",
            name: "openai.chat.completion",
            startTime: 4,
            endTime: 5);

        var adapter = new TracerTraceToTripletAdapter();
        var result = adapter.Adapt(new[] { agentSpan, firstCall, duplicateCall });

        result.Count.ShouldBe(1);
        var prompt = result[0].Prompt.ShouldBeOfType<Dictionary<string, object?>>();
        prompt["token_ids"].ShouldBeAssignableTo<IReadOnlyList<int>>().ShouldBe(new[] { 7, 8 });
    }

    [Fact]
    public void Adapt_ShouldUseFirstSiblingRewardPolicy()
    {
        var agentSpan = SpanModel.FromAttributes(
            new Dictionary<string, object?>
            {
                ["agent.name"] = "coach",
                ["agentops.task.output"] = new Dictionary<string, object?>
                {
                    ["type"] = "reward",
                    ["value"] = 9.5,
                },
            },
            rolloutId: "rollout-3",
            attemptId: "attempt-3",
            sequenceId: 1,
            spanId: "root",
            name: "agent.session",
            startTime: 0,
            endTime: 50);

        var llmSpan = SpanModel.FromAttributes(
            new Dictionary<string, object?>
            {
                ["prompt_token_ids"] = new[] { 1 },
                ["response_token_ids"] = new[] { 2 },
                ["gen_ai.response.id"] = "resp-3",
            },
            rolloutId: "rollout-3",
            attemptId: "attempt-3",
            sequenceId: 2,
            spanId: "llm",
            parentId: "root",
            name: "openai.chat.completion",
            startTime: 10,
            endTime: 20);

        var adapter = new TracerTraceToTripletAdapter(rewardMatchPolicy: RewardMatchPolicy.FirstSibling);
        var result = adapter.Adapt(new[] { agentSpan, llmSpan });

        result.Count.ShouldBe(1);
        result[0].Reward.ShouldBe(9.5);
    }

    [Fact]
    public void Adapt_ShouldSkipEmptyTokenSpansWhenConfigured()
    {
        var agentSpan = SpanModel.FromAttributes(
            new Dictionary<string, object?>
            {
                ["agent.name"] = "empty",
            },
            rolloutId: "rollout-4",
            attemptId: "attempt-4",
            sequenceId: 1,
            spanId: "root",
            name: "agent.session",
            startTime: 0,
            endTime: 5);

        var llmSpan = SpanModel.FromAttributes(
            new Dictionary<string, object?>(),
            rolloutId: "rollout-4",
            attemptId: "attempt-4",
            sequenceId: 2,
            spanId: "llm",
            parentId: "root",
            name: "openai.chat.completion",
            startTime: 1,
            endTime: 2);

        var adapter = new TracerTraceToTripletAdapter(skipEmptyTokenSpans: true);
        var result = adapter.Adapt(new[] { agentSpan, llmSpan });
        result.Count.ShouldBe(0);
    }
}
