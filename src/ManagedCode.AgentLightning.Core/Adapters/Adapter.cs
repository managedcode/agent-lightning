using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace ManagedCode.AgentLightning.Core.Adapters;

/// <summary>
/// Base class for synchronous adapters that convert data from one format to another.
/// </summary>
/// <typeparam name="TFrom">Source data type.</typeparam>
/// <typeparam name="TTo">Target data type.</typeparam>
public abstract class Adapter<TFrom, TTo>
{
    /// <summary>
    /// Converts the input to the target format.
    /// </summary>
    public TTo Adapt(TFrom source) => Convert(source);

    /// <summary>
    /// When overridden in a derived type, performs the conversion.
    /// </summary>
    protected abstract TTo Convert(TFrom source);
}

/// <summary>
/// Adapter base for OpenTelemetry activities.
/// </summary>
/// <typeparam name="TTo">Target type produced by the adapter.</typeparam>
public abstract class OtelTraceAdapter<TTo> : Adapter<IReadOnlyList<Activity>, TTo>
{
    protected sealed override TTo Convert(IReadOnlyList<Activity> source) =>
        ConvertCore(source ?? Array.Empty<Activity>());

    protected abstract TTo ConvertCore(IReadOnlyList<Activity> source);
}

/// <summary>
/// Adapter base for converting serialized span models.
/// </summary>
/// <typeparam name="TTo">Target type produced by the adapter.</typeparam>
public abstract class TraceAdapter<TTo> : Adapter<IReadOnlyList<Tracing.SpanModel>, TTo>
{
    protected sealed override TTo Convert(IReadOnlyList<Tracing.SpanModel> source) =>
        ConvertCore(source ?? Array.Empty<Tracing.SpanModel>());

    protected abstract TTo ConvertCore(IReadOnlyList<Tracing.SpanModel> source);
}
