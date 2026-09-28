using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Json;
using System.Text.Json;

namespace JapaneseLearning.User.Api.Common.Logging;

/// <summary>One JSON object per line, including safe diagnostics for provider-level exceptions.</summary>
public sealed class SafeJsonFormatter : ITextFormatter
{
    private readonly JsonFormatter _formatter = new(renderMessage: true);

    public void Format(LogEvent logEvent, TextWriter output)
    {
        // Framework/provider exceptions follow the same policy as application exceptions.
        var safeEvent = new LogEvent(logEvent.Timestamp, logEvent.Level, null,
            logEvent.MessageTemplate,
            logEvent.Properties.Select(property => new LogEventProperty(property.Key, property.Value)),
            logEvent.TraceId ?? default, logEvent.SpanId ?? default);
        if (logEvent.Exception is { } exception)
        {
            safeEvent.AddOrUpdateProperty(new LogEventProperty("ExceptionType",
                new ScalarValue(exception.GetType().FullName)));
            safeEvent.AddOrUpdateProperty(new LogEventProperty("ExceptionDetails",
                ToProperty(JsonSerializer.SerializeToElement(SafeExceptionDetails.Create(exception)))));
        }
        _formatter.Format(safeEvent, output);
    }

    private static LogEventPropertyValue ToProperty(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => new StructureValue(element.EnumerateObject()
            .Select(property => new LogEventProperty(property.Name, ToProperty(property.Value)))),
        JsonValueKind.Array => new SequenceValue(element.EnumerateArray().Select(ToProperty)),
        JsonValueKind.Number => new ScalarValue(element.GetInt64()),
        JsonValueKind.String => new ScalarValue(element.GetString()),
        _ => new ScalarValue(null)
    };
}