using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace ManagedCode.AgentLightning.Tests.TestHelpers;

internal sealed class FlakyChatClient : IChatClient
{
    private readonly ILogger _logger;
    private readonly int _failuresBeforeSuccess;
    private readonly string _responseText;
    private int _callCount;
    private bool _disposed;

    public FlakyChatClient(ILoggerFactory loggerFactory, int failuresBeforeSuccess = 1, string responseText = "ok")
    {
        _logger = (loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory))).CreateLogger<FlakyChatClient>();
        if (failuresBeforeSuccess < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(failuresBeforeSuccess));
        }

        _failuresBeforeSuccess = failuresBeforeSuccess;
        _responseText = responseText;
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        var current = Interlocked.Increment(ref _callCount);
        if (current <= _failuresBeforeSuccess)
        {
            _logger.LogInformation("FlakyChatClient simulating failure {Call}.", current);
            throw new InvalidOperationException("Simulated chat failure.");
        }

        var reply = new ChatMessage(ChatRole.Assistant, _responseText)
        {
            CreatedAt = DateTimeOffset.UtcNow,
        };

        return Task.FromResult(new ChatResponse(reply)
        {
            CreatedAt = reply.CreatedAt,
            FinishReason = ChatFinishReason.Stop,
            ModelId = nameof(FlakyChatClient),
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

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(FlakyChatClient));
        }
    }
}
