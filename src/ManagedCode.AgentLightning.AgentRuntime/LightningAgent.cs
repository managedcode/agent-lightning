using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using ManagedCode.AgentLightning.Core.Models;
using ManagedCode.AgentLightning.Core.Resources;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace ManagedCode.AgentLightning.AgentRuntime;

/// <summary>
/// Executes rollouts by delegating to <see cref="IChatClient"/>.
/// </summary>
public sealed class LightningAgent : LitAgentBase<object>, IDisposable
{
    private readonly IChatClient _chatClient;
    private readonly ILogger<LightningAgent> _logger;
    private readonly LightningAgentOptions _options;
    private readonly IReadOnlyList<Hook> _hooks;
    private readonly TimeProvider _timeProvider;
    private bool _disposed;

    public LightningAgent(
        IChatClient chatClient,
        Microsoft.Extensions.Options.IOptions<LightningAgentOptions> options,
        IEnumerable<Hook>? hooks,
        ILogger<LightningAgent> logger,
        TimeProvider? timeProvider = null)
        : this(chatClient, options?.Value ?? throw new ArgumentNullException(nameof(options)), hooks, logger, timeProvider)
    {
    }

    public LightningAgent(
        IChatClient chatClient,
        LightningAgentOptions options,
        IEnumerable<Hook>? hooks,
        ILogger<LightningAgent> logger,
        TimeProvider? timeProvider = null)
    {
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _hooks = hooks?.ToArray() ?? Array.Empty<Hook>();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    protected override async Task<LightningExecutionResult> RolloutAsync(
        object taskInput,
        NamedResources? resources,
        RolloutMode? mode,
        string? resourcesId,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        var startTimestamp = _timeProvider.GetUtcNow();
        var rolloutId = GenerateRolloutId(taskInput);
        var rollout = new Rollout(
            rolloutId,
            taskInput,
            startTimestamp,
            config: _options.RolloutConfig,
            mode: mode,
            resourcesId: resourcesId);

        var attempt = new Attempt(
            rolloutId,
            $"{rolloutId}:attempt:1",
            sequenceId: 1,
            startTimestamp);

        if (!string.IsNullOrEmpty(resourcesId))
        {
            var id = resourcesId!;
            rollout.AddMetadata("resources.id", id);
            attempt.AddMetadata("resources.id", id);
        }

        if (resources is { Count: > 0 })
        {
            var resourceNames = resources.Keys.ToArray();
            rollout.AddMetadata("resources.names", resourceNames);
            attempt.AddMetadata("resources.names", resourceNames);
        }

        if (mode is { } rolloutMode)
        {
            rollout.AddMetadata("mode", rolloutMode.ToString());
        }

        var context = new LightningContext(this, this, rollout);
        await InvokeHooksAsync(h => h.OnTraceStartAsync(context, cancellationToken), cancellationToken).ConfigureAwait(false);

        attempt.UpdateStatus(AttemptStatus.Running);
        attempt.Touch(startTimestamp);
        rollout.TransitionTo(RolloutStatus.Preparing);

        await InvokeHooksAsync(h => h.OnRolloutStartAsync(context, cancellationToken), cancellationToken).ConfigureAwait(false);
        rollout.TransitionTo(RolloutStatus.Running);

        IReadOnlyList<ChatMessage> prompt = _options.BuildPrompt(taskInput);

        try
        {
            var response = await _chatClient
                .GetResponseAsync(prompt, _options.ChatOptions, cancellationToken)
                .ConfigureAwait(false);

            attempt.UpdateStatus(AttemptStatus.Succeeded, _timeProvider.GetUtcNow());

            rollout.AddMetadata("modelId", response.ModelId ?? string.Empty);
            if (!string.IsNullOrEmpty(response.ResponseId))
            {
                rollout.AddMetadata("responseId", response.ResponseId);
            }

            var reward = _options.RewardEvaluator(response);
            var triplet = BuildTriplet(prompt, response, reward);
            rollout.Complete(_timeProvider.GetUtcNow());

            await InvokeHooksAsync(
                h => h.OnRolloutEndAsync(context, new ReadOnlyCollection<object>(new object[] { response }), cancellationToken),
                cancellationToken).ConfigureAwait(false);

            await InvokeHooksAsync(h => h.OnTraceEndAsync(context, cancellationToken), cancellationToken).ConfigureAwait(false);

            return new LightningExecutionResult(rollout, attempt, response, triplet);
        }
        catch (OperationCanceledException)
        {
            attempt.UpdateStatus(AttemptStatus.Timeout, _timeProvider.GetUtcNow());
            rollout.TransitionTo(RolloutStatus.Cancelled);
            await InvokeHooksAsync(h => h.OnTraceEndAsync(context, cancellationToken), cancellationToken).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Agent {AgentName} failed to execute rollout {RolloutId}.", _options.AgentName, rolloutId);
            attempt.UpdateStatus(AttemptStatus.Failed, _timeProvider.GetUtcNow());
            rollout.TransitionTo(RolloutStatus.Failed);

            await InvokeHooksAsync(
                h => h.OnRolloutEndAsync(context, Array.Empty<object>(), cancellationToken),
                cancellationToken).ConfigureAwait(false);
            await InvokeHooksAsync(h => h.OnTraceEndAsync(context, cancellationToken), cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _chatClient.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private static Triplet BuildTriplet(IEnumerable<ChatMessage> prompt, ChatResponse response, double? reward)
    {
        var promptSnapshot = prompt
            .Select(
                message => new
                {
                    message.Role,
                    Text = message.Text,
                    message.AuthorName,
                })
            .ToArray();

        var metadata = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["conversationId"] = response.ConversationId,
            ["finishReason"] = response.FinishReason?.ToString(),
            ["responseId"] = response.ResponseId,
        };

        if (response.CreatedAt is { } createdAt)
        {
            metadata["createdAt"] = createdAt;
        }

        if (response.ModelId is { Length: > 0 } modelId)
        {
            metadata["modelId"] = modelId;
        }

        return new Triplet
        {
            Prompt = promptSnapshot,
            Response = response.Text,
            Reward = reward,
            Metadata = new ReadOnlyDictionary<string, object?>(metadata),
        };
    }

    private async Task InvokeHooksAsync(Func<Hook, ValueTask> callback, CancellationToken cancellationToken)
    {
        foreach (var hook in _hooks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await callback(hook).ConfigureAwait(false);
        }
    }

    private string GenerateRolloutId(object taskInput)
    {
        static string Sanitize(string value)
        {
            var cleaned = new string(value.Where(char.IsLetterOrDigit).Take(12).ToArray());
            return string.IsNullOrEmpty(cleaned) ? "input" : cleaned;
        }

        var suffix = taskInput switch
        {
            string text => Sanitize(text),
            _ => Math.Abs(taskInput.GetHashCode()).ToString("X"),
        };

        return $"{_options.AgentName}-{Guid.NewGuid():N}-{suffix}";
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(LightningAgent));
        }
    }
}
