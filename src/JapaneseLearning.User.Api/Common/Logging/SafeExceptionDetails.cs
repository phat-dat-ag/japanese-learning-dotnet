using Microsoft.Data.SqlClient;
using System.Diagnostics;

namespace JapaneseLearning.User.Api.Common.Logging;

/// <summary>Allowlisted diagnostics: never copies exception messages, Data, SQL text or file paths.</summary>
public sealed record SafeExceptionDetails(
    string ExceptionType,
    int HResult,
    string[] StackFrames,
    SqlFailure[] SqlErrors,
    SafeExceptionDetails[] Causes)
{
    public static SafeExceptionDetails Create(Exception exception) => Create(exception, 0);

    private static SafeExceptionDetails Create(Exception exception, int depth)
    {
        var causes = exception is AggregateException aggregate
            ? aggregate.InnerExceptions.AsEnumerable()
            : exception.InnerException is { } inner ? new[] { inner } : [];
        return new(
            exception.GetType().FullName ?? exception.GetType().Name,
            exception.HResult,
            new StackTrace(exception, false).GetFrames().Take(64)
                .Select(frame => frame.GetMethod())
                .Select(method => $"{method?.DeclaringType?.FullName}.{method?.Name}").ToArray(),
            exception is SqlException sql
                ? sql.Errors.Cast<SqlError>().Take(16)
                    .Select(error => new SqlFailure(error.Number, error.State, error.Class, sql.ClientConnectionId))
                    .ToArray()
                : [],
            depth < 8 ? causes.Take(8).Select(cause => Create(cause, depth + 1)).ToArray() : []);
    }
}

public sealed record SqlFailure(int Number, byte State, byte Class, Guid ClientConnectionId);