using System.Collections.Generic;
using System.Diagnostics;
using ManagedCode.AgentLightning.Core.Tracing;
using OpenTelemetry.Resources;
using Shouldly;
using Xunit;

namespace ManagedCode.AgentLightning.Tests.Tracing;

public class SpanModelTests
{
    private static readonly ActivityListener Listener;

    static SpanModelTests()
    {
        Listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = _ => { },
            ActivityStopped = _ => { },
        };

        ActivitySource.AddActivityListener(Listener);
    }

    [Fact]
    public void FromActivity_MapsCoreFields()
    {
        using var source = new ActivitySource("ManagedCode.AgentLightning.Tests");
        using var activity = source.StartActivity("rollout-span", ActivityKind.Client)!;

        activity.SetTag("agent.rollout", "demo");
        activity.SetTag("iteration", 7);
        activity.SetTag("score", 0.87);
        activity.AddTag("labels", new[] { "a", "b" });
        activity.SetStatus(ActivityStatusCode.Ok, "completed");
        activity.AddEvent(new ActivityEvent("reward", tags: new ActivityTagsCollection(new Dictionary<string, object?>
        {
            ["value"] = 0.5,
        })));

        var linkContext = new ActivityContext(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded);
        activity.AddLink(new ActivityLink(linkContext, new ActivityTagsCollection(new Dictionary<string, object?>
        {
            ["reason"] = "parent",
        })));

        activity.Stop();

        var resource = ResourceBuilder
            .CreateEmpty()
            .AddAttributes(new Dictionary<string, object>
            {
                ["service.name"] = "agent-lightning-tests",
                ["service.instance.id"] = "test-instance",
            })
            .Build();

        var span = SpanModel.FromActivity(activity, "rollout-1", "attempt-1", 3, resource);

        span.RolloutId.ShouldBe("rollout-1");
        span.AttemptId.ShouldBe("attempt-1");
        span.SequenceId.ShouldBe(3);
        span.TraceId.Length.ShouldBe(32);
        span.SpanId.Length.ShouldBe(16);
        span.Name.ShouldBe("rollout-span");
        span.Status.StatusCode.ShouldBe(ActivityStatusCode.Ok.ToString());
        span.Attributes.ContainsKey("agent.rollout").ShouldBeTrue();
        span.Attributes["agent.rollout"].ShouldBe("demo");
        span.Attributes.ContainsKey("iteration").ShouldBeTrue();
        span.Attributes["iteration"].ShouldBe(7);
        span.Attributes.ContainsKey("score").ShouldBeTrue();
        span.Attributes["score"].ShouldBe(0.87);
        span.Attributes.ContainsKey("labels").ShouldBeTrue();
        span.Attributes["labels"].ShouldBeOfType<string[]>().ShouldBe(new[] { "a", "b" });
        span.StartTime.ShouldNotBeNull();
        span.EndTime.ShouldNotBeNull();
        span.Events.Count.ShouldBe(1);
        span.Links.Count.ShouldBe(1);
        span.Resource.Attributes.ContainsKey("service.name").ShouldBeTrue();
        span.Resource.Attributes["service.name"].ShouldBe("agent-lightning-tests");
        span.Resource.Attributes.ContainsKey("service.instance.id").ShouldBeTrue();
        span.AdditionalData.ContainsKey("ActivityKind").ShouldBeTrue();
        span.AdditionalData["ActivityKind"].ShouldBe(ActivityKind.Client.ToString());
    }

    [Fact]
    public void FromAttributes_GeneratesIdsAndContext()
    {
        var attributes = new Dictionary<string, object?>
        {
            ["reward"] = 1.0,
            ["status"] = "success",
        };

        var span = SpanModel.FromAttributes(
            attributes,
            rolloutId: "rollout-2",
            attemptId: "attempt-9",
            sequenceId: 11,
            parentId: "ffffffffffffffff");

        span.RolloutId.ShouldBe("rollout-2");
        span.AttemptId.ShouldBe("attempt-9");
        span.SequenceId.ShouldBe(11);
        span.TraceId.Length.ShouldBe(32);
        span.SpanId.Length.ShouldBe(16);
        span.ParentId.ShouldBe("ffffffffffffffff");
        span.Context.ShouldNotBeNull();
        span.Parent.ShouldNotBeNull();
        span.Attributes.ContainsKey("reward").ShouldBeTrue();
        span.Attributes["reward"].ShouldBe(1.0);
        span.Attributes.ContainsKey("status").ShouldBeTrue();
        span.Attributes["status"].ShouldBe("success");
        span.Events.ShouldBeEmpty();
        span.Links.ShouldBeEmpty();
        span.Status.StatusCode.ShouldBe("OK");
    }

    [Fact]
    public void FromActivity_WithUnknownAttribute_FallsBackToString()
    {
        using var source = new ActivitySource("ManagedCode.AgentLightning.Tests");
        using var activity = source.StartActivity("unknown-attribute")!;

        activity.SetTag("complex", new { foo = 1, bar = "baz" });
        activity.Stop();

        var span = SpanModel.FromActivity(activity, "rollout-x", "attempt-y", 1);

        span.Attributes.ContainsKey("complex").ShouldBeTrue();
        span.Attributes["complex"].ShouldBe(activity.GetTagItem("complex")!.ToString());
    }
}
