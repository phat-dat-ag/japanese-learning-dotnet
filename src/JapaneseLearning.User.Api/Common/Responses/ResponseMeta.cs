using System.Diagnostics;
using JapaneseLearning.User.Api.Common.Logging;

namespace JapaneseLearning.User.Api.Common.Responses;

public sealed record ResponseMeta(DateTime Timestamp, string TraceId, string CorrelationId)
{
    public static ResponseMeta Create(HttpContext context) => new(
        DateTime.UtcNow,
        context.Items[CorrelationIdMiddleware.TraceIdKey] as string
            ?? (Activity.Current?.TraceId is { } trace && trace != default
                ? trace.ToString() : ActivityTraceId.CreateRandom().ToString()),
        context.TraceIdentifier);
}
