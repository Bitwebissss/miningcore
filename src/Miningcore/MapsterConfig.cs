using Mapster;
using Miningcore.Configuration;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Model.Projections;
using Newtonsoft.Json.Linq;

namespace Miningcore;

public class MapsterConfig : IRegister
{
    public void Register(TypeAdapterConfig config)
    {
        config.NewConfig<JToken, JToken>().MapWith(x => x);

        // outgoing

        config.NewConfig<Blockchain.Share, Share>();

        config.NewConfig<Blockchain.Share, Block>()
            .Map(dest => dest.Reward, src => src.BlockReward)
            .Map(dest => dest.Hash,   src => src.BlockHash)
            .Map(dest => dest.Type,   src => src.BlockType)
            .Ignore(dest => dest.Status);

        config.NewConfig<BlockStatus, string>()
            .MapWith(e => e.ToString().ToLower());

        config.NewConfig<Mining.PoolStats, PoolStats>()
            .Ignore(dest => dest.PoolId)
            .Ignore(dest => dest.Created);

        config.NewConfig<Blockchain.BlockchainStats, PoolStats>()
            .Map(dest => dest.BlockHeight, src => (long) src.BlockHeight)
            .Ignore(dest => dest.PoolId)
            .Ignore(dest => dest.Created);

        // API
        config.NewConfig<CoinTemplate, Api.Responses.ApiCoinConfig>()
            .Map(dest => dest.Type,      src => src.Symbol)
            .Map(dest => dest.Family,    src => src.Family.ToString().ToLower())
            .Map(dest => dest.Symbol,    src => src.Symbol)
            .Map(dest => dest.Website,   src => src.Website)
            .Map(dest => dest.Market,    src => src.Market)
            .Map(dest => dest.Twitter,   src => src.Twitter)
            .Map(dest => dest.Discord,   src => src.Discord)
            .Map(dest => dest.Github,    src => src.Github)
            .Map(dest => dest.Telegram,  src => src.Telegram)
            .Map(dest => dest.Algorithm, src => src.GetAlgorithmName());

        config.NewConfig<PoolConfig, Api.Responses.PoolInfo>()
            .Map(dest => dest.Coin, src => src.Template);

        config.NewConfig<PoolStats, Api.Responses.PoolInfo>();
        config.NewConfig<PoolStats, Api.Responses.AggregatedPoolStats>();
        config.NewConfig<Block, Api.Responses.Block>();
        config.NewConfig<MinerSettings, Api.Responses.MinerSettings>();
        config.NewConfig<Payment, Api.Responses.Payment>();
        config.NewConfig<PoolPaymentProcessingConfig, Api.Responses.ApiPoolPaymentProcessingConfig>();

        config.NewConfig<MinerStats, Api.Responses.MinerStats>()
            .Ignore(dest => dest.LastPayment)
            .Ignore(dest => dest.LastPaymentLink);

        config.NewConfig<WorkerPerformanceStats, Api.Responses.WorkerPerformanceStats>();
        config.NewConfig<WorkerPerformanceStatsContainer, Api.Responses.WorkerPerformanceStatsContainer>();

        // PostgreSQL outgoing
        config.NewConfig<Share,                      Persistence.Postgres.Entities.Share>();
        config.NewConfig<Block,                      Persistence.Postgres.Entities.Block>();
        config.NewConfig<Balance,                    Persistence.Postgres.Entities.Balance>();
        config.NewConfig<Payment,                    Persistence.Postgres.Entities.Payment>();
        config.NewConfig<MinerSettings,              Persistence.Postgres.Entities.MinerSettings>();
        config.NewConfig<PoolStats,                  Persistence.Postgres.Entities.PoolStats>();

        config.NewConfig<MinerWorkerPerformanceStats, Persistence.Postgres.Entities.MinerWorkerPerformanceStats>()
            .Ignore(dest => dest.Id);

        // incoming

        // API
        config.NewConfig<Api.Responses.MinerSettings, MinerSettings>();

        // PostgreSQL incoming
        config.NewConfig<Persistence.Postgres.Entities.Share,                       Share>();
        config.NewConfig<Persistence.Postgres.Entities.Block,                       Block>();
        config.NewConfig<Persistence.Postgres.Entities.Balance,                     Balance>();
        config.NewConfig<Persistence.Postgres.Entities.Payment,                     Payment>();
        config.NewConfig<Persistence.Postgres.Entities.BalanceChange,               BalanceChange>();
        config.NewConfig<Persistence.Postgres.Entities.PoolStats,                   PoolStats>();
        config.NewConfig<Persistence.Postgres.Entities.MinerSettings,               MinerSettings>();
        config.NewConfig<Persistence.Postgres.Entities.MinerWorkerPerformanceStats, MinerWorkerPerformanceStats>();

        config.NewConfig<PoolStats,                  Mining.PoolStats>();
        config.NewConfig<Blockchain.BlockchainStats, Mining.PoolStats>();

        config.NewConfig<PoolStats, Blockchain.BlockchainStats>()
            .Ignore(dest => dest.RewardType)
            .Ignore(dest => dest.NetworkType);
    }
}
