using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Runtime.Serialization;
using AspNetCoreRateLimit;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
// ReSharper disable UnusedAutoPropertyAccessor.Global
// ReSharper disable PropertyCanBeMadeInitOnly.Global
// ReSharper disable ClassNeverInstantiated.Global
// ReSharper disable InconsistentNaming

namespace Miningcore.Configuration;

#region Coin Definitions

public enum CoinFamily
{
    [EnumMember(Value = "bitcoin")]
    Bitcoin,
}

public abstract partial class CoinTemplate
{
    /// <summary>
    /// Name
    /// </summary>
    [JsonProperty(Order = -10)]
    public string Name { get; set; }

    /// <summary>
    /// Canonical Name
    /// </summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string CanonicalName { get; set; }

    /// <summary>
    /// Trade Symbol
    /// </summary>
    [JsonProperty(Order = -9)]
    public string Symbol { get; set; }

    /// <summary>
    /// Website
    /// </summary>
    [JsonProperty(Order = -9)]
    public string Website { get; set; }

    /// <summary>
    /// Market
    /// </summary>
    [JsonProperty(Order = -9)]
    public string Market { get; set; }

    /// <summary>
    /// Family
    /// </summary>
    [JsonConverter(typeof(StringEnumConverter), true)]
    [JsonProperty(Order = -8)]
    public CoinFamily Family { get; set; }

    /// <summary>
    /// Dictionary mapping block type to a block explorer Url
    /// Supported placeholders: $height$ and $hash$
    /// </summary>
    public Dictionary<string, string> ExplorerBlockLinks { get; set; }

    /// <summary>
    /// Block explorer URL for transactions
    /// Can be alternatively used to define the url for the default Block type
    /// Supported placeholders: $height$ and $hash$
    /// </summary>
    public string ExplorerBlockLink { get; set; }

    /// <summary>
    /// Block explorer URL for transactions
    /// Supported placeholders: {0}
    /// </summary>
    public string ExplorerTxLink { get; set; }

    /// <summary>
    /// Block explorer URL for accounts
    /// Supported placeholders: {0}
    /// </summary>
    public string ExplorerAccountLink { get; set; }

    /// <summary>
    /// Twitter Link
    /// </summary>
    [JsonProperty(Order = -9)]
    public string Twitter { get; set; }

    /// <summary>
    /// Discord Link
    /// </summary>
    [JsonProperty(Order = -9)]
    public string Discord { get; set; }

    /// <summary>
    /// GitHub Repository Link
    /// </summary>
    [JsonProperty(Order = -9)]
    public string Github { get; set; }

    /// <summary>
    /// Telegram Group Link
    /// </summary>
    [JsonProperty(Order = -9)]
    public string Telegram { get; set; }

    /// <summary>
    /// Arbitrary extension data
    /// </summary>
    [JsonExtensionData]
    public IDictionary<string, object> Extra { get; set; }

    /// <summary>
    /// Coin Family associations
    /// </summary>
    [JsonIgnore]
    public static readonly Dictionary<CoinFamily, Type> Families = new()
    {
        {CoinFamily.Bitcoin, typeof(BitcoinTemplate)},
    };
}

public enum BitcoinSubfamily
{
    [EnumMember(Value = "none")]
    None,
}

public class ExtendedMaturityConfig
{
    /// <summary>
    /// Block height at which the extended maturity period begins (inclusive).
    /// </summary>
    public ulong StartHeight { get; set; }

    /// <summary>
    /// Number of blocks in the extended maturity period.
    /// EndHeight is computed as StartHeight + PeriodLength.
    /// Mirrors EXT_COINBASE_MATURITY in the node source.
    /// </summary>
    public ulong PeriodLength { get; set; }

    /// <summary>
    /// Computed end height (exclusive). Blocks with height >= EndHeight use standard maturity.
    /// </summary>
    [JsonIgnore]
    public ulong EndHeight => StartHeight + PeriodLength;
}

public partial class BitcoinTemplate : CoinTemplate
{
    public class BitcoinNetworkParams
    {
        /// <summary>
        /// Arbitrary extension data
        /// </summary>
        [JsonExtensionData]
        public IDictionary<string, object> Extra { get; set; }
    }

    [JsonProperty(Order = -7, DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate)]
    [DefaultValue(BitcoinSubfamily.None)]
    [JsonConverter(typeof(StringEnumConverter), true)]
    public BitcoinSubfamily Subfamily { get; set; }

    public JObject CoinbaseHasher { get; set; }
    public JObject HeaderHasher { get; set; }
    public JObject BlockHasher { get; set; }

    [JsonProperty("diff1")]
    public string Diff1 { get; set; }

    [JsonProperty("posBlockHasher")]
    public JObject PoSBlockHasher { get; set; }

    [JsonProperty(DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate)]
    [DefaultValue(1u)]
    public uint CoinbaseTxVersion { get; set; }

    /// <summary>
    /// Default transaction comment for coins that REQUIRE tx comments
    /// </summary>
    public string CoinbaseTxComment { get; set; }

    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
    public bool HasBrokenSendMany { get; set; } = false;

    [JsonProperty(DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate)]
    [DefaultValue(1.0d)]
    public double ShareMultiplier { get; set; } = 1.0d;

    /// <summary>
    /// Bech32Prefix of a valid address
    /// </summary>
    public string BechPrefix { get; set; }

    [JsonProperty(DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate)]
    public double? HashrateMultiplier { get; set; }

    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
    public bool CoinbaseIgnoreAuxFlags { get; set; }

    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
    public bool IsPseudoPoS { get; set; }

    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
    public JToken BlockTemplateRpcExtraParams { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public Dictionary<string, BitcoinNetworkParams> Networks { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public int? CoinbaseMinConfirmations { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string BlockSerializer { get; set; }

    /// <summary>
    /// Force the use of the raw public key of the specified poolAddress
    /// </summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public bool ForcePoolAddressDestinationWithPubKey { get; set; }

    /// <summary>
    /// Amount of decimals used for payouts
    /// </summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public int? PayoutDecimalPlaces { get; set; } = 8;

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public ExtendedMaturityConfig ExtendedMaturity { get; set; }
}

#endregion // Coin Definitions

public enum PayoutScheme
{
    PPLNS = 1,
    PROP = 2,
    SOLO = 3,
    PPS = 4,
    PPBS = 5,
    PPLNSBF = 6,
}

public partial class ClusterLoggingConfig
{
    public string Level { get; set; }
    public bool EnableConsoleLog { get; set; }
    public bool EnableConsoleColors { get; set; }
    public string LogFile { get; set; }
    public string ApiLogFile { get; set; }
    public bool PerPoolLogFile { get; set; }
    public string LogBaseDirectory { get; set; }
    public bool GPDRCompliant { get; set; }
}

public partial class NetworkEndpointConfig
{
    public string Host { get; set; }
    public int Port { get; set; }
}

public partial class AuthenticatedNetworkEndpointConfig : NetworkEndpointConfig
{
    public string User { get; set; }
    public string Password { get; set; }
}

public class DaemonEndpointConfig : AuthenticatedNetworkEndpointConfig
{
    /// <summary>
    /// Use SSL to for RPC requests
    /// </summary>
    public bool Ssl { get; set; }

    /// <summary>
    /// Use HTTP2 protocol for RPC requests (don't use this unless your daemon(s) live behind a HTTP reverse proxy)
    /// </summary>
    public bool Http2 { get; set; }

    /// <summary>
    /// Optional endpoint category
    /// </summary>
    public string Category { get; set; }

    /// <summary>
    /// Optional request path for RPC requests
    /// </summary>
    public string HttpPath { get; set; }

    /// <summary>
    /// Arbitrary extension data
    /// </summary>
    [JsonExtensionData]
    public IDictionary<string, object> Extra { get; set; }
}

public class DatabaseConfig : AuthenticatedNetworkEndpointConfig
{
    public string Database { get; set; }
}

public class PostgresConfig : DatabaseConfig
{
    public bool Tls { get; set; }
    public string TlsCert { get; set; }
    public string TlsKey { get; set; }
    public string TlsPassword { get; set; }
    public bool TlsNoValidate { get; set; }
    public int? CommandTimeout { get; set; }
    public bool? EnableLegacyTimestamps { get; set; }
}

public class TcpProxyProtocolConfig
{
    public bool Enable { get; set; }
    public bool Mandatory { get; set; }
    public string[] ProxyAddresses { get; set; }
}

public class PoolEndpoint
{
    public string ListenAddress { get; set; }
    public string Name { get; set; }
    public double Difficulty { get; set; }
    public TcpProxyProtocolConfig TcpProxyProtocol { get; set; }
    public VarDiffConfig VarDiff { get; set; }
    public bool Tls { get; set; }
    public bool TlsAuto { get; set; }
    public string TlsPfxFile { get; set; }
    public string TlsPfxPassword { get; set; }
}

public partial class VarDiffConfig
{
    public double MinDiff { get; set; }
    public double? MaxDiff { get; set; }
    public double? MaxDelta { get; set; }
    public double TargetTime { get; set; }
    public double RetargetTime { get; set; }
    public double VariancePercent { get; set; }
}

public enum BanManagerKind
{
    Integrated = 1,
    IpTables
}

public class ClusterBanningConfig
{
    public BanManagerKind? Manager { get; set; }
    public bool? BanOnJunkReceive { get; set; }
    public bool? BanOnInvalidShares { get; set; }
    public bool? BanOnLoginFailure { get; set; }
}

public partial class PoolShareBasedBanningConfig
{
    public bool Enabled { get; set; }
    public int CheckThreshold { get; set; }
    public double InvalidPercent { get; set; }
    public int Time { get; set; }
    public double? MinerEffortPercent { get; set; }
    public int? MinerEffortTime { get; set; }
}

public partial class PoolPaymentProcessingConfig
{
    public bool Enabled { get; set; }
    public decimal MinimumPayment { get; set; }
    public PayoutScheme PayoutScheme { get; set; }
    public JToken PayoutSchemeConfig { get; set; }

    [JsonExtensionData]
    public IDictionary<string, object> Extra { get; set; }
}

public partial class ClusterPaymentProcessingConfig
{
    public bool Enabled { get; set; }
    public int Interval { get; set; }

    /// <summary>
    /// Identifier used in coinbase transactions to identify the pool
    /// </summary>
    public string CoinbaseString { get; set; }
}

public partial class PersistenceConfig
{
    public PostgresConfig Postgres { get; set; }
}

public class RewardRecipient
{
    public string Address { get; set; }
    public decimal Percentage { get; set; }
    public string Type { get; set; }
}

public partial class EmailSenderConfig : AuthenticatedNetworkEndpointConfig
{
    public string FromAddress { get; set; }
    public string FromName { get; set; }
}

public class PushoverConfig
{
    public bool Enabled { get; set; }
    public string User { get; set; }
    public string Token { get; set; }
}

public partial class AdminNotifications
{
    public bool Enabled { get; set; }
    public string EmailAddress { get; set; }
    public bool NotifyBlockFound { get; set; }
    public bool NotifyPaymentSuccess { get; set; }
}

public partial class NotificationsConfig
{
    public bool Enabled { get; set; }
    public EmailSenderConfig Email { get; set; }
    public PushoverConfig Pushover { get; set; }
    public AdminNotifications Admin { get; set; }
}

public class ApiRateLimitConfig
{
    public bool Disabled { get; set; }
    public RateLimitRule[] Rules { get; set; }
    public string[] IpWhitelist { get; set; }
}

public class ApiTlsConfig
{
    public bool Enabled { get; set; }
    public string TlsPfxFile { get; set; }
    public string TlsPfxPassword { get; set; }
}

public partial class ApiConfig
{
    public bool Enabled { get; set; }
    public string ListenAddress { get; set; }
    public int Port { get; set; }
    public ApiTlsConfig Tls { get; set; }
    public ApiRateLimitConfig RateLimiting { get; set; }
    public int? AdminPort { get; set; }
    public int? MetricsPort { get; set; }
    public string[] AdminIpWhitelist { get; set; }
    public string[] MetricsIpWhitelist { get; set; }
    public bool LegacyNullValueHandling { get; set; }

    /// <summary>
    /// Disable built-in CORS headers. Set to true when a reverse proxy (e.g. nginx) manages CORS.
    /// </summary>
    public bool NoCors { get; set; }
}

public class ZmqPubSubEndpointConfig
{
    public string Url { get; set; }
    public string Topic { get; set; }
    public string SharedEncryptionKey { get; set; }
}

public class ShareRelayEndpointConfig
{
    public string Url { get; set; }
    public string SharedEncryptionKey { get; set; }
}

public class ShareRelayConfig
{
    public string PublishUrl { get; set; }
    public bool Connect { get; set; }
    public string SharedEncryptionKey { get; set; }
}

public class Statistics
{
    public int? UpdateInterval { get; set; }
    public int? HashrateCalculationWindow { get; set; }
    public int? GcInterval { get; set; }
    public int? CleanupDays { get; set; }
}

public class NicehashClusterConfig
{
    public bool EnableAutoDiff { get; set; }
}

public class ClusterMemoryConfig
{
    public int? RmsmMaximumFreeSmallPoolBytes { get; set; }
    public int? RmsmMaximumFreeLargePoolBytes { get; set; }
}

public partial class PoolConfig
{
    [Required]
    public string Id { get; set; }

    [Required]
    public string Coin { get; set; }

    public bool Enabled { get; set; }

    [Required]
    public Dictionary<int, PoolEndpoint> Ports { get; set; }

    [Required]
    public DaemonEndpointConfig[] Daemons { get; set; }

    public PoolPaymentProcessingConfig PaymentProcessing { get; set; }
    public PoolShareBasedBanningConfig Banning { get; set; }
    public RewardRecipient[] RewardRecipients { get; set; }
    public string Address { get; set; }
    public string PubKey { get; set; }
    public int ClientConnectionTimeout { get; set; }
    public int JobRebroadcastTimeout { get; set; }
    public int BlockRefreshInterval { get; set; }

    public bool? EnableInternalStratum { get; set; }

    public int? VardiffIdleSweepInterval { get; set; }

    /// <summary>
    /// Purely informational. List of hostnames where this pool's stratum endpoints can be
    /// reached (e.g. "mining.bitwebcore.net"). Not used by any pool/API logic — passed through
    /// to the API response so the frontend can display connection info without hardcoding it.
    /// Allows the pool's mining domain(s) to differ from the domain the API itself is served on,
    /// and to be changed/extended (e.g. additional "mining2.bitwebcore.net") without a frontend deploy.
    /// </summary>
    public string[] MiningDomains { get; set; }

    [JsonExtensionData]
    public IDictionary<string, object> Extra { get; set; }
}

public partial class ClusterConfig
{
    public byte? InstanceId { get; set; }
    public string[] CoinTemplates { get; set; }
    public string ClusterName { get; set; }
    public ClusterLoggingConfig Logging { get; set; }
    public ClusterBanningConfig Banning { get; set; }
    public PersistenceConfig Persistence { get; set; }
    public ClusterPaymentProcessingConfig PaymentProcessing { get; set; }
    public NotificationsConfig Notifications { get; set; }
    public ApiConfig Api { get; set; }
    public Statistics Statistics { get; set; }
    public NicehashClusterConfig Nicehash { get; set; }
    public ClusterMemoryConfig Memory { get; set; }
    public ShareRelayConfig ShareRelay { get; set; }
    public ShareRelayEndpointConfig[] ShareRelays { get; set; }
    public string ShareRecoveryFile { get; set; }

    [Required]
    public PoolConfig[] Pools { get; set; }
}
