using System.Text.Json;
using ManagedCode.AgentLightning.AgentRuntime.Trainer;
using ManagedCode.AgentLightning.Core.Models;
using ManagedCode.AgentLightning.Core.Resources;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.Logging;

namespace ManagedCode.AgentLightning.AgentRuntime.Optimization;

/// <summary>Controls the embedded textual-gradient and beam-search APO algorithm.</summary>
public sealed record AutomaticPromptOptimizerOptions
{
    public string? GradientModel { get; init; }

    public string? ApplyEditModel { get; init; }

    public float DiversityTemperature { get; init; } = 1.0f;

    public int GradientBatchSize { get; init; } = 4;

    public int ValidationBatchSize { get; init; } = 16;

    public int BeamWidth { get; init; } = 4;

    public int BranchFactor { get; init; } = 4;

    public int BeamRounds { get; init; } = 3;

    public bool RunInitialValidation { get; init; } = true;

    public int RandomSeed { get; init; } = 0;

    /// <summary>The exact Microsoft.Extensions.AI.Evaluation metric to optimize when no custom selector is supplied.</summary>
    public string? ObjectiveMetricName { get; init; }

    /// <summary>The direction in which the named objective should improve.</summary>
    public EvaluationScoreDirection? ObjectiveDirection { get; init; }

    /// <summary>Maps official evaluation metrics to the scalar objective APO maximizes.</summary>
    public Func<EvaluationResult, double>? ScoreSelector { get; init; }
}

/// <summary>Direction used to convert one named evaluation metric into APO's maximize-only objective.</summary>
public enum EvaluationScoreDirection
{
    Maximize,
    Minimize,
}

/// <summary>A prompt template and its validation score at one search step.</summary>
public sealed record VersionedPromptTemplate(
    string Version,
    PromptTemplateResource PromptTemplate,
    double? Score);

/// <summary>Result of optimizing one named prompt resource with in-process rollouts and evaluation.</summary>
public sealed record AutomaticPromptOptimizationResult(
    VersionedPromptTemplate BestPrompt,
    IReadOnlyList<VersionedPromptTemplate> History);

/// <summary>
/// Runs Agent Lightning's textual-gradient Automatic Prompt Optimization loop over the local trainer,
/// Microsoft.Extensions.AI chat clients, and Microsoft.Extensions.AI.Evaluation evaluators.
/// </summary>
public sealed class AutomaticPromptOptimizer
{
    private const string PromptResourceNamePrefix = "ManagedCode.AgentLightning.AgentRuntime.Optimization.Prompts.";
    private static readonly string[] GradientPrompts =
    {
        "text_gradient_variant01.txt",
        "text_gradient_variant02.txt",
        "text_gradient_variant03.txt",
    };
    private static readonly string[] ApplyEditPrompts =
    {
        "apply_edit_variant01.txt",
        "apply_edit_variant02.txt",
    };
    private static readonly string[] ResourceTokens = ["{{prompt_template}}", "{{experiments}}", "{{critique}}"];

    private readonly ManagedCode.AgentLightning.AgentRuntime.Trainer.Trainer _trainer;
    private readonly IChatClient _optimizationChatClient;
    private readonly IEvaluator _evaluator;
    private readonly ChatConfiguration _evaluationChatConfiguration;
    private readonly AutomaticPromptOptimizerOptions _options;
    private readonly ILogger<AutomaticPromptOptimizer> _logger;
    private readonly Func<object, IEnumerable<EvaluationContext>?>? _contextFactory;
    private Random _random;
    private readonly List<VersionedPromptTemplate> _history = [];
    private int _version;
    private int _isOptimizing;

    public AutomaticPromptOptimizer(
        ManagedCode.AgentLightning.AgentRuntime.Trainer.Trainer trainer,
        IChatClient optimizationChatClient,
        IEvaluator evaluator,
        IChatClient evaluationChatClient,
        AutomaticPromptOptimizerOptions options,
        ILogger<AutomaticPromptOptimizer> logger,
        Func<object, IEnumerable<EvaluationContext>?>? contextFactory = null)
    {
        _trainer = trainer ?? throw new ArgumentNullException(nameof(trainer));
        _optimizationChatClient = optimizationChatClient ?? throw new ArgumentNullException(nameof(optimizationChatClient));
        _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
        _evaluationChatConfiguration = new ChatConfiguration(evaluationChatClient ?? throw new ArgumentNullException(nameof(evaluationChatClient)));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _contextFactory = contextFactory;
        _random = new Random(options.RandomSeed);
        ValidateOptions(options);
    }

    public async Task<AutomaticPromptOptimizationResult> OptimizeAsync(
        PromptTemplateResource seedPrompt,
        IEnumerable<object> trainDataset,
        IEnumerable<object> validationDataset,
        IReadOnlyDictionary<string, ResourceDefinition>? fixedResources = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(seedPrompt);
        if (seedPrompt.Engine != PromptTemplateEngine.FString)
        {
            throw new NotSupportedException("Embedded APO currently requires the in-process FString prompt renderer.");
        }

        var training = trainDataset?.ToArray() ?? throw new ArgumentNullException(nameof(trainDataset));
        var validation = validationDataset?.ToArray() ?? throw new ArgumentNullException(nameof(validationDataset));
        if (training.Length == 0)
        {
            throw new ArgumentException("APO requires at least one training example.", nameof(trainDataset));
        }

        if (validation.Length == 0)
        {
            throw new ArgumentException("APO requires at least one validation example.", nameof(validationDataset));
        }

        if (string.IsNullOrWhiteSpace(_trainer.PromptResourceName))
        {
            throw new InvalidOperationException("LightningAgentOptions.PromptResourceName must identify the prompt being optimized.");
        }

        if (Interlocked.CompareExchange(ref _isOptimizing, 1, 0) != 0)
        {
            throw new InvalidOperationException("This AutomaticPromptOptimizer instance is already optimizing a dataset.");
        }

        try
        {
            _random = new Random(_options.RandomSeed);
            _history.Clear();
            _version = 0;
            var beam = new List<VersionedPromptTemplate> { CreateVersion(seedPrompt) };
            var validationBatch = Sample(validation, _options.ValidationBatchSize);
            if (_options.RunInitialValidation)
            {
                var seedScore = await EvaluatePromptAsync(beam[0], validationBatch, fixedResources, RolloutMode.Val, cancellationToken).ConfigureAwait(false);
                beam[0] = beam[0] with { Score = seedScore };
                Remember(beam[0]);
            }

            for (var round = 0; round < _options.BeamRounds; round++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var candidates = new Dictionary<string, VersionedPromptTemplate>(StringComparer.Ordinal);
                foreach (var parent in beam)
                {
                    var gradientBatch = Sample(training, _options.GradientBatchSize);
                    var trainingRuns = await RunPromptAsync(parent, gradientBatch, fixedResources, RolloutMode.Train, cancellationToken).ConfigureAwait(false);
                    if (trainingRuns.Count != gradientBatch.Count)
                    {
                        throw new InvalidOperationException($"APO expected {gradientBatch.Count} successful training rollouts but received {trainingRuns.Count}.");
                    }

                    _ = await EvaluateRunsAsync(trainingRuns, cancellationToken).ConfigureAwait(false);
                    var experiments = await BuildExperimentsAsync(trainingRuns, cancellationToken).ConfigureAwait(false);
                    var critique = await ComputeGradientAsync(parent, experiments, cancellationToken).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(critique))
                    {
                        _logger.LogWarning("APO received an empty critique for {PromptVersion}; skipping its branches.", parent.Version);
                        continue;
                    }

                    for (var branch = 0; branch < _options.BranchFactor; branch++)
                    {
                        var edited = await ApplyEditAsync(parent, critique, cancellationToken).ConfigureAwait(false);
                        if (string.IsNullOrWhiteSpace(edited) || string.Equals(edited, parent.PromptTemplate.Template, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        if (!PreservesPlaceholders(parent.PromptTemplate.Template, edited))
                        {
                            _logger.LogWarning("APO rejected a candidate that removed one or more prompt placeholders.");
                            continue;
                        }

                        candidates.TryAdd(edited, CreateVersion(parent.PromptTemplate with { Template = edited }));
                    }
                }

                if (candidates.Count == 0)
                {
                    _logger.LogInformation("APO stopped after round {Round}; no distinct valid prompt edits were generated.", round + 1);
                    break;
                }

                var scored = new List<VersionedPromptTemplate>(candidates.Count);
                foreach (var candidate in candidates.Values)
                {
                    var score = await EvaluatePromptAsync(candidate, validationBatch, fixedResources, RolloutMode.Val, cancellationToken).ConfigureAwait(false);
                    var scoredCandidate = candidate with { Score = score };
                    scored.Add(scoredCandidate);
                    Remember(scoredCandidate);
                }

                beam = scored
                    .OrderByDescending(candidate => candidate.Score)
                    .ThenBy(candidate => candidate.Version, StringComparer.Ordinal)
                    .Take(_options.BeamWidth)
                    .ToList();
            }

            var best = _history
                .Where(prompt => prompt.Score.HasValue)
                .OrderByDescending(prompt => prompt.Score)
                .ThenBy(prompt => prompt.Version, StringComparer.Ordinal)
                .FirstOrDefault() ?? beam[0];
            return new AutomaticPromptOptimizationResult(best, _history.ToArray());
        }
        finally
        {
            Volatile.Write(ref _isOptimizing, 0);
        }
    }

    private async Task<double> EvaluatePromptAsync(
        VersionedPromptTemplate prompt,
        IReadOnlyList<object> tasks,
        IReadOnlyDictionary<string, ResourceDefinition>? fixedResources,
        RolloutMode mode,
        CancellationToken cancellationToken)
    {
        var runs = await RunPromptAsync(prompt, tasks, fixedResources, mode, cancellationToken).ConfigureAwait(false);
        if (runs.Count != tasks.Count)
        {
            throw new InvalidOperationException($"APO expected {tasks.Count} successful rollouts but received {runs.Count}.");
        }

        return await EvaluateRunsAsync(runs, cancellationToken).ConfigureAwait(false);
    }

    private async Task<double> EvaluateRunsAsync(
        IReadOnlyList<LightningExecutionResult> runs,
        CancellationToken cancellationToken)
    {
        if (runs.Count == 0)
        {
            throw new InvalidOperationException("APO cannot evaluate an empty rollout batch.");
        }

        var scores = new List<double>(runs.Count);
        foreach (var run in runs)
        {
            var result = await _evaluator.EvaluateAsync(
                run.Messages,
                run.Response,
                _evaluationChatConfiguration,
                _contextFactory?.Invoke(run.Rollout.Input),
                cancellationToken).ConfigureAwait(false);
            if (result.Metrics.Values.Any(metric => metric.Diagnostics?.Any(diagnostic => diagnostic.Severity == EvaluationDiagnosticSeverity.Error) == true))
            {
                throw new InvalidOperationException("The evaluator reported an infrastructure error; APO cannot compare this rollout.");
            }

            var score = (_options.ScoreSelector ?? SelectScore)(result);
            if (!double.IsFinite(score))
            {
                throw new InvalidOperationException("The configured APO score selector returned a non-finite score.");
            }

            await _trainer.RecordEvaluationAsync(run, result, score, cancellationToken).ConfigureAwait(false);
            scores.Add(score);
        }

        return scores.Average();
    }

    private Task<IReadOnlyList<LightningExecutionResult>> RunPromptAsync(
        VersionedPromptTemplate prompt,
        IReadOnlyList<object> tasks,
        IReadOnlyDictionary<string, ResourceDefinition>? fixedResources,
        RolloutMode mode,
        CancellationToken cancellationToken)
    {
        var resources = fixedResources?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
            ?? new Dictionary<string, ResourceDefinition>(StringComparer.Ordinal);
        resources[_trainer.PromptResourceName] = prompt.PromptTemplate;
        return _trainer.RunRolloutsAsync(tasks, new NamedResources(resources), mode, cancellationToken: cancellationToken);
    }

    private async Task<string> ComputeGradientAsync(
        VersionedPromptTemplate prompt,
        string experiments,
        CancellationToken cancellationToken)
    {
        var file = GradientPrompts[_random.Next(GradientPrompts.Length)];
        var template = ReadPrompt(file);
        var body = Render(template, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["prompt_template"] = prompt.PromptTemplate.Template,
            ["experiments"] = experiments,
        });
        var response = await _optimizationChatClient.GetResponseAsync(
            [new ChatMessage(ChatRole.User, body)],
            CreateChatOptions(_options.GradientModel),
            cancellationToken).ConfigureAwait(false);
        return response.Text.Trim();
    }

    private async Task<string> ApplyEditAsync(
        VersionedPromptTemplate prompt,
        string critique,
        CancellationToken cancellationToken)
    {
        var file = ApplyEditPrompts[_random.Next(ApplyEditPrompts.Length)];
        var template = ReadPrompt(file);
        var body = Render(template, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["prompt_template"] = prompt.PromptTemplate.Template,
            ["critique"] = critique,
        });
        var response = await _optimizationChatClient.GetResponseAsync(
            [new ChatMessage(ChatRole.User, body)],
            CreateChatOptions(_options.ApplyEditModel),
            cancellationToken).ConfigureAwait(false);
        return response.Text.Trim();
    }

    private async Task<string> BuildExperimentsAsync(
        IReadOnlyList<LightningExecutionResult> runs,
        CancellationToken cancellationToken)
    {
        var experiments = new List<object>(runs.Count);
        foreach (var run in runs)
        {
            var spans = await _trainer.GetSpansAsync(run.Rollout.RolloutId, cancellationToken).ConfigureAwait(false);
            experiments.Add(new
            {
                status = run.Rollout.Status.ToString(),
                spans = spans.Select(span => new { span.Name, span.Attributes, span.Status.StatusCode }).ToArray(),
                messages = run.Messages.Select(message => new { role = message.Role.ToString(), content = message.Text }).Append(
                    new { role = "assistant", content = run.Response.Text }).ToArray(),
            });
        }

        return JsonSerializer.Serialize(experiments);
    }

    private IReadOnlyList<object> Sample(IReadOnlyList<object> dataset, int batchSize)
    {
        var count = Math.Min(batchSize, dataset.Count);
        var indices = Enumerable.Range(0, dataset.Count).ToArray();
        for (var index = 0; index < count; index++)
        {
            var selected = _random.Next(index, indices.Length);
            (indices[index], indices[selected]) = (indices[selected], indices[index]);
        }

        return indices.Take(count).Select(index => dataset[index]).ToArray();
    }

    private VersionedPromptTemplate CreateVersion(PromptTemplateResource prompt) =>
        new($"v{_version++}", prompt, null);

    private void Remember(VersionedPromptTemplate prompt)
    {
        _history.Add(prompt);
        _logger.LogInformation("APO prompt {PromptVersion} scored {Score}.", prompt.Version, prompt.Score);
    }

    private double SelectScore(EvaluationResult result)
    {
        if (result.Metrics.Values.Any(metric => metric.Diagnostics?.Any(diagnostic => diagnostic.Severity == EvaluationDiagnosticSeverity.Error) == true))
        {
            throw new InvalidOperationException("The evaluator reported an infrastructure error; APO cannot compare this rollout.");
        }

        if (_options.ScoreSelector is not null)
        {
            return _options.ScoreSelector(result);
        }

        if (!result.Metrics.TryGetValue(_options.ObjectiveMetricName!, out var metric))
        {
            throw new InvalidOperationException($"APO did not receive the configured objective metric '{_options.ObjectiveMetricName}'.");
        }

        var value = metric switch
        {
            NumericMetric { Value: { } numeric } => numeric,
            BooleanMetric { Value: true } => 1d,
            BooleanMetric { Value: false } => 0d,
            _ => throw new InvalidOperationException($"APO objective metric '{_options.ObjectiveMetricName}' must have a numeric or Boolean value."),
        };
        return _options.ObjectiveDirection == EvaluationScoreDirection.Maximize ? value : -value;
    }

    private ChatOptions CreateChatOptions(string? modelId)
    {
        var chatOptions = new ChatOptions { Temperature = _options.DiversityTemperature };
        if (!string.IsNullOrWhiteSpace(modelId))
        {
            chatOptions.ModelId = modelId;
        }

        return chatOptions;
    }

    private static bool PreservesPlaceholders(string original, string edited)
    {
        return TryGetPlaceholders(original, out var originalFields) &&
            TryGetPlaceholders(edited, out var editedFields) &&
            originalFields.SetEquals(editedFields);
    }

    private static bool TryGetPlaceholders(string template, out HashSet<string> fields)
    {
        fields = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < template.Length;)
        {
            if (template[index] == '{')
            {
                if (index + 1 < template.Length && template[index + 1] == '{')
                {
                    index += 2;
                    continue;
                }

                var closingBrace = template.IndexOf('}', index + 1);
                if (closingBrace < 0)
                {
                    return false;
                }

                var field = template[(index + 1)..closingBrace];
                if (field.Length == 0 || field.Any(character => !char.IsLetterOrDigit(character) && character != '_'))
                {
                    return false;
                }

                fields.Add(field);
                index = closingBrace + 1;
                continue;
            }

            if (template[index] == '}')
            {
                if (index + 1 < template.Length && template[index + 1] == '}')
                {
                    index += 2;
                    continue;
                }

                return false;
            }

            index++;
        }

        return true;
    }

    private static string ReadPrompt(string fileName)
    {
        var resourceName = PromptResourceNamePrefix + fileName;
        using var stream = typeof(AutomaticPromptOptimizer).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Required APO prompt resource '{resourceName}' is missing.");
        using var reader = new StreamReader(stream);
        var content = reader.ReadToEnd();
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException($"Required APO prompt resource '{resourceName}' is empty.");
        }

        return content;
    }

    private static string Render(string template, IReadOnlyDictionary<string, string> values)
    {
        var matches = System.Text.RegularExpressions.Regex.Matches(template, "\\{\\{([^{}]+)\\}\\}");
        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            var token = match.Value;
            var key = match.Groups[1].Value;
            if (!ResourceTokens.Contains(token, StringComparer.Ordinal))
            {
                throw new InvalidOperationException($"Unknown APO prompt token '{token}' was found in a prompt resource.");
            }

            if (!values.TryGetValue(key, out var value))
            {
                throw new InvalidOperationException($"Required APO prompt token '{token}' was not supplied.");
            }
        }

        return System.Text.RegularExpressions.Regex.Replace(
            template,
            "\\{\\{([^{}]+)\\}\\}",
            match => values[match.Groups[1].Value]);
    }

    private static void ValidateOptions(AutomaticPromptOptimizerOptions options)
    {
        if (options.GradientBatchSize < 1 || options.ValidationBatchSize < 1 || options.BeamWidth < 1 || options.BranchFactor < 1 || options.BeamRounds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "APO batch sizes, beam width, and branch factor must be positive; beam rounds cannot be negative.");
        }

        if (!float.IsFinite(options.DiversityTemperature) || options.DiversityTemperature < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Diversity temperature must be finite and non-negative.");
        }

        if (options.GradientModel is not null && string.IsNullOrWhiteSpace(options.GradientModel) ||
            options.ApplyEditModel is not null && string.IsNullOrWhiteSpace(options.ApplyEditModel))
        {
            throw new ArgumentException("Optional APO model overrides cannot be empty or whitespace.", nameof(options));
        }

        var hasObjective = !string.IsNullOrWhiteSpace(options.ObjectiveMetricName);
        if (options.ScoreSelector is null && (!hasObjective || options.ObjectiveDirection is null))
        {
            throw new ArgumentException("Configure a custom ScoreSelector or both ObjectiveMetricName and ObjectiveDirection; APO will not guess which evaluation metric or direction to optimize.", nameof(options));
        }

        if (options.ScoreSelector is not null && (hasObjective || options.ObjectiveDirection is not null))
        {
            throw new ArgumentException("Configure either ScoreSelector or the named objective and direction, not both.", nameof(options));
        }

        if (options.ObjectiveDirection is { } direction && !Enum.IsDefined(direction))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The objective direction is unsupported.");
        }
    }
}
