using System.Text.Json.Serialization;
using Miningcore.Blockchain;
using Miningcore.Configuration;
using Miningcore.Mining;

namespace Miningcore.Api.Responses;

public class ApiCoinConfig
{
    public string Type { get; set; }
    public string Name { get; set; }
    public string Symbol { get; set; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Website { get; set; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Market { get; set; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Family { get; set; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Algorithm { get; set; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Twitter { get; set; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Discord { get; set; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Github{ get; set; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Telegram { get; set; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string CanonicalName { get; set; }
}

public class ApiPoolPayoutSchemeConfig
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? Factor { get; set; } = 2.0m;

    [System.Text.Json.Serialization.JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? BlockFinderPercentage { get; set; } = 5.0m;
}

public class ApiPoolPaymentProcessingConfig
{
    public bool Enabled { get; set; }
    public decimal MinimumPayment { get; set; } // in pool-base-currency (ie. Bitcoin, not Satoshis)
    public string PayoutScheme { get; set; }
    public ApiPoolPayoutSchemeConfig PayoutSchemeConfig { get; set; }
    public double PaymentIntervalSeconds { get; set; }

    [Newtonsoft.Json.JsonExtensionData]
    public IDictionary<string, object> Extra { get; set; }
}

public class PoolInfo
{
    // Configuration Properties directly mapping to PoolConfig (omitting security relevant fields)
    public string Id { get; set; }

    public ApiCoinConfig Coin { get; set; }
    public Dictionary<int, PoolEndpoint> Ports { get; set; }
    public ApiPoolPaymentProcessingConfig PaymentProcessing { get; set; }
    public PoolShareBasedBanningConfig ShareBasedBanning { get; set; }
    public int ClientConnectionTimeout { get; set; }
    public int JobRebroadcastTimeout { get; set; }
    public int BlockRefreshInterval { get; set; }
    public float PoolFeePercent { get; set; }
    public string Address { get; set; }
    public string AddressInfoLink { get; set; }

    // Hostnames where this pool's stratum ports are reachable (separate from the API domain).
    // Purely informational passthrough from config — used by the frontend's miner-setup tab.
    [System.Text.Json.Serialization.JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[] MiningDomains { get; set; }

    // Stats
    public PoolStats PoolStats { get; set; }

    public BlockchainStats NetworkStats { get; set; }
    public decimal TotalPaid { get; set; }
    public uint TotalPaymentsCount { get; set; }
    public uint TotalBlocks { get; set; }
    public uint Blocks24h { get; set; }
    public DateTime? LastPoolBlockTime { get; set; }
    public DateTime? LastPaymentTime { get; set; }
    public double PoolEffort { get; set; }
    public uint TotalConfirmedBlocks { get; set; }
    public uint TotalPendingBlocks { get; set; }
    public uint TotalOrphanedBlocks { get; set; }
    public decimal BlockReward { get; set; }
    public int WorkersOnline { get; set; }
    public int WorkersOffline { get; set; }
}

// Minimal pool entry returned by GET /api/pools.
// Frontend uses only id + coin.name/coin.symbol to build the pool selector.
// Full stats live at GET /api/pools/{id}.
public class PoolListItem
{
    public string Id { get; set; }
    public ApiCoinConfig Coin { get; set; }
}

public class GetPoolsResponse
{
    public PoolListItem[] Pools { get; set; }
}
