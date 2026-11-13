using System.Diagnostics;

namespace JapaneseLearning.User.Api.Observability;

public sealed class TraceContextPropagation : DistributedContextPropagator
{
    public override IReadOnlyCollection<string> Fields { get; } = new[] { "traceparent" };

    public override void Inject(Activity? activity, object? carrier, PropagatorSetterCallback? setter)
    {
        if (activity?.IdFormat == ActivityIdFormat.W3C && activity.Id is { } parent)
            setter?.Invoke(carrier, "traceparent", parent);
    }

    public override void ExtractTraceIdAndState(object? carrier, PropagatorGetterCallback? getter,
        out string? traceId, out string? traceState)
    {
        traceId = null;
        traceState = null; // No vendor tracestate is approved; baggage is also excluded.

        if (getter is null)
            return;

        getter(carrier, "traceparent", out var single, out var multiple);

        var values = multiple?.Take(2).ToArray();

        if (values is { Length: > 1 })
            return;

        var parent = values is { Length: 1 } ? values[0] : single;

        if (ActivityContext.TryParse(parent, null, true, out _))
            traceId = parent;
    }

    public override IEnumerable<KeyValuePair<string, string?>>? ExtractBaggage(
        object? carrier, PropagatorGetterCallback? getter) => null;
}
