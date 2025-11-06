using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using ManagedCode.AgentLightning.Core.Models;
using ManagedCode.AgentLightning.Core.Tracing;

namespace ManagedCode.AgentLightning.Core.Adapters;

/// <summary>
/// Strategies for matching reward spans to LLM call spans.
/// Mirrors <c>agentlightning.adapter.triplet.RewardMatchPolicy</c>.
/// </summary>
public enum RewardMatchPolicy
{
    FirstSibling,
    FirstOccurrence,
}

/// <summary>
/// Base class for adapters that emit <see cref="Triplet"/> trajectories from trace spans.
/// </summary>
public abstract class TraceToTripletBase : TraceAdapter<IReadOnlyList<Triplet>>
{
}

/// <summary>
/// Converts tracer-emitted spans into triplet trajectories.
/// Mirrors <c>agentlightning.adapter.triplet.TracerTraceToTriplet</c>.
/// </summary>
public sealed class TracerTraceToTripletAdapter : TraceToTripletBase
{
    private readonly bool _repairHierarchy;
    private readonly Regex _llmCallMatch;
    private readonly Regex? _agentMatch;
    private readonly bool _excludeLlmCallInReward;
    private readonly RewardMatchPolicy _rewardMatchPolicy;
    private readonly bool _skipEmptyTokenSpans;
    private readonly bool _deduplicateLlmCalls;

    public TracerTraceToTripletAdapter(
        bool repairHierarchy = true,
        string? llmCallMatch = @"openai\.chat\.completion",
        string? agentMatch = null,
        bool excludeLlmCallInReward = true,
        RewardMatchPolicy rewardMatchPolicy = RewardMatchPolicy.FirstOccurrence,
        bool skipEmptyTokenSpans = false,
        bool deduplicateLlmCalls = true)
    {
        _repairHierarchy = repairHierarchy;
        _llmCallMatch = new Regex(llmCallMatch ?? throw new ArgumentNullException(nameof(llmCallMatch)), RegexOptions.Compiled);
        _agentMatch = string.IsNullOrWhiteSpace(agentMatch) ? null : new Regex(agentMatch, RegexOptions.Compiled);
        _excludeLlmCallInReward = excludeLlmCallInReward;
        _rewardMatchPolicy = rewardMatchPolicy;
        _skipEmptyTokenSpans = skipEmptyTokenSpans;
        _deduplicateLlmCalls = deduplicateLlmCalls;
    }

    protected override IReadOnlyList<Triplet> ConvertCore(IReadOnlyList<SpanModel> source)
    {
        if (source.Count == 0)
        {
            return Array.Empty<Triplet>();
        }

        var traceTree = TraceTree.FromSpans(source);
        if (_repairHierarchy)
        {
            traceTree.RepairHierarchy();
        }

        return traceTree.ToTrajectory(
            _llmCallMatch,
            _agentMatch,
            _excludeLlmCallInReward,
            _deduplicateLlmCalls,
            _rewardMatchPolicy,
            finalReward: null,
            skipEmptyTokenSpans: _skipEmptyTokenSpans);
    }

    private sealed class TraceTree
    {
        private static readonly string[] RewardPayloadKeys =
        {
            "agentops.task.output",
            "agentops.entity.output",
        };

        private TraceTree(string id, SpanModel span, List<TraceTree> children)
        {
            Id = id;
            Span = span;
            Children = children;
        }

        public string Id { get; }

        public SpanModel Span { get; }

        public List<TraceTree> Children { get; }

        private double? StartTime => Span.StartTime;

        private double? EndTime => Span.EndTime;

        public static TraceTree FromSpans(IReadOnlyList<SpanModel> spans)
        {
            if (spans is null)
            {
                throw new ArgumentNullException(nameof(spans));
            }

            if (spans.Count == 0)
            {
                throw new ArgumentException("At least one span is required to build the trace tree.", nameof(spans));
            }

            var idToSpan = spans
                .Where(span => !string.IsNullOrEmpty(span.SpanId))
                .ToDictionary(span => span.SpanId, StringComparer.Ordinal);

            var forwardGraph = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var rootIds = new List<string>();

            foreach (var span in spans)
            {
                if (!string.IsNullOrEmpty(span.ParentId))
                {
                    var parentId = span.ParentId!;
                    if (!forwardGraph.TryGetValue(parentId, out var children))
                    {
                        children = new List<string>();
                        forwardGraph[parentId] = children;
                    }

                    children.Add(span.SpanId);
                }
                else if (!string.IsNullOrEmpty(span.SpanId))
                {
                    rootIds.Add(span.SpanId);
                }
            }

            foreach (var parentId in forwardGraph.Keys)
            {
                if (!idToSpan.ContainsKey(parentId) && !rootIds.Contains(parentId))
                {
                    rootIds.Add(parentId);
                }
            }

            TraceTree Visit(string nodeId)
            {
                var children = forwardGraph.TryGetValue(nodeId, out var childIds)
                    ? childIds.Select(Visit).ToList()
                    : new List<TraceTree>();

                if (!idToSpan.TryGetValue(nodeId, out var span))
                {
                    if (children.Count == 0)
                    {
                        throw new InvalidOperationException($"Unable to synthesise span '{nodeId}' without children.");
                    }

                    var firstChild = children[0].Span;
                    var start = MinTime(children.Select(child => child.StartTime));
                    var end = MaxTime(children.Select(child => child.EndTime));

                    span = SpanModel.FromAttributes(
                        new Dictionary<string, object?>(StringComparer.Ordinal),
                        rolloutId: firstChild.RolloutId,
                        attemptId: firstChild.AttemptId,
                        sequenceId: firstChild.SequenceId,
                        traceId: firstChild.TraceId,
                        spanId: nodeId,
                        parentId: null,
                        name: SpanNames.Virtual.ToStringValue(),
                        startTime: start,
                        endTime: end);
                }

                return new TraceTree(nodeId, span, children);
            }

            TraceTree root;

            if (rootIds.Count == 0)
            {
                throw new InvalidOperationException("No root spans were discovered for the trace.");
            }

            if (rootIds.Count == 1)
            {
                root = Visit(rootIds[0]);
            }
            else
            {
                var roots = rootIds.Select(Visit).ToList();
                var first = roots[0].Span;
                var start = MinTime(roots.Select(child => child.StartTime));
                var end = MaxTime(roots.Select(child => child.EndTime));

                var virtualRootSpan = SpanModel.FromAttributes(
                    new Dictionary<string, object?>(StringComparer.Ordinal),
                    rolloutId: first.RolloutId,
                    attemptId: first.AttemptId,
                    sequenceId: first.SequenceId,
                    traceId: first.TraceId,
                    spanId: "virtual-root",
                    parentId: null,
                    name: "virtual-root",
                    startTime: start,
                    endTime: end);

                root = new TraceTree("virtual-root", virtualRootSpan, roots);
            }

            return root;
        }

        public void RepairHierarchy()
        {
            if (Children.Count <= 1)
            {
                return;
            }

            var nodesToRepair = Children.ToList();
            foreach (var repairNode in nodesToRepair)
            {
                if (Children.Count == 1)
                {
                    break;
                }

                if (!repairNode.StartTime.HasValue || !repairNode.EndTime.HasValue)
                {
                    continue;
                }

                TraceTree? closestParent = null;
                var closestDuration = double.PositiveInfinity;

                foreach (var candidate in Traverse())
                {
                    if (ReferenceEquals(candidate, this) || ReferenceEquals(candidate, repairNode))
                    {
                        continue;
                    }

                    if (!candidate.StartTime.HasValue || !candidate.EndTime.HasValue)
                    {
                        continue;
                    }

                    if (candidate.StartTime.Value <= repairNode.StartTime.Value &&
                        candidate.EndTime.Value >= repairNode.EndTime.Value)
                    {
                        var durationDelta = (candidate.EndTime.Value - repairNode.EndTime.Value) +
                                            (repairNode.StartTime.Value - candidate.StartTime.Value);

                        if (durationDelta > 0 && durationDelta < closestDuration)
                        {
                            closestDuration = durationDelta;
                            closestParent = candidate;
                        }
                    }
                }

                if (closestParent is not null)
                {
                    Children.Remove(repairNode);
                    closestParent.Children.Add(repairNode);
                }
            }
        }

        public IReadOnlyList<Triplet> ToTrajectory(
            Regex llmCallMatch,
            Regex? agentMatch,
            bool excludeLlmCallInReward,
            bool deduplicateLlmCalls,
            RewardMatchPolicy rewardMatchPolicy,
            double? finalReward,
            bool skipEmptyTokenSpans)
        {
            var existingResponseIds = new HashSet<string>(StringComparer.Ordinal);
            var initialAgentContext = agentMatch is null ? "*" : null;
            bool? initialRewardContext = excludeLlmCallInReward ? false : null;
            bool? initialLlmContext = deduplicateLlmCalls ? false : null;

            var llmCalls = FindLlmCalls(
                llmCallMatch,
                agentMatch,
                initialAgentContext,
                initialRewardContext,
                initialLlmContext,
                existingResponseIds);

            var filtered = new List<(TraceTree Node, Triplet Triplet)>();

            foreach (var (node, agentMarker) in llmCalls)
            {
                var agentName = string.IsNullOrEmpty(agentMarker) ? "*" : agentMarker;
                var triplet = SpanToTriplet(node.Span, agentName, out var hasTokens);
                if (skipEmptyTokenSpans && !hasTokens)
                {
                    continue;
                }

                filtered.Add((node, triplet));
            }

            var rewards = MatchRewards(rewardMatchPolicy, filtered.Select(pair => pair.Node).ToList());

            var results = new List<Triplet>(filtered.Count);
            foreach (var (node, triplet) in filtered)
            {
                if (rewards.TryGetValue(node.Id, out var reward))
                {
                    results.Add(triplet with { Reward = reward });
                }
                else
                {
                    results.Add(triplet);
                }
            }

            if (finalReward.HasValue && results.Count > 0)
            {
                var last = results[^1];
                results[^1] = last with { Reward = finalReward };
            }

            return results;
        }

        private List<(TraceTree Node, string AgentMarker)> FindLlmCalls(
            Regex llmCallMatch,
            Regex? agentMatch,
            string? withinMatchingSubtree,
            bool? withinReward,
            bool? withinLlmCall,
            HashSet<string> existingResponseIds)
        {
            var matches = new List<(TraceTree, string)>();

            var isCandidate = withinMatchingSubtree is not null &&
                              withinReward != true &&
                              llmCallMatch.IsMatch(Span.Name);

            var responseId = GetResponseId();
            var nextWithinLlmCall = withinLlmCall;

            if (isCandidate)
            {
                if (responseId is null && withinLlmCall == true)
                {
                    isCandidate = false;
                }
                else if (responseId is not null && existingResponseIds.Contains(responseId))
                {
                    isCandidate = false;
                }
            }

            if (isCandidate)
            {
                matches.Add((this, withinMatchingSubtree ?? string.Empty));
                if (responseId is not null)
                {
                    existingResponseIds.Add(responseId);
                }

                if (withinLlmCall.HasValue)
                {
                    nextWithinLlmCall = true;
                }
            }

            var updatedMatching = withinMatchingSubtree;
            var agentName = AgentName();
            if (agentName is not null)
            {
                updatedMatching = agentMatch is null || agentMatch.IsMatch(agentName)
                    ? agentName
                    : null;
            }

            var updatedReward = withinReward;
            if (withinReward.HasValue && IsRewardSpan())
            {
                updatedReward = true;
            }

            foreach (var child in Children)
            {
                matches.AddRange(child.FindLlmCalls(
                    llmCallMatch,
                    agentMatch,
                    updatedMatching,
                    updatedReward,
                    nextWithinLlmCall,
                    existingResponseIds));
            }

            return matches;
        }

        private Dictionary<string, double?> MatchRewards(RewardMatchPolicy policy, List<TraceTree> llmCalls)
        {
            var rewards = new Dictionary<string, double?>(StringComparer.Ordinal);
            if (llmCalls.Count == 0)
            {
                return rewards;
            }

            var llmCallIds = new HashSet<string>(llmCalls.Select(call => call.Id), StringComparer.Ordinal);

            switch (policy)
            {
                case RewardMatchPolicy.FirstOccurrence:
                    {
                        var ordered = Traverse()
                            .OrderBy(node => node.StartTime ?? double.MaxValue)
                            .ToList();

                        var assignTo = new List<(string Id, double? EndTime)>();
                        foreach (var node in ordered)
                        {
                            if (llmCallIds.Contains(node.Id))
                            {
                                assignTo.Add((node.Id, node.EndTime));
                            }

                            if (node.TryGetReward(out var rewardValue))
                            {
                                var rewardTimestamp = node.EndTime ?? node.StartTime;
                                for (var index = assignTo.Count - 1; index >= 0; index--)
                                {
                                    var (candidateId, candidateEnd) = assignTo[index];
                                    if (rewardTimestamp.HasValue &&
                                        candidateEnd.HasValue &&
                                        candidateEnd.Value > rewardTimestamp.Value)
                                    {
                                        continue;
                                    }

                                    if (rewards.ContainsKey(candidateId))
                                    {
                                        continue;
                                    }

                                    rewards[candidateId] = rewardValue;
                                    break;
                                }
                            }
                        }

                        break;
                    }

                case RewardMatchPolicy.FirstSibling:
                    {
                        foreach (var node in Traverse())
                        {
                            if (!node.TryGetReward(out var rewardValue))
                            {
                                continue;
                            }

                            var rewardStart = node.StartTime;
                            var candidates = node.Children
                                .Where(child => llmCallIds.Contains(child.Id))
                                .Select(child => (child.Id, child.EndTime))
                                .ToList();

                            for (var index = candidates.Count - 1; index >= 0; index--)
                            {
                                var (candidateId, candidateEnd) = candidates[index];
                                var rewardTimestamp = node.EndTime ?? rewardStart;
                                if (rewardTimestamp.HasValue &&
                                    candidateEnd.HasValue &&
                                    candidateEnd.Value > rewardTimestamp.Value)
                                {
                                    continue;
                                }

                                if (rewards.ContainsKey(candidateId))
                                {
                                    continue;
                                }

                                rewards[candidateId] = rewardValue;
                                break;
                            }
                        }

                        break;
                    }
            }

            return rewards;
        }

        private Triplet SpanToTriplet(SpanModel span, string agentName, out bool hasCompleteTokens)
        {
            var promptTokens = ExtractIntList(GetAttribute(span.Attributes, "prompt_token_ids"));
            var responseTokens = ExtractIntList(GetAttribute(span.Attributes, "response_token_ids"));
            hasCompleteTokens = promptTokens.Count > 0 && responseTokens.Count > 0;

            var response = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["token_ids"] = responseTokens,
            };

            if (span.Attributes.TryGetValue("logprobs.content", out var logProbs) && logProbs is not null)
            {
                response["logprobs"] = ParseJsonOrReturn(logProbs);
            }

            var metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["response_id"] = GetResponseId(span),
                ["agent_name"] = agentName,
            };

            return new Triplet
            {
                Prompt = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["token_ids"] = promptTokens,
                },
                Response = response,
                Reward = null,
                Metadata = new ReadOnlyDictionary<string, object?>(metadata),
            };
        }

        private IEnumerable<TraceTree> Traverse()
        {
            yield return this;
            foreach (var child in Children)
            {
                foreach (var descendant in child.Traverse())
                {
                    yield return descendant;
                }
            }
        }

        private string? AgentName()
        {
            var attributes = Span.Attributes;
            if (attributes.Count == 0)
            {
                return null;
            }

            var agentName = GetString(attributes, "agent.name");
            if (!string.IsNullOrEmpty(agentName))
            {
                return agentName;
            }

            var agentOpsKind = GetString(attributes, "agentops.span.kind");
            if (string.Equals(agentOpsKind, "agent", StringComparison.Ordinal))
            {
                agentName = GetString(attributes, "operation.name");
                if (!string.IsNullOrEmpty(agentName))
                {
                    return agentName;
                }
            }

            agentName = GetString(attributes, "recipient_agent_type");
            if (!string.IsNullOrEmpty(agentName))
            {
                return agentName;
            }

            agentName = GetString(attributes, "langchain.chain.type");
            if (!string.IsNullOrEmpty(agentName))
            {
                return agentName;
            }

            agentName = GetString(attributes, "executor.id");
            if (!string.IsNullOrEmpty(agentName))
            {
                return agentName;
            }

            return null;
        }

        private bool TryGetReward(out double? reward)
        {
            foreach (var key in RewardPayloadKeys)
            {
                if (Span.Attributes.TryGetValue(key, out var payload) && payload is not null)
                {
                    if (TryExtractReward(payload, out reward))
                    {
                        return true;
                    }
                }
            }

            if (string.Equals(Span.Name, SpanNames.Reward.ToStringValue(), StringComparison.Ordinal))
            {
                if (Span.Attributes.TryGetValue("reward", out var rewardValue))
                {
                    reward = ConvertToNullableDouble(rewardValue);
                }
                else
                {
                    reward = null;
                }

                return true;
            }

            reward = null;
            return false;
        }

        private bool IsRewardSpan() =>
            string.Equals(Span.Name, SpanNames.Reward.ToStringValue(), StringComparison.Ordinal);

        private static object? GetAttribute(IReadOnlyDictionary<string, object?> attributes, string key) =>
            attributes.TryGetValue(key, out var value) ? value : null;

        private static string? GetString(IReadOnlyDictionary<string, object?> attributes, string key) =>
            attributes.TryGetValue(key, out var value) ? ConvertToString(value) : null;

        private string? GetResponseId() => GetResponseId(Span);

        private static string? GetResponseId(SpanModel span) =>
            span.Attributes.TryGetValue("gen_ai.response.id", out var value) ? ConvertToString(value) : null;

        private static IReadOnlyList<int> ExtractIntList(object? value)
        {
            if (value is null)
            {
                return Array.Empty<int>();
            }

            static IReadOnlyList<int> FromJsonElement(JsonElement element)
            {
                if (element.ValueKind != JsonValueKind.Array)
                {
                    return Array.Empty<int>();
                }

                var list = new List<int>(element.GetArrayLength());
                foreach (var item in element.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var number))
                    {
                        list.Add(number);
                    }
                }

                return list;
            }

            static bool TryConvertToInt(object? candidate, out int number)
            {
                switch (candidate)
                {
                    case int i:
                        number = i;
                        return true;
                    case long l when l is >= int.MinValue and <= int.MaxValue:
                        number = (int)l;
                        return true;
                    case short s:
                        number = s;
                        return true;
                    case byte b:
                        number = b;
                        return true;
                    case double d when d is >= int.MinValue and <= int.MaxValue && Math.Abs(d % 1) < double.Epsilon:
                        number = (int)d;
                        return true;
                    case float f when f is >= int.MinValue and <= int.MaxValue && Math.Abs(f % 1) < float.Epsilon:
                        number = (int)f;
                        return true;
                    case decimal m when m % 1 == 0 && m is >= int.MinValue and <= int.MaxValue:
                        number = (int)m;
                        return true;
                    case string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed):
                        number = parsed;
                        return true;
                    case JsonElement element when element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var parsedElement):
                        number = parsedElement;
                        return true;
                    default:
                        number = default;
                        return false;
                }
            }

            if (value is JsonElement jsonElement)
            {
                return FromJsonElement(jsonElement);
            }

            if (value is IEnumerable<int> iEnumerable)
            {
                return iEnumerable.ToArray();
            }

            if (value is IEnumerable<long> longEnumerable)
            {
                return longEnumerable.Select(item => (int)item).ToArray();
            }

            if (value is IEnumerable<object?> objectEnumerable)
            {
                var list = new List<int>();
                foreach (var item in objectEnumerable)
                {
                    if (TryConvertToInt(item, out var number))
                    {
                        list.Add(number);
                    }
                }

                return list;
            }

            if (value is string stringValue)
            {
                try
                {
                    using var document = JsonDocument.Parse(stringValue);
                    return FromJsonElement(document.RootElement);
                }
                catch (JsonException)
                {
                    return Array.Empty<int>();
                }
            }

            if (TryConvertToInt(value, out var single))
            {
                return new[] { single };
            }

            return Array.Empty<int>();
        }

        private static object? ParseJsonOrReturn(object value)
        {
            switch (value)
            {
                case JsonElement jsonElement:
                    return jsonElement.Clone();
                case string stringValue:
                    try
                    {
                        return JsonSerializer.Deserialize<JsonElement>(stringValue);
                    }
                    catch (JsonException)
                    {
                        return stringValue;
                    }
                default:
                    return value;
            }
        }

        private static bool TryExtractReward(object payload, out double? reward)
        {
            if (payload is IReadOnlyDictionary<string, object?> readOnlyDictionary)
            {
                return TryExtractReward(readOnlyDictionary, out reward);
            }

            if (payload is Dictionary<string, object?> dictionary)
            {
                return TryExtractReward(dictionary, out reward);
            }

            if (payload is JsonElement jsonElement && jsonElement.ValueKind == JsonValueKind.Object)
            {
                return TryExtractReward(jsonElement, out reward);
            }

            if (payload is string json)
            {
                try
                {
                    using var document = JsonDocument.Parse(json);
                    if (document.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        return TryExtractReward(document.RootElement, out reward);
                    }
                }
                catch (JsonException)
                {
                }
            }

            reward = null;
            return false;
        }

        private static bool TryExtractReward(IReadOnlyDictionary<string, object?> payload, out double? reward)
        {
            if (!payload.TryGetValue("type", out var type) ||
                !string.Equals(ConvertToString(type), "reward", StringComparison.OrdinalIgnoreCase))
            {
                reward = null;
                return false;
            }

            payload.TryGetValue("value", out var value);
            reward = ConvertToNullableDouble(value);
            return true;
        }

        private static bool TryExtractReward(JsonElement payload, out double? reward)
        {
            if (!payload.TryGetProperty("type", out var typeProperty) ||
                typeProperty.ValueKind != JsonValueKind.String ||
                !string.Equals(typeProperty.GetString(), "reward", StringComparison.OrdinalIgnoreCase))
            {
                reward = null;
                return false;
            }

            if (payload.TryGetProperty("value", out var valueProperty))
            {
                reward = valueProperty.ValueKind switch
                {
                    JsonValueKind.Number when valueProperty.TryGetDouble(out var number) => number,
                    JsonValueKind.String when double.TryParse(valueProperty.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
                    JsonValueKind.Null => null,
                    _ => null,
                };
            }
            else
            {
                reward = null;
            }

            return true;
        }

        private static string? ConvertToString(object? value)
        {
            switch (value)
            {
                case null:
                    return null;
                case string s:
                    return s;
                case IFormattable formattable:
                    return formattable.ToString(null, CultureInfo.InvariantCulture);
                case JsonElement jsonElement when jsonElement.ValueKind == JsonValueKind.String:
                    return jsonElement.GetString();
                default:
                    return value.ToString();
            }
        }

        private static double? ConvertToNullableDouble(object? value)
        {
            switch (value)
            {
                case null:
                    return null;
                case double d:
                    return d;
                case float f:
                    return f;
                case int i:
                    return i;
                case long l:
                    return l;
                case decimal m:
                    return (double)m;
                case string s when double.TryParse(s, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var parsed):
                    return parsed;
                case JsonElement jsonElement when jsonElement.ValueKind == JsonValueKind.Number && jsonElement.TryGetDouble(out var number):
                    return number;
                case JsonElement jsonElement when jsonElement.ValueKind == JsonValueKind.String &&
                                                  double.TryParse(jsonElement.GetString(), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var parsed):
                    return parsed;
                default:
                    return null;
            }
        }

        private static double? MinTime(IEnumerable<double?> values)
        {
            double? min = null;
            foreach (var value in values)
            {
                if (!value.HasValue)
                {
                    continue;
                }

                if (min is null || value.Value < min.Value)
                {
                    min = value.Value;
                }
            }

            return min;
        }

        private static double? MaxTime(IEnumerable<double?> values)
        {
            double? max = null;
            foreach (var value in values)
            {
                if (!value.HasValue)
                {
                    continue;
                }

                if (max is null || value.Value > max.Value)
                {
                    max = value.Value;
                }
            }

            return max;
        }
    }
}
