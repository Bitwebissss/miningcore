using System.Data;
using System.Text;
using MapsterMapper;
using Dapper;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Repositories;
using Npgsql;
using NpgsqlTypes;

namespace Miningcore.Persistence.Postgres.Repositories;

public class PaymentRepository : IPaymentRepository
{
    public PaymentRepository(IMapper mapper)
    {
        this.mapper = mapper;
    }

    private readonly IMapper mapper;

    public async Task InsertAsync(IDbConnection con, IDbTransaction tx, Payment payment)
    {
        var mapped = mapper.Map<Entities.Payment>(payment);

        const string query = @"INSERT INTO payments(poolid, coin, address, amount, transactionconfirmationdata, created)
            VALUES(@poolid, @coin, @address, @amount, @transactionconfirmationdata, @created)";

        await con.ExecuteAsync(query, mapped, tx);
    }

    public async Task BatchInsertAsync(IDbConnection con, IDbTransaction tx, IEnumerable<Payment> payments)
    {
        // NOTE: Even though the tx parameter is completely ignored here,
        // the COPY command still honors a current ambient transaction

        var pgCon = (NpgsqlConnection) con;

        const string query = @"COPY payments (poolid, coin, address, amount, transactionconfirmationdata, created) FROM STDIN (FORMAT BINARY)";

        await using(var writer = await pgCon.BeginBinaryImportAsync(query))
        {
            foreach(var payment in payments)
            {
                await writer.StartRowAsync();

                await writer.WriteAsync(payment.PoolId);
                await writer.WriteAsync(payment.Coin);
                await writer.WriteAsync(payment.Address);
                await writer.WriteAsync(payment.Amount, NpgsqlDbType.Numeric);
                await writer.WriteAsync(payment.TransactionConfirmationData);
                await writer.WriteAsync(payment.Created, NpgsqlDbType.Timestamp);
            }

            await writer.CompleteAsync();
        }
    }

    public async Task<Payment[]> PagePaymentsAsync(IDbConnection con, string poolId, string address, int page, int pageSize, CancellationToken ct)
    {
        var query = new StringBuilder("SELECT * FROM payments WHERE poolid = @poolid ");

        if(!string.IsNullOrEmpty(address))
            query.Append(" AND address = @address ");

        query.Append("ORDER BY created DESC OFFSET @offset FETCH NEXT @pageSize ROWS ONLY");

        return (await con.QueryAsync<Entities.Payment>(new CommandDefinition(query.ToString(),
                new { poolId, address, offset = page * pageSize, pageSize }, cancellationToken: ct)))
            .Select(mapper.Map<Payment>)
            .ToArray();
    }

    public Task<uint> GetPaymentsCountAsync(IDbConnection con, string poolId, string address, CancellationToken ct)
    {
        var query = new StringBuilder("SELECT COUNT(*) FROM payments WHERE poolid = @poolId");

        if(!string.IsNullOrEmpty(address))
            query.Append(" AND address = @address ");

        return con.ExecuteScalarAsync<uint>(new CommandDefinition(query.ToString(), new { poolId, address }, cancellationToken: ct));
    }

    public Task<DateTime?> GetLastPoolPaymentTimeAsync(IDbConnection con, string poolId, CancellationToken ct)
    {
        const string query = @"SELECT created FROM payments WHERE poolid = @poolId ORDER BY created DESC LIMIT 1";

        return con.ExecuteScalarAsync<DateTime?>(new CommandDefinition(query, new { poolId }, cancellationToken: ct));
    }

    public Task<uint> GetTotalPoolPaymentsCountAsync(IDbConnection con, string poolId, CancellationToken ct)
    {
        const string query = @"SELECT COUNT(*) FROM payments WHERE poolid = @poolId";

        return con.ExecuteScalarAsync<uint>(new CommandDefinition(query, new { poolId }, cancellationToken: ct));
    }
}
