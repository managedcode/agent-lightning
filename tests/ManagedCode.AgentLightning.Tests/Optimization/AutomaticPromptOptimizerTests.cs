using ManagedCode.AgentLightning.AgentRuntime;
using ManagedCode.AgentLightning.AgentRuntime.Optimization;
using ManagedCode.AgentLightning.AgentRuntime.Trainer;
using ManagedCode.AgentLightning.Core.Models;
using ManagedCode.AgentLightning.Core.Resources;
using ManagedCode.AgentLightning.Core.Stores;
using ManagedCode.AgentLightning.Tests.TestHelpers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.Logging;
using Shouldly;
using TrainerRuntime = ManagedCode.AgentLightning.AgentRuntime.Trainer.Trainer;

namespace ManagedCode.AgentLightning.Tests.Optimization;

public sealed class AutomaticPromptOptimizerTests
{
    [Theory]
    [InlineData(false, 0d)]
    [InlineData(true, 0d)]
    public async Task OptimizeAsync_AcceptsMeasuredZeroAndFalse(bool booleanMetric, double expectedScore)
    {
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(new NullLoggerProvider()));
        using var agentClient = new EchoChatClient(loggerFactory.CreateLogger<EchoChatClient>());
        using var evaluationClient = new EchoChatClient(loggerFactory.CreateLogger<EchoChatClient>());
        using var agent = new LightningAgent(agentClient, new LightningAgentOptions(), hooks: null, loggerFactory.CreateLogger<LightningAgent>());
        var trainer = new TrainerRuntime(new InMemoryLightningStore(), agent, loggerFactory);
        var evaluator = new FixedEvaluator(booleanMetric
            ? new BooleanMetric("quality", false)
            : new NumericMetric("quality", 0d));
        var optimizer = CreateOptimizer(trainer, evaluationClient, evaluator, loggerFactory);

        var result = await optimizer.OptimizeAsync(
            new PromptTemplateResource { Template = "Answer {input}", Engine = PromptTemplateEngine.FString },
            ["train"],
            ["validation"]);

        result.BestPrompt.Score.ShouldBe(expectedScore);
        result.History.ShouldContain(candidate => candidate.Score == expectedScore);
    }

    [Fact]
    public async Task OptimizeAsync_RejectsEvaluatorInfrastructureFailureEvenWithCustomScoreSelector()
    {
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(new NullLoggerProvider()));
        using var agentClient = new EchoChatClient(loggerFactory.CreateLogger<EchoChatClient>());
        using var evaluationClient = new EchoChatClient(loggerFactory.CreateLogger<EchoChatClient>());
        using var agent = new LightningAgent(agentClient, new LightningAgentOptions(), hooks: null, loggerFactory.CreateLogger<LightningAgent>());
        var trainer = new TrainerRuntime(new InMemoryLightningStore(), agent, loggerFactory);
        var metric = new NumericMetric("quality", 0d)
        {
            Diagnostics = [new EvaluationDiagnostic(EvaluationDiagnosticSeverity.Error, "Evaluator service failed.")],
        };
        var optimizer = CreateOptimizer(
            trainer,
            evaluationClient,
            new FixedEvaluator(metric),
            loggerFactory,
            new AutomaticPromptOptimizerOptions { BeamRounds = 0, ScoreSelector = _ => 0d });

        var exception = await Should.ThrowAsync<InvalidOperationException>(() => optimizer.OptimizeAsync(
            new PromptTemplateResource { Template = "Answer {input}", Engine = PromptTemplateEngine.FString },
            ["train"],
            ["validation"]));

        exception.Message.ShouldContain("infrastructure error");
    }

    [Fact]
    public async Task OptimizeAsync_RequiresNonEmptyValidationDataset()
    {
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(new NullLoggerProvider()));
        using var agentClient = new EchoChatClient(loggerFactory.CreateLogger<EchoChatClient>());
        using var evaluationClient = new EchoChatClient(loggerFactory.CreateLogger<EchoChatClient>());
        using var agent = new LightningAgent(agentClient, new LightningAgentOptions(), hooks: null, loggerFactory.CreateLogger<LightningAgent>());
        var trainer = new TrainerRuntime(new InMemoryLightningStore(), agent, loggerFactory);
        var optimizer = CreateOptimizer(trainer, evaluationClient, new FixedEvaluator(new NumericMetric("quality", 1d)), loggerFactory);

        await Should.ThrowAsync<ArgumentException>(() => optimizer.OptimizeAsync(
            new PromptTemplateResource { Template = "Answer {input}", Engine = PromptTemplateEngine.FString },
            ["train"],
            []));
    }

    [Fact]
    public async Task OptimizeAsync_ValidatesCandidatesAndSelectsNamedMinimizationObjectiveAcrossRepeatCalls()
    {
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(new NullLoggerProvider()));
        using var agentClient = new EchoChatClient(
            loggerFactory.CreateLogger<EchoChatClient>(),
            messages =>
            {
                var systemPrompt = messages.First(message => message.Role == ChatRole.System).Text;
                return systemPrompt.StartsWith("Candidate low cost", StringComparison.Ordinal)
                    ? "low-cost response"
                    : systemPrompt.StartsWith("Candidate high cost", StringComparison.Ordinal)
                        ? "high-cost response"
                        : "seed response";
            });
        using var evaluationClient = new EchoChatClient(loggerFactory.CreateLogger<EchoChatClient>());
        var optimizationCall = 0;
        using var optimizationClient = new EchoChatClient(
            loggerFactory.CreateLogger<EchoChatClient>(),
            _ => (Interlocked.Increment(ref optimizationCall) % 4) switch
            {
                1 => "Add one clear instruction.",
                2 => "Candidate low cost for {input}",
                3 => "Candidate for {input} plus unresolved {unknown}",
                _ => "Candidate high cost for {input}",
            });
        var store = new InMemoryLightningStore();
        using var agent = new LightningAgent(
            agentClient,
            new LightningAgentOptions { ChatOptions = { ModelId = "caller-selected-deployment" } },
            hooks: null,
            loggerFactory.CreateLogger<LightningAgent>());
        var trainer = new TrainerRuntime(store, agent, loggerFactory);
        var optimizer = CreateOptimizer(
            trainer,
            evaluationClient,
            new ResponseEvaluator(response => new NumericMetric(
                "cost",
                response.Text switch
                {
                    "low-cost response" => 0.1d,
                    "high-cost response" => 0.9d,
                    _ => 0.5d,
                })),
            loggerFactory,
            new AutomaticPromptOptimizerOptions
            {
                BeamRounds = 1,
                GradientBatchSize = 1,
                ValidationBatchSize = 1,
                BranchFactor = 3,
                ObjectiveMetricName = "cost",
                ObjectiveDirection = EvaluationScoreDirection.Minimize,
            },
            optimizationClient);

        var seed = new PromptTemplateResource
        {
            Template = "Seed {{literal}} instruction for {input}",
            Engine = PromptTemplateEngine.FString,
        };
        var firstResult = await optimizer.OptimizeAsync(seed, ["training"], ["validation"]);
        var secondResult = await optimizer.OptimizeAsync(seed, ["training"], ["validation"]);

        firstResult.History.Select(candidate => candidate.Version).ShouldBe(["v0", "v1", "v2"]);
        firstResult.History.Select(candidate => candidate.Score).ShouldBe([-0.5d, -0.1d, -0.9d]);
        firstResult.BestPrompt.Version.ShouldBe("v1");
        firstResult.BestPrompt.PromptTemplate.Template.ShouldBe("Candidate low cost for {input}");
        secondResult.History.Select(candidate => candidate.Version).ShouldBe(["v0", "v1", "v2"]);
        secondResult.BestPrompt.Version.ShouldBe("v1");

        agentClient.RequestOptions.All(options => options?.ModelId == "caller-selected-deployment").ShouldBeTrue();
        optimizationClient.RequestOptions.All(options => options?.ModelId is null).ShouldBeTrue();
        var rollouts = await store.QueryRolloutsAsync();
        rollouts.Count.ShouldBe(8);
        foreach (var rollout in rollouts)
        {
            var spans = await store.GetSpansAsync(rollout.RolloutId);
            spans.Any(span =>
                span.RolloutId == rollout.RolloutId &&
                span.Name == "agentlightning.reward" &&
                span.Attributes.TryGetValue("gen_ai.reward.value", out var value) &&
                value is double).ShouldBeTrue();
        }
    }

    [Fact]
    public async Task OptimizeAsync_RejectsMissingConfiguredObjectiveMetric()
    {
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(new NullLoggerProvider()));
        using var agentClient = new EchoChatClient(loggerFactory.CreateLogger<EchoChatClient>());
        using var evaluationClient = new EchoChatClient(loggerFactory.CreateLogger<EchoChatClient>());
        using var agent = new LightningAgent(agentClient, new LightningAgentOptions(), hooks: null, loggerFactory.CreateLogger<LightningAgent>());
        var trainer = new TrainerRuntime(new InMemoryLightningStore(), agent, loggerFactory);
        var optimizer = CreateOptimizer(
            trainer,
            evaluationClient,
            new FixedEvaluator(new NumericMetric("other", 0d)),
            loggerFactory);

        var exception = await Should.ThrowAsync<InvalidOperationException>(() => optimizer.OptimizeAsync(
            new PromptTemplateResource { Template = "Answer {input}", Engine = PromptTemplateEngine.FString },
            ["train"],
            ["validation"]));

        exception.Message.ShouldContain("configured objective metric");
    }

    [Fact]
    public async Task OptimizeAsync_RejectsConcurrentCallsOnOneOptimizerInstance()
    {
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(new NullLoggerProvider()));
        using var agentClient = new EchoChatClient(loggerFactory.CreateLogger<EchoChatClient>());
        using var evaluationClient = new EchoChatClient(loggerFactory.CreateLogger<EchoChatClient>());
        using var agent = new LightningAgent(agentClient, new LightningAgentOptions(), hooks: null, loggerFactory.CreateLogger<LightningAgent>());
        var evaluator = new BlockingEvaluator();
        var optimizer = CreateOptimizer(new TrainerRuntime(new InMemoryLightningStore(), agent, loggerFactory), evaluationClient, evaluator, loggerFactory);
        var seed = new PromptTemplateResource { Template = "Answer {input}", Engine = PromptTemplateEngine.FString };
        var firstCall = optimizer.OptimizeAsync(seed, ["train"], ["validation"]);

        try
        {
            await evaluator.Started.WaitAsync(TimeSpan.FromSeconds(10));
            var exception = await Should.ThrowAsync<InvalidOperationException>(() => optimizer.OptimizeAsync(seed, ["train"], ["validation"]));
            exception.Message.ShouldContain("already optimizing");
        }
        finally
        {
            evaluator.Release();
        }

        var result = await firstCall;
        result.BestPrompt.Version.ShouldBe("v0");
    }

    private static AutomaticPromptOptimizer CreateOptimizer(
        TrainerRuntime trainer,
        IChatClient evaluationClient,
        IEvaluator evaluator,
        ILoggerFactory loggerFactory,
        AutomaticPromptOptimizerOptions? options = null,
        IChatClient? optimizationClient = null) =>
        new(
            trainer,
            optimizationClient ?? new EchoChatClient(loggerFactory.CreateLogger<EchoChatClient>()),
            evaluator,
            evaluationClient,
            options ?? new AutomaticPromptOptimizerOptions
            {
                BeamRounds = 0,
                ObjectiveMetricName = "quality",
                ObjectiveDirection = EvaluationScoreDirection.Maximize,
            },
            loggerFactory.CreateLogger<AutomaticPromptOptimizer>());

    private sealed class FixedEvaluator(EvaluationMetric metric) : IEvaluator
    {
        public IReadOnlyCollection<string> EvaluationMetricNames => [metric.Name];

        public ValueTask<EvaluationResult> EvaluateAsync(
            IEnumerable<ChatMessage> messages,
            ChatResponse modelResponse,
            ChatConfiguration? chatConfiguration = null,
            IEnumerable<EvaluationContext>? additionalContext = null,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new EvaluationResult(metric));
    }

    private sealed class ResponseEvaluator(Func<ChatResponse, EvaluationMetric> metricFactory) : IEvaluator
    {
        public IReadOnlyCollection<string> EvaluationMetricNames => ["cost"];

        public ValueTask<EvaluationResult> EvaluateAsync(
            IEnumerable<ChatMessage> messages,
            ChatResponse modelResponse,
            ChatConfiguration? chatConfiguration = null,
            IEnumerable<EvaluationContext>? additionalContext = null,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new EvaluationResult(metricFactory(modelResponse)));
    }

    private sealed class BlockingEvaluator : IEvaluator
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public IReadOnlyCollection<string> EvaluationMetricNames => ["quality"];

        public void Release() => _release.TrySetResult();

        public async ValueTask<EvaluationResult> EvaluateAsync(
            IEnumerable<ChatMessage> messages,
            ChatResponse modelResponse,
            ChatConfiguration? chatConfiguration = null,
            IEnumerable<EvaluationContext>? additionalContext = null,
            CancellationToken cancellationToken = default)
        {
            _started.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new EvaluationResult(new NumericMetric("quality", 1d));
        }
    }
}
