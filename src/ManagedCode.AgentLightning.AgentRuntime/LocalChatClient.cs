using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace ManagedCode.AgentLightning.AgentRuntime;

/// <summary>
/// Simple <see cref="IChatClient"/> implementation that responds locally without contacting a remote provider.
/// Useful for smoke tests and CLI experimentation.
/// </summary>
public sealed class LocalChatClient : IChatClient
{
    private readonly ILogger<LocalChatClient> _logger;
    private bool _disposed;

    public LocalChatClient(ILogger<LocalChatClient> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (messages is null)
        {
            throw new ArgumentNullException(nameof(messages));
        }

        var reply = GenerateReply(messages);
        var responseMessage = new ChatMessage(ChatRole.Assistant, reply)
        {
            CreatedAt = DateTimeOffset.UtcNow,
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["provider"] = nameof(LocalChatClient),
                ["temperature"] = options?.Temperature,
            },
        };

        return Task.FromResult(new ChatResponse(responseMessage)
        {
            CreatedAt = responseMessage.CreatedAt,
            ModelId = nameof(LocalChatClient),
            FinishReason = ChatFinishReason.Stop,
        });
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        foreach (var update in response.ToChatResponseUpdates())
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(IChatClient) ? this : null;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private static string GenerateReply(IEnumerable<ChatMessage> messages)
    {
        var relevant = messages.LastOrDefault(m => m.Role == ChatRole.User) ??
            messages.LastOrDefault();

        if (relevant is null)
        {
            return "I did not receive any input to process.";
        }

        var builder = new StringBuilder();
        var text = relevant.Text.Trim();

        if (string.IsNullOrEmpty(text))
        {
            return "I received your message, but it was empty.";
        }

        builder.Append("You said: ").Append(text);

        if (text.Contains("?", StringComparison.Ordinal))
        {
            builder.AppendLine()
                .Append("I'm a local reasoning agent and cannot access external knowledge, ")
                .Append("but you can run this task through a production provider by wiring an IChatClient implementation.");
        }

        return builder.ToString();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(LocalChatClient));
        }
    }
}
