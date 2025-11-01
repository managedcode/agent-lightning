using System.Collections.ObjectModel;
using System.Text.Json;
using ManagedCode.AgentLightning.Core.Models;
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

    internal IReadOnlyList<ChatMessage> BuildPrompt(object input)
    {
        var messages = new List<ChatMessage>();

        if (!string.IsNullOrWhiteSpace(SystemPrompt))
        {
            messages.Add(new ChatMessage(ChatRole.System, SystemPrompt));
        }

        if (Preamble.Count > 0)
        {
            messages.AddRange(Preamble);
        }

        messages.AddRange(TaskMessageFactory(input));
        return new ReadOnlyCollection<ChatMessage>(messages);
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
