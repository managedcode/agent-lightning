using System.Collections.Generic;
using ManagedCode.AgentLightning.Core.Models;
using ManagedCode.AgentLightning.Core.Resources;
using Shouldly;
using Xunit;

namespace ManagedCode.AgentLightning.Tests.Resources;

public class ResourceModelTests
{
    [Fact]
    public void ProxyLlmResource_GeneratesRoutedEndpoint()
    {
        var proxy = new ProxyLlmResource
        {
            Endpoint = "http://localhost:5000/v1",
            Model = "gpt-4o",
            SamplingParameters = new Dictionary<string, object?> { ["temperature"] = 0.5 },
        };

        var url = proxy.GetBaseUrl("rollout-123", "attempt-456");

        url.ShouldBe("http://localhost:5000/rollout/rollout-123/attempt/attempt-456/v1");
    }

    [Fact]
    public void ProxyLlmResource_WithAttemptedRollout_CreatesConcreteLlm()
    {
        var proxy = new ProxyLlmResource
        {
            Endpoint = "https://proxy.local",
            Model = "llama3",
            SamplingParameters = new Dictionary<string, object?>(),
        };

        var rollout = new Rollout("rollout-1", "input", DateTimeOffset.UtcNow);
        var attempt = new Attempt("rollout-1", "attempt-1", 1, DateTimeOffset.UtcNow);
        var attemptedRollout = new AttemptedRollout(rollout, attempt);

        var llm = proxy.WithAttemptedRollout(attemptedRollout);

        llm.Endpoint.ShouldBe("https://proxy.local/rollout/rollout-1/attempt/attempt-1");
        llm.Model.ShouldBe("llama3");
    }

    [Fact]
    public void ProxyLlmResource_GetBaseUrl_ThrowsWhenIdsIncomplete()
    {
        var proxy = new ProxyLlmResource
        {
            Endpoint = "https://proxy.local",
            Model = "llama3",
        };

        Should.Throw<ArgumentException>(() => proxy.GetBaseUrl("rollout-1", null));
    }

    [Fact]
    public void PromptTemplateResource_Render_ReplacesTokens()
    {
        var template = new PromptTemplateResource
        {
            Template = "Hello {name}, score: {score}",
            Engine = PromptTemplateEngine.FString,
        };

        var rendered = template.Render(new Dictionary<string, object?>
        {
            ["name"] = "Agent",
            ["score"] = 0.98,
        });

        rendered.ShouldBe("Hello Agent, score: 0.98");
    }

    [Fact]
    public void PromptTemplateResource_Render_ThrowsForUnsupportedEngine()
    {
        var template = new PromptTemplateResource
        {
            Template = "unused",
            Engine = PromptTemplateEngine.Jinja,
        };

        Should.Throw<NotImplementedException>(() => template.Render(new Dictionary<string, object?>()));
    }
}
