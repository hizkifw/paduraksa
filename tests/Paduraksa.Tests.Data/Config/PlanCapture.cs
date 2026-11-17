using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Paduraksa.Data;

namespace Paduraksa.Tests.Data.Config;

/// <summary>Records each statement EF runs and the plan SQLite makes for it, asked on the same connection with the same parameters.</summary>
internal sealed class PlanCapture : DbCommandInterceptor
{
    private readonly Lock _lock = new();
    private readonly List<(string Sql, string Plan)> _statements = [];

    public List<(string Sql, string Plan)> Statements
    {
        get
        {
            lock (_lock)
            {
                return [.. _statements];
            }
        }
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Record(command);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Record(command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Record(command);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Record(command);
        return ValueTask.FromResult(result);
    }

    private void Record(DbCommand command)
    {
        using var explain = command.Connection!.CreateCommand();
        explain.CommandText = "EXPLAIN QUERY PLAN " + command.CommandText;
        foreach (DbParameter parameter in command.Parameters)
        {
            var copy = explain.CreateParameter();
            copy.ParameterName = parameter.ParameterName;
            copy.Value = parameter.Value;
            explain.Parameters.Add(copy);
        }

        List<string> lines = [];
        using (var reader = explain.ExecuteReader())
        {
            while (reader.Read())
            {
                lines.Add(reader.GetString(reader.FieldCount - 1));
            }
        }

        lock (_lock)
        {
            _statements.Add((command.CommandText, string.Join("\n", lines)));
        }
    }
}

/// <summary>Hands out contexts that report to a <see cref="PlanCapture"/>, for code that takes a factory.</summary>
internal sealed class CapturingFactory(TestConfigDatabase db, PlanCapture capture) : IDbContextFactory<ConfigDbContext>
{
    public ConfigDbContext CreateDbContext() => db.NewContext(capture);
}
