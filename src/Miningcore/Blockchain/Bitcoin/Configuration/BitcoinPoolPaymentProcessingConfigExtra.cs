namespace Miningcore.Blockchain.Bitcoin.Configuration;

public class BitcoinPoolPaymentProcessingConfigExtra
{
    /// <summary>
    /// Wallet Password if the daemon is running with an encrypted wallet (used for unlocking wallet during payment processing)
    /// </summary>
    public string WalletPassword { get; set; }

    /// <summary>
    /// if True, miners pay payment tx fees
    /// </summary>
    public bool MinersPayTxFees { get; set; }

    /// <summary>
    /// Enable the payout confirmation guard.
    /// When true, two additional protections are activated:
    ///
    /// 1. ClassifyBlocksAsync will not mark a block as Confirmed (and will not credit miner
    ///    balances) until transactionInfo.Confirmations >= PayoutMinConfirmations, even when
    ///    the node already reports the coinbase category as "generate". This adds an explicit
    ///    buffer on top of the consensus maturity boundary.
    ///
    /// 2. The sendmany RPC call will include PayoutMinConfirmations as the minconf parameter,
    ///    telling the node to only select wallet UTXOs (e.g. change from previous payouts) that
    ///    have at least that many confirmations. Coinbase UTXOs are already consensus-gated at
    ///    100 blocks so this is belt-and-suspenders for non-coinbase wallet funds.
    ///
    /// Note: this feature is intentionally opt-in and is independent of ExtendedMaturity.
    /// ExtendedMaturity adjusts the progress-bar denominator for display purposes; this guard
    /// acts on the raw confirmation count and controls when balances are actually credited.
    /// There is no conflict: even if ExtendedMaturity is configured, this guard fires only on
    /// PayoutMinConfirmations which is a flat post-maturity threshold.
    ///
    /// Recommended value: same as coinbaseMinConfirmations (102 default) or higher for
    /// low-hashrate coins where short reorgs are possible.
    /// </summary>
    public bool PayoutMinConfirmationsEnabled { get; set; } = false;

    /// <summary>
    /// Number of confirmations required before a "generate"-category coinbase block is credited
    /// to miner balances and before wallet UTXOs are eligible for inclusion in sendmany.
    ///
    /// Fallback chain (highest priority first):
    ///   paymentProcessing.extra.payoutMinConfirmations   (this field)
    ///   → daemon endpoint extra.minimumConfirmations
    ///   → coin template coinbaseMinConfirmations
    ///   → BitcoinConstants.CoinbaseMinConfirmations (102)
    ///
    /// Only effective when PayoutMinConfirmationsEnabled = true.
    /// </summary>
    public int? PayoutMinConfirmations { get; set; }
}
