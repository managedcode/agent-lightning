using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using OpenTelemetry.Resources;

namespace ManagedCode.AgentLightning.Core.Tracing;

/// <summary>
/// Helper utilities for converting OpenTelemetry activities into serializable span models.
/// </summary>
internal static class TraceConversionHelpers
{
    private static readonly IReadOnlyDictionary<string, object?> EmptyObjectDictionaryImpl =
        new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>());

    private static readonly IReadOnlyDictionary<string, string> EmptyStringDictionaryImpl =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());

    internal static IReadOnlyDictionary<string, object?> EmptyObjectDictionary => EmptyObjectDictionaryImpl;

    internal static IReadOnlyDictionary<string, string> EmptyStringDictionary => EmptyStringDictionaryImpl;

    public static double? ConvertTimestamp(DateTime? dateTimeUtc)
    {
        if (dateTimeUtc is null)
        {
            return null;
        }

        if (dateTimeUtc.Value.Kind != DateTimeKind.Utc)
        {
            dateTimeUtc = DateTime.SpecifyKind(dateTimeUtc.Value, DateTimeKind.Utc);
        }

        var seconds = (dateTimeUtc.Value - DateTime.UnixEpoch).TotalSeconds;
        return double.IsFinite(seconds) ? seconds : null;
    }

    public static IReadOnlyDictionary<string, string> ParseTraceState(string? traceState)
    {
        if (string.IsNullOrWhiteSpace(traceState))
        {
            return EmptyStringDictionary;
        }

        var pairs = traceState.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in pairs)
        {
            var trimmed = pair.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var separator = trimmed.IndexOf('=');
            if (separator <= 0 || separator >= trimmed.Length - 1)
            {
                continue;
            }

            var key = trimmed[..separator].Trim();
            var value = trimmed[(separator + 1)..].Trim();
            if (key.Length == 0)
            {
                continue;
            }

            dict[key] = value;
        }

        return new ReadOnlyDictionary<string, string>(dict);
    }

    public static object? NormalizeAttributeValue(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case string or bool or int or long or short or byte:
                return value;
            case double or float or decimal:
                return Convert.ToDouble(value, CultureInfo.InvariantCulture);
            case IEnumerable<string> stringEnumerable:
                return stringEnumerable.ToArray();
            case IEnumerable<bool> boolEnumerable:
                return boolEnumerable.ToArray();
            case IEnumerable<int> intEnumerable:
                return intEnumerable.ToArray();
            case IEnumerable<long> longEnumerable:
                return longEnumerable.ToArray();
            case IEnumerable<double> doubleEnumerable:
                return doubleEnumerable.ToArray();
            case IEnumerable<float> floatEnumerable:
                return floatEnumerable.Select(f => Convert.ToDouble(f, CultureInfo.InvariantCulture)).ToArray();
            case IEnumerable<object?> objectEnumerable:
                return objectEnumerable.Select(NormalizeAttributeValue).ToArray();
            default:
                return value.ToString();
        }
    }

    public static IReadOnlyDictionary<string, object?> BuildAttributes(IEnumerable<KeyValuePair<string, object?>> tags)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var tag in tags ?? Array.Empty<KeyValuePair<string, object?>>())
        {
            dict[tag.Key] = NormalizeAttributeValue(tag.Value);
        }

        return dict.Count == 0 ? EmptyObjectDictionary : new ReadOnlyDictionary<string, object?>(dict);
    }

    public static IReadOnlyDictionary<string, object?> ExtractCustomProperties(Activity activity)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["ActivityKind"] = activity.Kind.ToString(),
            ["SourceName"] = activity.Source.Name,
            ["TraceFlags"] = activity.ActivityTraceFlags.ToString(),
        };

        if (!string.IsNullOrEmpty(activity.Source.Version))
        {
            dict["SourceVersion"] = activity.Source.Version;
        }

        return new ReadOnlyDictionary<string, object?>(dict);
    }

    public static IReadOnlyDictionary<string, object?> ExtractExtraFields(object source, IEnumerable<string> excludedFields)
    {
        var excluded = new HashSet<string>(excludedFields.Select(f => f.TrimStart('_')), StringComparer.Ordinal);
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in source.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (!property.CanRead)
            {
                continue;
            }

            if (excluded.Contains(property.Name))
            {
                continue;
            }

            if (property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            object? propValue;
            try
            {
                propValue = property.GetValue(source);
            }
            catch
            {
                continue;
            }

            if (propValue is null)
            {
                continue;
            }

            try
            {
                JsonSerializer.Serialize(propValue);
                values[property.Name] = propValue;
            }
            catch (NotSupportedException)
            {
                values[property.Name] = propValue.ToString();
            }
        }

        return values.Count == 0 ? EmptyObjectDictionary : new ReadOnlyDictionary<string, object?>(values);
    }
}

/// <summary>
/// Serializable representation of an activity/span context.
/// </summary>
public sealed record SpanContextModel(
    string TraceId,
    string SpanId,
    bool IsRemote,
    IReadOnlyDictionary<string, string> TraceState,
    IReadOnlyDictionary<string, object?> AdditionalData)
{
    public static SpanContextModel FromActivityContext(ActivityContext context) =>
        new(
            context.TraceId.ToHexString(),
            context.SpanId.ToHexString(),
            context.IsRemote,
            TraceConversionHelpers.EmptyStringDictionary,
            TraceConversionHelpers.EmptyObjectDictionary);
}

/// <summary>
/// Serializable representation of activity status.
/// </summary>
public sealed record TraceStatusModel(
    string StatusCode,
    string? Description,
    IReadOnlyDictionary<string, object?> AdditionalData)
{
    public static TraceStatusModel FromActivity(Activity activity) =>
        new(
            activity.Status.ToString(),
            activity.StatusDescription,
            TraceConversionHelpers.EmptyObjectDictionary);

    public static TraceStatusModel Ok() =>
        new("OK", null, TraceConversionHelpers.EmptyObjectDictionary);
}

/// <summary>
/// Serializable representation of an <see cref="ActivityEvent"/>.
/// </summary>
public sealed record EventModel(
    string Name,
    IReadOnlyDictionary<string, object?> Attributes,
    double? Timestamp,
    IReadOnlyDictionary<string, object?> AdditionalData)
{
    public static EventModel FromActivityEvent(in ActivityEvent activityEvent) =>
        new(
            activityEvent.Name,
            TraceConversionHelpers.BuildAttributes(activityEvent.Tags ?? Array.Empty<KeyValuePair<string, object?>>()),
            TraceConversionHelpers.ConvertTimestamp(activityEvent.Timestamp.UtcDateTime),
            TraceConversionHelpers.ExtractExtraFields(activityEvent, new[] { "Name", "Tags", "Timestamp", "Attributes" }));
}

/// <summary>
/// Serializable representation of an <see cref="ActivityLink"/>.
/// </summary>
public sealed record LinkModel(
    SpanContextModel Context,
    IReadOnlyDictionary<string, object?>? Attributes,
    IReadOnlyDictionary<string, object?> AdditionalData)
{
    public static LinkModel FromActivityLink(in ActivityLink link) =>
        new(
            SpanContextModel.FromActivityContext(link.Context),
            link.Tags is null ? null : TraceConversionHelpers.BuildAttributes(link.Tags),
            TraceConversionHelpers.ExtractExtraFields(link, new[] { "Context", "Tags" }));
}

/// <summary>
/// Serializable representation of an OpenTelemetry resource.
/// </summary>
public sealed record ResourceModel(
    IReadOnlyDictionary<string, object?> Attributes,
    string SchemaUrl,
    IReadOnlyDictionary<string, object?> AdditionalData)
{
    public static ResourceModel FromResource(Resource? resource)
    {
        if (resource is null)
        {
            return new ResourceModel(
                TraceConversionHelpers.EmptyObjectDictionary,
                string.Empty,
                TraceConversionHelpers.EmptyObjectDictionary);
        }

        var attributes = resource.Attributes?.ToDictionary(
            pair => pair.Key,
            pair => TraceConversionHelpers.NormalizeAttributeValue(pair.Value),
            StringComparer.Ordinal) ?? new Dictionary<string, object?>();

        var schemaUrl = resource.GetType().GetProperty("SchemaUrl", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(resource) as string ?? string.Empty;

        return new ResourceModel(
            new ReadOnlyDictionary<string, object?>(attributes),
            schemaUrl,
            TraceConversionHelpers.EmptyObjectDictionary);
    }
}

/// <summary>
/// Canonical span model used by ManagedCode Agent Lightning for persistence.
/// </summary>
public sealed record SpanModel(
    string RolloutId,
    string AttemptId,
    int SequenceId,
    string TraceId,
    string SpanId,
    string? ParentId,
    string Name,
    TraceStatusModel Status,
    IReadOnlyDictionary<string, object?> Attributes,
    IReadOnlyList<EventModel> Events,
    IReadOnlyList<LinkModel> Links,
    double? StartTime,
    double? EndTime,
    SpanContextModel? Context,
    SpanContextModel? Parent,
    ResourceModel Resource,
    IReadOnlyDictionary<string, object?> AdditionalData)
{
    public static SpanModel FromActivity(
        Activity activity,
        string rolloutId,
        string attemptId,
        int sequenceId,
        Resource? resource = null)
    {
        var context = activity.Context;
        SpanContextModel? parentContext = null;
        if (activity.ParentSpanId != default)
        {
            parentContext = new SpanContextModel(
                context.TraceId.ToHexString(),
                activity.ParentSpanId.ToHexString(),
                context.IsRemote,
                TraceConversionHelpers.EmptyStringDictionary,
                TraceConversionHelpers.EmptyObjectDictionary);
        }

        var parentId = activity.ParentSpanId != default
            ? activity.ParentSpanId.ToHexString()
            : null;

        var attributes = TraceConversionHelpers.BuildAttributes(activity.TagObjects ?? Array.Empty<KeyValuePair<string, object?>>());

        var events = activity.Events.Select(e => EventModel.FromActivityEvent(e)).ToList();
        var links = activity.Links.Select(link => LinkModel.FromActivityLink(link)).ToList();

        var startTime = TraceConversionHelpers.ConvertTimestamp(activity.StartTimeUtc);
        var endTime = activity.Duration > TimeSpan.Zero
            ? TraceConversionHelpers.ConvertTimestamp(activity.StartTimeUtc + activity.Duration)
            : null;

        return new SpanModel(
            rolloutId,
            attemptId,
            sequenceId,
            context.TraceId.ToHexString(),
            context.SpanId.ToHexString(),
            parentId,
            activity.DisplayName ?? activity.OperationName ?? SpanNames.Virtual.ToStringValue(),
            TraceStatusModel.FromActivity(activity),
            attributes,
            new ReadOnlyCollection<EventModel>(events),
            new ReadOnlyCollection<LinkModel>(links),
            startTime,
            endTime,
            SpanContextModel.FromActivityContext(context),
            parentContext,
            ResourceModel.FromResource(resource),
            TraceConversionHelpers.ExtractCustomProperties(activity));
    }

    public static SpanModel FromAttributes(
        IReadOnlyDictionary<string, object?> attributes,
        string? rolloutId = null,
        string? attemptId = null,
        int? sequenceId = null,
        string? name = null,
        string? traceId = null,
        string? spanId = null,
        string? parentId = null,
        double? startTime = null,
        double? endTime = null,
        ResourceModel? resource = null)
    {
        var generatedTraceId = traceId ?? ActivityTraceId.CreateRandom().ToHexString();
        var generatedSpanId = spanId ?? ActivitySpanId.CreateRandom().ToHexString();

        var context = new SpanContextModel(
            generatedTraceId,
            generatedSpanId,
            false,
            TraceConversionHelpers.EmptyStringDictionary,
            TraceConversionHelpers.EmptyObjectDictionary);

        SpanContextModel? parentContext = null;
        if (!string.IsNullOrWhiteSpace(parentId))
        {
            parentContext = new SpanContextModel(
                generatedTraceId,
                parentId,
                false,
                TraceConversionHelpers.EmptyStringDictionary,
                TraceConversionHelpers.EmptyObjectDictionary);
        }

        return new SpanModel(
            rolloutId ?? string.Empty,
            attemptId ?? string.Empty,
            sequenceId ?? 0,
            generatedTraceId,
            generatedSpanId,
            parentId,
            name ?? SpanNames.Virtual.ToStringValue(),
            TraceStatusModel.Ok(),
            attributes,
            Array.Empty<EventModel>(),
            Array.Empty<LinkModel>(),
            startTime,
            endTime,
            context,
            parentContext,
            resource ?? new ResourceModel(
                TraceConversionHelpers.EmptyObjectDictionary,
                string.Empty,
                TraceConversionHelpers.EmptyObjectDictionary),
            TraceConversionHelpers.EmptyObjectDictionary);
    }
}

public enum SpanNames
{
    Reward,
    Message,
    Object,
    Exception,
    Virtual,
}

public enum SpanAttributeNames
{
    Message,
    Object,
}

internal static class SpanEnumExtensions
{
    public static string ToStringValue(this SpanNames name) =>
        name switch
        {
            SpanNames.Reward => "agentlightning.reward",
            SpanNames.Message => "agentlightning.message",
            SpanNames.Object => "agentlightning.object",
            SpanNames.Exception => "agentlightning.exception",
            SpanNames.Virtual => "agentlightning.virtual",
            _ => name.ToString(),
        };

    public static string ToStringValue(this SpanAttributeNames name) =>
        name switch
        {
            SpanAttributeNames.Message => "message",
            SpanAttributeNames.Object => "object",
            _ => name.ToString(),
        };
}
