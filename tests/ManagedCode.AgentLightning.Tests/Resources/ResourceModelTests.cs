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
    public void PromptTemplateResource_Render_LeavesInsertedPlaceholderTextLiteral()
    {
        var template = new PromptTemplateResource
        {
            Template = "Value: {value}",
            Engine = PromptTemplateEngine.FString,
        };

        template.Render(new Dictionary<string, object?>
        {
            ["value"] = "{missing}",
        }).ShouldBe("Value: {missing}");
    }

    [Fact]
    public void PromptTemplateResource_Render_HandlesEscapedBracesAndNestedField()
    {
        var template = new PromptTemplateResource
        {
            Template = "{{literal}} {{{value}}}",
            Engine = PromptTemplateEngine.FString,
        };

        template.Render(new Dictionary<string, object?>
        {
            ["value"] = "{missing}",
        }).ShouldBe("{literal} {{missing}}");
    }

    [Fact]
    public void PromptTemplateResource_Render_ThrowsForMissingValuesEvenWhenVariablesAreEmpty()
    {
        var template = new PromptTemplateResource
        {
            Template = "Value: {missing}",
            Engine = PromptTemplateEngine.FString,
        };

        Should.Throw<KeyNotFoundException>(() => template.Render(new Dictionary<string, object?>()));
    }

    [Theory]
    [InlineData("Missing {field")]
    [InlineData("Unexpected }")]
    [InlineData("Unsupported {field:format}")]
    public void PromptTemplateResource_Render_RejectsMalformedOrUnsupportedFields(string source)
    {
        var template = new PromptTemplateResource { Template = source, Engine = PromptTemplateEngine.FString };

        Should.Throw<FormatException>(() => template.Render(new Dictionary<string, object?> { ["field"] = "value" }));
    }

    [Fact]
    public void PromptTemplateResource_Render_ThrowsForUnsupportedEngine()
    {
        var template = new PromptTemplateResource
        {
            Template = "unused",
            Engine = PromptTemplateEngine.Jinja,
        };

        Should.Throw<NotSupportedException>(() => template.Render(new Dictionary<string, object?>()));
    }
}
