using System.Diagnostics;
using System.Diagnostics.Tracing;
using Prometheus;

namespace JapaneseLearning.User.Api.Observability;

// Observe provider events only. Never inspect command, connection, exception or SQL payloads.
public sealed class SqlClientMetrics : IDisposable, IObserver<DiagnosticListener>,
    IObserver<KeyValuePair<string, object?>>
{
    private readonly Counter _operations;
    private readonly Gauge _connections;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly IDisposable _listeners;
    private readonly PoolListener _poolListener;
    private bool _disposed;

    public SqlClientMetrics(MetricFactory factory)
    {
        _operations = factory.CreateCounter("app_database_operations_total", "SqlClient terminal operations; no SQL or endpoint labels.",
            ["dependency", "operation", "outcome"]);
        _connections = factory.CreateGauge("app_database_pool_connections", "Process-wide SqlClient connection counts; emitted only after a provider sample.",
            ["state"]);
        _listeners = DiagnosticListener.AllListeners.Subscribe(this);
        _poolListener = new PoolListener(ObservePoolCounter);
    }

    public void OnNext(DiagnosticListener listener)
    {
        if (listener.Name != "SqlClientDiagnosticListener")
            return;
        lock (_subscriptions)
        {
            if (!_disposed)
                _subscriptions.Add(listener.Subscribe(this, name => name is
                    "Microsoft.Data.SqlClient.WriteCommandAfter" or "Microsoft.Data.SqlClient.WriteCommandError"
                    or "Microsoft.Data.SqlClient.WriteConnectionOpenAfter" or "Microsoft.Data.SqlClient.WriteConnectionOpenError"));
        }
    }

    public void OnNext(KeyValuePair<string, object?> item)
    {
        var operation = item.Key switch
        {
            "Microsoft.Data.SqlClient.WriteCommandAfter" or "Microsoft.Data.SqlClient.WriteCommandError" => "command",
            "Microsoft.Data.SqlClient.WriteConnectionOpenAfter" or "Microsoft.Data.SqlClient.WriteConnectionOpenError" => "connect",
            _ => null
        };
        if (operation is not null)
            _operations.WithLabels("sqlserver", operation, item.Key.EndsWith("Error", StringComparison.Ordinal) ? "failure" : "success").Inc();
    }

    public void ObservePoolCounter(string name, double value)
    {
        var state = name switch
        {
            "number-of-active-connections" => "active",
            "number-of-free-connections" => "idle",
            "number-of-pooled-connections" => "pooled",
            _ => null
        };
        if (state is not null && double.IsFinite(value) && value >= 0)
            _connections.WithLabels(state).Set(value);
    }

    public void OnCompleted() { }
    public void OnError(Exception error) { }

    public void Dispose()
    {
        _listeners.Dispose();
        _poolListener.Dispose();
        lock (_subscriptions)
        {
            _disposed = true;
            foreach (var subscription in _subscriptions)
                subscription.Dispose();
            _subscriptions.Clear();
        }
    }

    private sealed class PoolListener : EventListener
    {
        private Action<string, double>? _observe;

        public PoolListener(Action<string, double> observe)
        {
            _observe = observe;
            foreach (var source in EventSource.GetSources())
                OnEventSourceCreated(source);
        }

        protected override void OnEventSourceCreated(EventSource source)
        {
            if (_observe is not null && source.Name == "Microsoft.Data.SqlClient.EventSource")
                EnableEvents(source, EventLevel.Informational, EventKeywords.None,
                    new Dictionary<string, string?> { ["EventCounterIntervalSec"] = "15" });
        }

        protected override void OnEventWritten(EventWrittenEventArgs item)
        {
            if (item.EventName != "EventCounters" || item.Payload?.Count != 1
                || item.Payload[0] is not IDictionary<string, object> payload)
                return;
            if (payload.TryGetValue("Name", out var name) && name is string counter
                && payload.TryGetValue("Mean", out var mean) && mean is double value)
                _observe?.Invoke(counter, value);
        }
    }
}
