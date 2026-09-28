using Serilog;

namespace JapaneseLearning.User.Api.Common.Logging;

public static class SerilogConfiguration
{
    public static LoggerConfiguration ConfigureService(
        this LoggerConfiguration logger, IConfiguration configuration, IHostEnvironment environment)
    {
        var file = configuration.GetSection("LogFiles");
        var directory = file.GetValue<string>("Directory") ?? "logs";
        var retentionDays = file.GetValue<int>("RetentionDays", 30);
        var sizeLimit = file.GetValue<long>("FileSizeLimitBytes", 104857600);
        var countLimit = file.GetValue<int>("RetainedFileCountLimit", 300);
        if (string.IsNullOrWhiteSpace(directory) || retentionDays <= 0 || sizeLimit <= 0 || countLimit <= 0)
            throw new InvalidOperationException("LogFiles requires a directory and positive retention/size limits.");

        return logger
            .ReadFrom.Configuration(configuration)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("ServiceName", environment.ApplicationName)
            .Enrich.WithProperty("Environment", environment.EnvironmentName)
            // Keep one request event and prevent framework dumps of raw exception messages.
            .Filter.ByExcluding(log => log.Properties.TryGetValue("SourceContext", out var source) &&
                source is Serilog.Events.ScalarValue { Value: string category } &&
                (category.StartsWith("Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware", StringComparison.Ordinal) ||
                 category.StartsWith("Microsoft.AspNetCore.Hosting.Diagnostics", StringComparison.Ordinal) ||
                 category.StartsWith("Microsoft.AspNetCore.Authentication.JwtBearer", StringComparison.Ordinal)))
            .WriteTo.Console(new SafeJsonFormatter())
            .WriteTo.File(new SafeJsonFormatter(),
                Path.Combine(directory, "user-api-.json"),
                rollingInterval: RollingInterval.Day,
                retainedFileTimeLimit: TimeSpan.FromDays(retentionDays),
                retainedFileCountLimit: countLimit,
                fileSizeLimitBytes: sizeLimit,
                rollOnFileSizeLimit: true,
                buffered: false);
    }
}