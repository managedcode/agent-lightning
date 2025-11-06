using System.Diagnostics;
using ManagedCode.AgentLightning.Core.Adapters;
using Shouldly;
using Xunit;

namespace ManagedCode.AgentLightning.Tests.Adapters;

public class ActivityToSpanModelAdapterTests
{
    [Fact]
    public void Adapt_ShouldConvertActivitiesToSpanModels()
    {
        using var activity = CreateActivity("operation");
        activity.SetTag("agentlightning.rollout_id", "rollout-123");
        activity.SetTag("agentlightning.attempt_id", "attempt-456");
        activity.SetTag("agentlightning.sequence_id", 7);
        activity.Start();
        activity.Stop();

        var adapter = new ActivityToSpanModelAdapter();
        var result = adapter.Adapt(new[] { activity });

        result.Count.ShouldBe(1);
        var span = result[0];
        span.RolloutId.ShouldBe("rollout-123");
        span.AttemptId.ShouldBe("attempt-456");
        span.SequenceId.ShouldBe(7);
        span.Name.ShouldBe("operation");
    }

    [Fact]
    public void Adapt_ShouldUseDefaultsWhenTagsMissing()
    {
        using var activity = CreateActivity("operation-default");
        activity.Start();
        activity.Stop();

        var adapter = new ActivityToSpanModelAdapter(defaultRolloutId: "default-rollout", defaultAttemptId: "default-attempt");
        var result = adapter.Adapt(new[] { activity });

        result.Count.ShouldBe(1);
        var span = result[0];
        span.RolloutId.ShouldBe("default-rollout");
        span.AttemptId.ShouldBe("default-attempt");
        span.SequenceId.ShouldBe(1);
    }

    private static Activity CreateActivity(string name)
    {
        var activity = new Activity(name);
        activity.SetIdFormat(ActivityIdFormat.W3C);
        return activity;
    }
}
