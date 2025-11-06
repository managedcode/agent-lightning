using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using ManagedCode.AgentLightning.Core.Tracing;
using OpenTelemetry.Resources;

namespace ManagedCode.AgentLightning.Core.Adapters;

/// <summary>
/// Converts <see cref="Activity"/> instances into serialized <see cref="SpanModel"/> records.
/// </summary>
public sealed class ActivityToSpanModelAdapter : OtelTraceAdapter<IReadOnlyList<SpanModel>>
{
    private readonly string? _defaultRolloutId;
    private readonly string? _defaultAttemptId;
    private readonly int _sequenceSeed;
    private readonly IComparer<Activity> _ordering;
    private readonly Func<Activity, Resource?>? _resourceResolver;

    public ActivityToSpanModelAdapter(
        string? defaultRolloutId = null,
        string? defaultAttemptId = null,
        int sequenceSeed = 1,
        IComparer<Activity>? ordering = null,
        Func<Activity, Resource?>? resourceResolver = null)
    {
        _defaultRolloutId = defaultRolloutId;
        _defaultAttemptId = defaultAttemptId;
        _sequenceSeed = sequenceSeed < 0 ? 0 : sequenceSeed;
        _ordering = ordering ?? ActivityStartComparer.Instance;
        _resourceResolver = resourceResolver;
    }

    protected override IReadOnlyList<SpanModel> ConvertCore(IReadOnlyList<Activity> source)
    {
        if (source.Count == 0)
        {
            return Array.Empty<SpanModel>();
        }

        var ordered = source.Count == 1
            ? source
            : source.OrderBy(activity => activity, _ordering).ToArray();

        var result = new List<SpanModel>(ordered.Count);
        var sequenceId = Math.Max(0, _sequenceSeed);

        foreach (var activity in ordered)
        {
            sequenceId++;
            var rolloutId = ResolveRolloutId(activity);
            var attemptId = ResolveAttemptId(activity);
            var explicitSequence = ResolveSequenceId(activity) ?? sequenceId;
            var resource = _resourceResolver?.Invoke(activity);

            var span = SpanModel.FromActivity(
                activity,
                rolloutId,
                attemptId,
                explicitSequence,
                resource);

            result.Add(span);
        }

        return result;
    }

    private string ResolveRolloutId(Activity activity)
    {
        return GetTagString(activity, "agentlightning.rollout_id")
            ?? GetTagString(activity, "rollout.id")
            ?? _defaultRolloutId
            ?? "rollout-unknown";
    }

    private string ResolveAttemptId(Activity activity)
    {
        return GetTagString(activity, "agentlightning.attempt_id")
            ?? GetTagString(activity, "attempt.id")
            ?? _defaultAttemptId
            ?? "attempt-unknown";
    }

    private static int? ResolveSequenceId(Activity activity)
    {
        var sequenceValue = GetTag(activity, "agentlightning.sequence_id")
            ?? GetTag(activity, "gen_ai.sequence_id");

        if (sequenceValue is null)
        {
            return null;
        }

        return TryConvertToInt(sequenceValue, out var sequenceId) ? sequenceId : null;
    }

    private static object? GetTag(Activity activity, string name)
    {
#if NET9_0_OR_GREATER
        return activity.GetTagItem(name);
#else
        return activity.TagObjects?.FirstOrDefault(tag => string.Equals(tag.Key, name, StringComparison.Ordinal)).Value;
#endif
    }

    private static string? GetTagString(Activity activity, string name)
    {
        var value = GetTag(activity, name);
        if (value is null)
        {
            return null;
        }

        return value switch
        {
            string s => s,
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString(),
        };
    }

    private static bool TryConvertToInt(object value, out int result)
    {
        switch (value)
        {
            case int i:
                result = i;
                return true;
            case long l when l is >= int.MinValue and <= int.MaxValue:
                result = (int)l;
                return true;
            case string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed):
                result = parsed;
                return true;
            case IFormattable formattable:
                var asString = formattable.ToString(null, CultureInfo.InvariantCulture);
                if (int.TryParse(asString, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                {
                    result = parsed;
                    return true;
                }

                break;
        }

        result = default;
        return false;
    }

    private sealed class ActivityStartComparer : IComparer<Activity>
    {
        public static ActivityStartComparer Instance { get; } = new();

        public int Compare(Activity? x, Activity? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null)
            {
                return -1;
            }

            if (y is null)
            {
                return 1;
            }

            var startComparison = x.StartTimeUtc.CompareTo(y.StartTimeUtc);
            if (startComparison != 0)
            {
                return startComparison;
            }

            return string.CompareOrdinal(x.Id, y.Id);
        }
    }
}
