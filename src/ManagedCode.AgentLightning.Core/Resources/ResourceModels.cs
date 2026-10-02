using System.Collections.ObjectModel;
using System.Linq;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Serialization;
using ManagedCode.AgentLightning.Core.Models;

namespace ManagedCode.AgentLightning.Core.Resources;

/// <summary>
/// Base class for tunable resources distributed to executors.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "resource_type")]
[JsonDerivedType(typeof(LlmResource), LlmResource.ResourceTypeValue)]
[JsonDerivedType(typeof(ProxyLlmResource), ProxyLlmResource.ResourceTypeValue)]
[JsonDerivedType(typeof(PromptTemplateResource), PromptTemplateResource.ResourceTypeValue)]
public abstract record ResourceDefinition
{
    /// <summary>
    /// Alias of the resource type used during serialization.
    /// </summary>
    [JsonPropertyName("resource_type")]
    public abstract string ResourceType { get; }
}

/// <summary>
/// Resource that identifies an LLM endpoint and its configuration.
/// </summary>
public record LlmResource : ResourceDefinition
{
    public const string ResourceTypeValue = "llm";

    private IReadOnlyDictionary<string, object?> _samplingParameters = CreateEmptyParameters();

    public override string ResourceType => ResourceTypeValue;

    [JsonPropertyName("endpoint")]
    public required string Endpoint { get; init; }

    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyName("api_key")]
    public string? ApiKey { get; init; }

    [JsonPropertyName("sampling_parameters")]
    public IReadOnlyDictionary<string, object?> SamplingParameters
    {
        get => _samplingParameters;
        init => _samplingParameters = value switch
        {
            null => CreateEmptyParameters(),
            ReadOnlyDictionary<string, object?> existing => existing,
            _ => new ReadOnlyDictionary<string, object?>(value.ToDictionary(kvp => kvp.Key, kvp => kvp.Value)),
        };
    }

    /// <summary>
    /// Returns the base URL consumed by OpenAI-compatible clients. Override to customise routing behaviour.
    /// </summary>
    public virtual string GetBaseUrl(string? rolloutId = null, string? attemptId = null)
    {
        if ((rolloutId is null) != (attemptId is null))
        {
            throw new ArgumentException("rolloutId and attemptId must both be provided or both be null.");
        }

        return Endpoint;
    }

    private static ReadOnlyDictionary<string, object?> CreateEmptyParameters() =>
        new(new Dictionary<string, object?>());
}

/// <summary>
/// LLM resource that rewrites endpoints through the rollouts proxy.
/// </summary>
public sealed record ProxyLlmResource : LlmResource
{
    public new const string ResourceTypeValue = "proxy_llm";

    private bool _initialised;

    public override string ResourceType => ResourceTypeValue;

    public ProxyLlmResource()
    {
        _initialised = true;
    }

    /// <summary>
    /// Bake rollout metadata into a concrete <see cref="LlmResource"/>.
    /// </summary>
    public LlmResource WithAttemptedRollout(AttemptedRollout rollout) =>
        new LlmResource
        {
            Endpoint = GetBaseUrl(rollout.Rollout.RolloutId, rollout.Attempt.AttemptId),
            Model = Model,
            ApiKey = ApiKey,
            SamplingParameters = SamplingParameters,
        };

    public override string GetBaseUrl(string? rolloutId = null, string? attemptId = null)
    {
        if (rolloutId is null && attemptId is null)
        {
            WarnIfEndpointAccessedDirectly();
            return Endpoint;
        }

        if (rolloutId is null || attemptId is null)
        {
            throw new ArgumentException("rolloutId and attemptId must both be provided to generate a routed endpoint.");
        }

        var prefix = Endpoint;
        if (prefix.EndsWith("/", StringComparison.Ordinal))
        {
            prefix = prefix[..^1];
        }

        var hasV1 = false;
        if (prefix.EndsWith("/v1", StringComparison.Ordinal))
        {
            hasV1 = true;
            prefix = prefix[..^3];
        }

        prefix = $"{prefix}/rollout/{rolloutId}/attempt/{attemptId}";
        if (hasV1)
        {
            prefix += "/v1";
        }

        return prefix;
    }

    private void WarnIfEndpointAccessedDirectly()
    {
        if (!_initialised)
        {
            return;
        }

        var stackFrame = new StackFrame(2);
        var method = stackFrame.GetMethod();
        if (method is not null && method.Name is not nameof(GetBaseUrl))
        {
            Trace.TraceWarning(
                "Accessing 'Endpoint' directly on ProxyLlmResource is discouraged. " +
                "Use 'GetBaseUrl(rolloutId, attemptId)' instead to get the routed endpoint.");
        }
    }
}

/// <summary>
/// Supported prompt template engines.
/// </summary>
public enum PromptTemplateEngine
{
    FString,
    Jinja,
    Poml,
}

/// <summary>
/// Resource describing a reusable prompt template.
/// </summary>
public sealed record PromptTemplateResource : ResourceDefinition
{
    public const string ResourceTypeValue = "prompt_template";

    public override string ResourceType => ResourceTypeValue;

    [JsonPropertyName("template")]
    public required string Template { get; init; }

    [JsonPropertyName("engine")]
    public required PromptTemplateEngine Engine { get; init; }

    /// <summary>
    /// Render the prompt using the supplied variables. Only <see cref="PromptTemplateEngine.FString"/> is currently supported.
    /// </summary>
    public string Render(IReadOnlyDictionary<string, object?> variables)
    {
        if (Engine != PromptTemplateEngine.FString)
        {
            throw new NotSupportedException($"The '{Engine}' prompt template engine is not supported; only FString templates can be rendered.");
        }

        ArgumentNullException.ThrowIfNull(variables);
        var output = new System.Text.StringBuilder(Template.Length);
        for (var index = 0; index < Template.Length;)
        {
            var character = Template[index];
            if (character == '{')
            {
                if (index + 1 < Template.Length && Template[index + 1] == '{')
                {
                    output.Append('{');
                    index += 2;
                    continue;
                }

                var closingBrace = Template.IndexOf('}', index + 1);
                if (closingBrace < 0)
                {
                    throw new FormatException("The prompt template contains an unmatched opening brace.");
                }

                var key = Template[(index + 1)..closingBrace];
                if (!IsSimpleFieldName(key))
                {
                    throw new FormatException($"Prompt field '{key}' is malformed or uses unsupported formatting syntax.");
                }

                if (!variables.TryGetValue(key, out var value))
                {
                    throw new KeyNotFoundException($"Prompt field '{key}' has no supplied value.");
                }

                output.Append(FormatValue(value));
                index = closingBrace + 1;
                continue;
            }

            if (character == '}')
            {
                if (index + 1 < Template.Length && Template[index + 1] == '}')
                {
                    output.Append('}');
                    index += 2;
                    continue;
                }

                throw new FormatException("The prompt template contains an unmatched closing brace.");
            }

            output.Append(character);
            index++;
        }

        return output.ToString();
    }

    private static bool IsSimpleFieldName(string key) =>
        key.Length > 0 && key.All(character => char.IsLetterOrDigit(character) || character == '_');

    private static string FormatValue(object? value) => value switch
    {
        null => string.Empty,
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
        _ => value.ToString() ?? string.Empty,
    };
}

/// <summary>
/// Mapping from resource names to their configured instances.
/// </summary>
public sealed class NamedResources : ReadOnlyDictionary<string, ResourceDefinition>
{
    public NamedResources()
        : base(new Dictionary<string, ResourceDefinition>(StringComparer.Ordinal))
    {
    }

    public NamedResources(IDictionary<string, ResourceDefinition> dictionary)
        : base(new Dictionary<string, ResourceDefinition>(dictionary, StringComparer.Ordinal))
    {
    }
}

/// <summary>
/// Update payload broadcast to clients when resources change.
/// </summary>
public sealed record ResourcesUpdate(
    string ResourcesId,
    NamedResources Resources);
