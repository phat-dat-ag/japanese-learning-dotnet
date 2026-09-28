using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using JapaneseLearning.User.Api.Common.Errors;
using JapaneseLearning.User.Api.Common.Logging;
using JapaneseLearning.User.Application.Auth.Login;
using JapaneseLearning.User.Application.Common.Behaviors;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace JapaneseLearning.User.UnitTests.Logging;

public sealed class SerilogLoggingTests
{
    [Fact]
    public async Task RequestAndFailureShareActivityAndCorrelationWithoutCredentials()
    {
        var sink = new CaptureSink();
        using var serilog = new LoggerConfiguration().Enrich.FromLogContext().WriteTo.Sink(sink).CreateLogger();
        using var factory = LoggerFactory.Create(builder => builder.AddSerilog(serilog));
        using var activity = new Activity("request").SetIdFormat(ActivityIdFormat.W3C).Start();
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/api/auth/login";
        context.Request.QueryString = new QueryString("?password=credential-sentinel");
        context.Request.Headers["Authorization"] = "Bearer credential-sentinel";
        context.Request.Headers["Cookie"] = "credential-sentinel";
        context.Request.Headers[CorrelationIdMiddleware.HeaderName] = "request-123";
        context.Response.Body = new MemoryStream();
        var handler = new GlobalExceptionHandler(factory.CreateLogger<GlobalExceptionHandler>());
        var middleware = new CorrelationIdMiddleware(async http =>
        {
            factory.CreateLogger("JapaneseLearning.User.Application").LogInformation("Inside operation");
            try { throw new InvalidOperationException("credential-sentinel"); }
            catch (Exception exception) { await handler.TryHandleAsync(http, exception, default); }
        }, factory.CreateLogger<CorrelationIdMiddleware>());

        await middleware.InvokeAsync(context);

        Assert.Equal(500, context.Response.StatusCode);
        Assert.Equal("request-123", context.TraceIdentifier);
        Assert.Equal(3, sink.Events.Count);
        foreach (var entry in sink.Events)
        {
            Assert.Equal("request-123", ((ScalarValue)entry.Properties["CorrelationId"]).Value);
            Assert.Equal(activity.TraceId.ToString(), ((ScalarValue)entry.Properties["TraceId"]).Value);
            Assert.Equal(activity.SpanId.ToString(), ((ScalarValue)entry.Properties["SpanId"]).Value);
            Assert.DoesNotContain("credential-sentinel", Format(entry));
        }
        var completed = Assert.Single(sink.Events, entry => entry.Properties.ContainsKey("StatusCode"));
        Assert.Equal(500, ((ScalarValue)completed.Properties["StatusCode"]).Value);
        Assert.Equal("/api/auth/login", ((ScalarValue)completed.Properties["RequestPath"]).Value);
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        Assert.DoesNotContain("credential-sentinel", body);
        Assert.Contains("INTERNAL_SERVER_ERROR", body);
    }

    [Fact]
    public void SqlDiagnosticsPreserve18456AndExcludeMessagesAndData()
    {
        // SqlClient has no public exception factory. Keep its test-only reflection isolated here.
        var sql = CreateSqlException();
        sql.Data["ConnectionString"] = "Password=credential-sentinel";
        var details = SafeExceptionDetails.Create(new InvalidOperationException("credential-sentinel", sql));
        var failure = Assert.Single(Assert.Single(details.Causes).SqlErrors);
        Assert.Equal(18456, failure.Number);
        Assert.Equal((byte)1, failure.State);
        Assert.Equal((byte)14, failure.Class);
        Assert.Equal(sql.ClientConnectionId, failure.ClientConnectionId);
        Assert.DoesNotContain("credential-sentinel", JsonSerializer.Serialize(details));

        var sink = new CaptureSink();
        using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        logger.Error(sql, "Database operation failed");
        var line = Format(Assert.Single(sink.Events));
        using var document = JsonDocument.Parse(line);
        Assert.Equal(18456, document.RootElement.GetProperty("Properties")
            .GetProperty("ExceptionDetails").GetProperty("SqlErrors")[0].GetProperty("Number").GetInt32());
        Assert.DoesNotContain("credential-sentinel", line);
        Assert.Single(line.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task MediatRLoggingNeverSerializesCommandOrResponse()
    {
        var sink = new CaptureSink();
        using var serilog = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        using var factory = LoggerFactory.Create(builder => builder.AddSerilog(serilog));
        var behavior = new LoggingBehavior<LoginCommand, string>(
            factory.CreateLogger<LoggingBehavior<LoginCommand, string>>());
        var result = await behavior.Handle(new LoginCommand("credential-sentinel", "credential-sentinel"),
            _ => Task.FromResult("credential-sentinel"), default);
        Assert.Equal("credential-sentinel", result);
        Assert.Equal(2, sink.Events.Count);
        Assert.All(sink.Events, entry => Assert.DoesNotContain("credential-sentinel", Format(entry)));
    }

    [Fact]
    public void FilesAreJsonRetainedByAgeAndCountAndRolledBySize()
    {
        var directory = Path.Combine(Path.GetTempPath(), "user-logging-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var expired = Path.Combine(directory, $"user-api-{DateTime.Today.AddDays(-32):yyyyMMdd}.json");
            File.WriteAllText(expired, "{}");
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LogFiles:Directory"] = directory,
                ["LogFiles:RetentionDays"] = "30",
                ["LogFiles:FileSizeLimitBytes"] = "1024",
                ["LogFiles:RetainedFileCountLimit"] = "3",
                ["Serilog:MinimumLevel:Default"] = "Information",
                ["Serilog:MinimumLevel:Override:Microsoft"] = "Warning"
            }).Build();
            var environment = Mock.Of<IHostEnvironment>(value =>
                value.ApplicationName == "JapaneseLearning.User.Api" && value.EnvironmentName == "Production");
            using (var logger = new LoggerConfiguration().ConfigureService(configuration, environment).CreateLogger())
            {
                logger.ForContext("SourceContext", "Microsoft.AspNetCore.Hosting.Diagnostics")
                    .Error("credential-sentinel");
                logger.ForContext("SourceContext", "Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware")
                    .Error("credential-sentinel");
                logger.ForContext("SourceContext", "Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerHandler")
                    .Error("credential-sentinel");
                logger.ForContext("SourceContext", "Microsoft.Framework").Information("credential-sentinel");
                logger.Information("First event applies age retention independently of the count limit");
                Assert.False(File.Exists(expired));
                for (var index = 0; index < 20; index++)
                    logger.Information("Rolling test {Index} {Padding}", index, new string('x', 250));
            }
            Assert.False(File.Exists(expired));
            var files = Directory.GetFiles(directory, "*.json");
            Assert.Equal(3, files.Length);
            Assert.Contains(files, path => Path.GetFileNameWithoutExtension(path).Contains('_'));
            foreach (var line in files.SelectMany(File.ReadAllLines))
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                Assert.True(root.TryGetProperty("Timestamp", out _));
                Assert.Equal("Information", root.GetProperty("Level").GetString());
                Assert.Equal("Production", root.GetProperty("Properties").GetProperty("Environment").GetString());
                Assert.Equal("JapaneseLearning.User.Api", root.GetProperty("Properties").GetProperty("ServiceName").GetString());
                Assert.DoesNotContain("credential-sentinel", line);
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static SqlException CreateSqlException()
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var constructor = typeof(SqlError).GetConstructors(flags)
            .Single(value => value.GetParameters().Length == 8);
        var error = (SqlError)constructor.Invoke([18456, (byte)1, (byte)14, "server",
            "credential-sentinel", "procedure", 1, null]);
        var errors = (SqlErrorCollection)Activator.CreateInstance(typeof(SqlErrorCollection), true)!;
        typeof(SqlErrorCollection).GetMethod("Add", flags)!.Invoke(errors, [error]);
        var factory = typeof(SqlException).GetMethod("CreateException", BindingFlags.NonPublic | BindingFlags.Static,
            null, [typeof(SqlErrorCollection), typeof(string), typeof(Guid), typeof(Exception)], null)!;
        return (SqlException)factory.Invoke(null, [errors, "test", Guid.NewGuid(), null])!;
    }

    private static string Format(LogEvent entry)
    {
        using var output = new StringWriter();
        new SafeJsonFormatter().Format(entry, output);
        return output.ToString();
    }

    private sealed class CaptureSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}