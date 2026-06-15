using System.Data;
using Miningcore.Persistence.Model;

namespace Miningcore.Persistence.Repositories;

public interface IPaymentRepository
{
    Task InsertAsync(IDbConnection con, IDbTransaction tx, Payment payment);
    Task BatchInsertAsync(IDbConnection con, IDbTransaction tx, IEnumerable<Payment> shares);

    Task<Payment[]> PagePaymentsAsync(IDbConnection con, string poolId, string address, int page, int pageSize, CancellationToken ct);
    Task<uint> GetPaymentsCountAsync(IDbConnection con, string poolId, string address, CancellationToken ct);
    Task<DateTime?> GetLastPoolPaymentTimeAsync(IDbConnection con, string poolId, CancellationToken ct);
    Task<uint> GetTotalPoolPaymentsCountAsync(IDbConnection con, string poolId, CancellationToken ct);
}
