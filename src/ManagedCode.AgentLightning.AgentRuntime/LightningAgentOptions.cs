using System.Collections.ObjectModel;
using System.Text.Json;
using ManagedCode.AgentLightning.Core.Models;
using ManagedCode.AgentLightning.Core.Resources;
using Microsoft.Extensions.AI;

namespace ManagedCode.AgentLightning.AgentRuntime;

/// <summary>
/// Options that configure the <see cref="LightningAgent"/>.
/// </summary>
public sealed class LightningAgentOptions
{
    private Func<object, IEnumerable<ChatMessage>> _taskMessageFactory = DefaultTaskMessageFactory;
    private Func<ChatResponse, double?> _rewardEvaluator = _ => null;

    public string AgentName { get; set; } = "agent";

    public string? SystemPrompt { get; set; }

    public string PromptResourceName { get; set; } = "prompt";

    public IList<ChatMessage> Preamble { get; } = new List<ChatMessage>();

    public ChatOptions ChatOptions { get; } = new();

    public RolloutConfig RolloutConfig { get; set; } = new();

    public Func<object, IEnumerable<ChatMessage>> TaskMessageFactory
    {
        get => _taskMessageFactory;
        set => _taskMessageFactory = value ?? throw new ArgumentNullException(nameof(value));
    }

    public Func<ChatResponse, double?> RewardEvaluator
    {
        get => _rewardEvaluator;
        set => _rewardEvaluator = value ?? throw new ArgumentNullException(nameof(value));
    }

    internal IReadOnlyList<ChatMessage> BuildPrompt(object input, NamedResources? resources = null)
    {
        var messages = new List<ChatMessage>();
        var systemPrompt = SystemPrompt;

        if (resources is not null && resources.TryGetValue(PromptResourceName, out var resource))
        {
            if (resource is not PromptTemplateResource promptTemplate)
            {
                throw new InvalidOperationException($"Resource '{PromptResourceName}' must be a prompt template.");
            }

            systemPrompt = promptTemplate.Render(GetTemplateVariables(input));
        }

        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            messages.Add(new ChatMessage(ChatRole.System, systemPrompt));
        }

        if (Preamble.Count > 0)
        {
            messages.AddRange(Preamble);
        }

        messages.AddRange(TaskMessageFactory(input));
        return new ReadOnlyCollection<ChatMessage>(messages);
    }

    private static IReadOnlyDictionary<string, object?> GetTemplateVariables(object input)
    {
        if (input is IReadOnlyDictionary<string, object?> values)
        {
            return values;
        }

        return new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>
        {
            ["input"] = input,
            ["task"] = input,
        });
    }

    private static IEnumerable<ChatMessage> DefaultTaskMessageFactory(object input)
    {
        switch (input)
        {
            case ChatMessage message:
                yield return message;
                break;

            case IEnumerable<ChatMessage> messages:
                foreach (var message in messages)
                {
                    yield return message;
                }

                break;

            case string text:
                yield return new ChatMessage(ChatRole.User, text);
                break;

            case IReadOnlyDictionary<string, object?> dict:
                yield return new ChatMessage(ChatRole.User, JsonSerializer.Serialize(dict));
                break;

            default:
                yield return new ChatMessage(ChatRole.User, JsonSerializer.Serialize(input));
                break;
        }
    }
}
