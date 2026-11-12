using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

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
            logEvent.Properties.Where(property => !PrivateProperty(property.Key))
                .Select(property => new LogEventProperty(property.Key, property.Value)),
            logEvent.TraceId ?? default, logEvent.SpanId ?? default);
       
        if (logEvent.Properties.TryGetValue("EventId", out var eventId) && eventId is StructureValue eventFields)
        {
            var name = eventFields.Properties.FirstOrDefault(property => property.Name == "Name");
            if (name is not null)
                safeEvent.AddOrUpdateProperty(new LogEventProperty("EventName", name.Value));
        }
       
        if (logEvent.Exception is { } exception)
        {
            safeEvent.AddOrUpdateProperty(new LogEventProperty("ExceptionType",
                new ScalarValue(exception.GetType().FullName)));
            safeEvent.AddOrUpdateProperty(new LogEventProperty("ExceptionDetails",
                ToProperty(JsonSerializer.SerializeToElement(SafeExceptionDetails.Create(exception)))));
        }
        using var native = new StringWriter();
        _formatter.Format(safeEvent, native);
        var record = JsonNode.Parse(native.ToString())!.AsObject();
        CanonicalLogFields.Add(record);
        output.Write(record.ToJsonString());
        output.WriteLine();
    }

    private static bool PrivateProperty(string key) => key.ToLowerInvariant() is
        "userid" or "email" or "password" or "accesstoken" or "refreshtoken" or
        "authorization" or "cookie" or "connectionstring" or "privatekey" or
        "peeraddress" or "peerport" or "payload" or "body" or "headers" or "request" or "response";

    private static LogEventPropertyValue ToProperty(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => new StructureValue(element.EnumerateObject()
            .Select(property => new LogEventProperty(property.Name, ToProperty(property.Value)))),
        JsonValueKind.Array => new SequenceValue(element.EnumerateArray().Select(ToProperty)),
        JsonValueKind.Number => new ScalarValue(element.GetInt64()),
        JsonValueKind.String => new ScalarValue(element.GetString()),
        JsonValueKind.True => new ScalarValue(true),
        JsonValueKind.False => new ScalarValue(false),
        _ => new ScalarValue(null)
    };
}
