namespace Miningcore.Blockchain;

public class BlockchainStats
{
    /// <summary>
    /// Shared lock for all concurrent writers.
    /// Three independent execution contexts write to this object at runtime:
    ///   1. BitcoinJobManager.UpdateJob      — Rx Concat chain #1 (new block)
    ///   2. UpdateNetworkStatsAsync/Legacy    — Rx Concat chain #2 (10-min timer)
    ///   3. ShareReceiver.ProcessMessage      — N ThreadPool workers (master mode)
    /// Chains #1 and #2 are serialised within themselves but not with each other
    /// and not with #3.  All three must take this lock before writing any field.
    /// </summary>
    public readonly object SyncRoot = new();

    public string NetworkType { get; set; }
    public double NetworkHashrate { get; set; }
    public double NetworkDifficulty { get; set; }
    public string NextNetworkTarget { get; set; }
    public string NextNetworkBits { get; set; }
    public DateTime? LastNetworkBlockTime { get; set; }
    public ulong BlockHeight { get; set; }          // height of block being worked on (work height = last accepted + 1)
    public ulong NetworkBlockHeight { get; set; }   // height of last block accepted by the network (= BlockHeight - 1)
    public int ConnectedPeers { get; set; }
    public string NodeVersion { get; set; } = "Unknown";
    public string RewardType { get; set; }
}

public interface IExtraNonceProvider
{
    int ByteSize { get; }
    string Next();
}
