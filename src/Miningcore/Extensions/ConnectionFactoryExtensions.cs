using System.Data;
using Miningcore.Persistence;
using Npgsql;

namespace Miningcore.Extensions;

public static class ConnectionFactoryExtensions
{
    /// <summary>
    /// Run the specified action providing it with a fresh connection returning its result.
    /// </summary>
    public static async Task Run(this IConnectionFactory factory,
        Func<IDbConnection, Task> action)
    {
        await using var con = (NpgsqlConnection) await factory.OpenConnectionAsync();
        await action(con);
    }

    /// <summary>
    /// Run the specified action providing it with a fresh connection returning its result.
    /// </summary>
    public static async Task<T> Run<T>(this IConnectionFactory factory,
        Func<IDbConnection, Task<T>> action)
    {
        await using var con = (NpgsqlConnection) await factory.OpenConnectionAsync();
        return await action(con);
    }

    /// <summary>
    /// Run the specified action inside a transaction. If the action throws an exception,
    /// the transaction is rolled back. Otherwise it is committed.
    /// </summary>
    public static async Task RunTx(this IConnectionFactory factory,
        Func<IDbConnection, IDbTransaction, Task> action,
        bool autoCommit = true, IsolationLevel isolation = IsolationLevel.ReadCommitted)
    {
        await using var con = (NpgsqlConnection) await factory.OpenConnectionAsync();
        await using var tx  = await con.BeginTransactionAsync(isolation);

        try
        {
            await action(con, tx);

            if(autoCommit)
                await tx.CommitAsync();
        }

        catch
        {
            try { await tx.RollbackAsync(); } catch { /* best-effort */ }
            throw;
        }
    }

    /// <summary>
    /// Run the specified action inside a transaction. If the action throws an exception,
    /// the transaction is rolled back. Otherwise it is committed.
    /// </summary>
    public static async Task<T> RunTx<T>(this IConnectionFactory factory,
        Func<IDbConnection, IDbTransaction, Task<T>> func,
        bool autoCommit = true, IsolationLevel isolation = IsolationLevel.ReadCommitted)
    {
        await using var con = (NpgsqlConnection) await factory.OpenConnectionAsync();
        await using var tx  = await con.BeginTransactionAsync(isolation);

        try
        {
            var result = await func(con, tx);

            if(autoCommit)
                await tx.CommitAsync();

            return result;
        }

        catch
        {
            try { await tx.RollbackAsync(); } catch { /* best-effort */ }
            throw;
        }
    }
}
