using System.Text.Json.Nodes;

namespace JapaneseLearning.User.Api.Common.Logging;

// Additive adapter: native Serilog fields remain available to the existing collector.
public static class CanonicalLogFields
{
    public static void Add(JsonObject record)
    {
        var properties = record["Properties"] as JsonObject ?? new JsonObject();
        string? Text(string key) => properties[key]?.ToString();

        void Copy(string target, string source)
        {
            if (properties[source] is { } value && value.ToString().Length > 0)
                record[target] = value.DeepClone();
        }

        record["@timestamp"] = DateTimeOffset.Parse(record["Timestamp"]!.ToString())
            .UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
        record["level"] = record["Level"]?.ToString() switch
        {
            "Warning" => "WARN",
            "Error" or "Fatal" => "ERROR",
            "Debug" or "Verbose" => "DEBUG",
            _ => "INFO"
        };
        record["service_name"] = "user-api";
        record["service_version"] = Bounded(Text("DeploymentVersion"), 128) ?? "unknown";
        record["service_instance_id"] = Bounded(Text("ServiceInstanceId"), 128) ?? "unknown";
        record["environment"] = Text("Environment")?.ToLowerInvariant() switch
        {
            "development" => "development",
            "test" or "testing" => "test",
            "staging" => "staging",
            "production" => "production",
            _ => "unknown"
        };

        var nativeEvent = Text("EventName");

        record["event_name"] = nativeEvent switch
        {
            "HttpRequestCompleted" => "http.request.completed",
            "ApplicationOperationCompleted" or "ApplicationOperationFailed" or
                "ApplicationOperationCancelled" => "operation.completed",
            "DependencyCompleted" => "dependency.completed",
            null => "log.message",
            _ => nativeEvent
        };

        Copy("operation", "OperationName");
        Copy("duration_ms", "ElapsedMilliseconds");
        Copy("http_status", "StatusCode");
        Copy("dependency_name", "DependencyName");
        Copy("dependency_type", "DependencyType");
        Copy("dependency_operation", "DependencyOperation");
        Copy("error_code", "ErrorCode");
        Copy("error_type", "ExceptionType");

        var correlation = Text("CorrelationId");

        if (ValidCorrelation(correlation))
            record["correlation_id"] = correlation;

        var trace = Text("TraceId");
        var span = Text("SpanId");

        if (ValidHex(trace, 32) && ValidHex(span, 16))
        {
            record["trace_id"] = trace;
            record["span_id"] = span;

            if (ValidHex(Text("ParentSpanId"), 16))
                Copy("parent_span_id", "ParentSpanId");
        }

        if (Text("RequestMethod") is { } method)
        {
            record["http_method"] = method is "GET" or "HEAD" or "POST" or "PUT" or
                "PATCH" or "DELETE" or "OPTIONS" ? method : "OTHER";

            var route = "/" + (Text("RequestRoute") ?? "unmatched").TrimStart('/');

            record["http_route"] = route;
            record["route_family"] = route.StartsWith("/api/auth", StringComparison.OrdinalIgnoreCase)
                ? "auth" : route.StartsWith("/health", StringComparison.Ordinal) ? "infrastructure" : "other";
        }

        var outcome = Text("Outcome");
        var status = properties["StatusCode"]?.GetValue<int>() ?? 0;

        if (outcome is not null)
            record["outcome"] = outcome is "cancelled" or "timeout" or "partial_failure"
                ? outcome : status >= 500 ? "server_error" : status >= 400 ? "client_error" : outcome;

        if (status >= 400 || outcome is "failure" or "cancelled" or "timeout")
        {
            record["error_category"] = Text("ErrorCategory") ?? (outcome == "cancelled" ? "cancellation"
                : outcome == "timeout" ? "timeout" : status == 401 ? "authentication"
                : status == 403 ? "authorization" : status == 400 ? "validation"
                : Text("ExceptionType")?.Contains("SqlException", StringComparison.Ordinal) == true ? "database"
                : Text("DependencyName") is not null ? "dependency" : status >= 500 ? "internal" : "unknown");
            record["error_type"] ??= "http";
        }
        // Schema assignment is an adapter version, not a producer or runtime acceptance stamp.
        record["schema_version"] = 1;
    }

    public static bool ValidCorrelation(string? value) => value is { Length: >= 1 and <= 64 }
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    private static bool ValidHex(string? value, int length) => value?.Length == length
        && value.Any(c => c != '0') && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string? Bounded(string? value, int length) => value is { Length: > 0 }
        && value.Length <= length && !value.Any(char.IsControl) ? value : null;
}
