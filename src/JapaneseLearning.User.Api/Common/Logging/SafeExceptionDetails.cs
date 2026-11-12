using Microsoft.Data.SqlClient;
using System.Diagnostics;

namespace JapaneseLearning.User.Api.Common.Logging;

/// <summary>Allowlisted diagnostics: never copies exception messages, Data, SQL text or file paths.</summary>
public sealed record SafeExceptionDetails(
    string ExceptionType,
    int HResult,
    string[] StackFrames,
    SqlFailure[] SqlErrors,
    SafeExceptionDetails[] Causes,
    bool Truncated = false)
{
    public static SafeExceptionDetails Create(Exception exception) => Create(exception, 0);

    private static SafeExceptionDetails Create(Exception exception, int depth)
    {
        var causes = exception is AggregateException aggregate
            ? aggregate.InnerExceptions.AsEnumerable()
            : exception.InnerException is { } inner ? new[] { inner } : [];
        var frames = new StackTrace(exception, false).GetFrames();
        var frameLimit = depth switch { 0 => 12, 1 => 6, _ => 2 };
        return new(
            exception.GetType().FullName ?? exception.GetType().Name,
            exception.HResult,
            frames.Take(frameLimit)
                .Select(frame => frame.GetMethod())
                .Select(method => $"{method?.DeclaringType?.FullName}.{method?.Name}").ToArray(),
            exception is SqlException sql
                ? sql.Errors.Cast<SqlError>().Take(16)
                    .Select(error => new SqlFailure(error.Number, error.State, error.Class, sql.ClientConnectionId))
                    .ToArray()
                : [],
            depth < 2 ? causes.Take(1).Select(cause => Create(cause, depth + 1)).ToArray() : [],
            frames.Length > frameLimit || causes.Skip(depth < 2 ? 1 : 0).Any());
    }
}

public sealed record SqlFailure(int Number, byte State, byte Class, Guid ClientConnectionId);