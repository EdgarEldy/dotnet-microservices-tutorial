using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Identity.API.Tests.TestSupport;

/// <summary>What an armed <see cref="CommitGateInterceptor"/> does once the targeted transaction is about to commit.</summary>
public enum CommitAction
{
    /// <summary>Throws instead of committing: the transaction is rolled back.</summary>
    Fail,

    /// <summary>Holds the commit until <see cref="CommitScenario.Release"/> is completed.</summary>
    Hold,
}

/// <summary>Row counts read from inside the transaction, right before its commit.</summary>
public sealed record InTransactionSnapshot(int OutboxRows, int BusinessRows);

public sealed class CommitScenario(string marker, string businessRowsSql, CommitAction action)
{
    public string Marker { get; } = marker;

    public string BusinessRowsSql { get; } = businessRowsSql;

    public CommitAction Action { get; } = action;

    /// <summary>Completed when the targeted transaction reaches its commit, with what it contains.</summary>
    public TaskCompletionSource<InTransactionSnapshot> Reached { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completed by the test to let a held commit proceed.</summary>
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// Test-only EF Core interceptor registered on identity-api's AppDbContext. When armed, it waits
/// for the first transaction that, right before committing, contains an OutboxMessage whose body
/// mentions <see cref="CommitScenario.Marker"/> (an e-mail unique to one test). It then reads,
/// on that same connection and transaction, how many outbox rows and business rows it holds,
/// and either fails the commit or holds it. Every other transaction passes through untouched.
/// </summary>
public sealed class CommitGateInterceptor : DbTransactionInterceptor
{
    private static readonly TimeSpan MaxHold = TimeSpan.FromSeconds(60);

    private CommitScenario? armed;

    public CommitScenario Arm(string marker, string businessRowsSql, CommitAction action)
    {
        var scenario = new CommitScenario(marker, businessRowsSql, action);
        if (Interlocked.CompareExchange(ref armed, scenario, null) is not null)
        {
            throw new InvalidOperationException("Another commit scenario is already armed.");
        }

        return scenario;
    }

    /// <summary>Disarms <paramref name="scenario"/> if it never triggered, and releases it if it is holding.</summary>
    public void Disarm(CommitScenario scenario)
    {
        Interlocked.CompareExchange(ref armed, null, scenario);
        scenario.Release.TrySetResult();
    }

    public override async ValueTask<InterceptionResult> TransactionCommittingAsync(
        DbTransaction transaction,
        TransactionEventData eventData,
        InterceptionResult result,
        CancellationToken cancellationToken = default)
    {
        var scenario = Volatile.Read(ref armed);
        if (scenario is null)
        {
            return result;
        }

        var outboxRows = await CountAsync(transaction, Sql.OutboxRowsForEmail,scenario.Marker, cancellationToken);
        if (outboxRows == 0 || Interlocked.CompareExchange(ref armed, null, scenario) != scenario)
        {
            return result;
        }

        var businessRows = await CountAsync(transaction, scenario.BusinessRowsSql, scenario.Marker, cancellationToken);
        scenario.Reached.TrySetResult(new InTransactionSnapshot(outboxRows, businessRows));

        if (scenario.Action == CommitAction.Fail)
        {
            throw new InvalidOperationException("Simulated failure after Publish, before the transaction commits.");
        }

        await scenario.Release.Task.WaitAsync(MaxHold, cancellationToken);
        return result;
    }

    private static async Task<int> CountAsync(
        DbTransaction transaction, string sql, string marker, CancellationToken cancellationToken)
    {
        await using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;

        var parameter = command.CreateParameter();
        parameter.ParameterName = "marker";
        parameter.Value = marker;
        command.Parameters.Add(parameter);

        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }
}
