using System.Reflection;
using Autofac;
using JetBrains.Annotations;
using Miningcore.Crypto;
using Miningcore.Crypto.Hashing.Algorithms;
using NBitcoin;
using Newtonsoft.Json;

namespace Miningcore.Configuration;

public abstract partial class CoinTemplate
{
    public T As<T>() where T : CoinTemplate
    {
        return (T) this;
    }

    public abstract string GetAlgorithmName();

    /// <summary>
    /// json source file where this template originated from
    /// </summary>
    [JsonIgnore]
    public string Source { get; set; }
}

public partial class BitcoinTemplate
{
    public BitcoinTemplate()
    {
        coinbaseHasherValue = new Lazy<IHashAlgorithm>(() =>
            HashAlgorithmFactory.GetHash(ComponentContext, CoinbaseHasher));

        headerHasherValue = new Lazy<IHashAlgorithm>(() =>
            HashAlgorithmFactory.GetHash(ComponentContext, HeaderHasher));

        blockHasherValue = new Lazy<IHashAlgorithm>(() =>
            HashAlgorithmFactory.GetHash(ComponentContext, BlockHasher));

        posBlockHasherValue = new Lazy<IHashAlgorithm>(() =>
            HashAlgorithmFactory.GetHash(ComponentContext, PoSBlockHasher));
    }

    private readonly Lazy<IHashAlgorithm> coinbaseHasherValue;
    private readonly Lazy<IHashAlgorithm> headerHasherValue;
    private readonly Lazy<IHashAlgorithm> blockHasherValue;
    private readonly Lazy<IHashAlgorithm> posBlockHasherValue;

    public IComponentContext ComponentContext { get; [UsedImplicitly] init; }

    public IHashAlgorithm CoinbaseHasherValue => coinbaseHasherValue.Value;
    public IHashAlgorithm HeaderHasherValue => headerHasherValue.Value;
    public IHashAlgorithm BlockHasherValue => blockHasherValue.Value;
    public IHashAlgorithm PoSBlockHasherValue => posBlockHasherValue.Value;

    public BitcoinNetworkParams GetNetwork(ChainName chain)
    {
        if(Networks == null || Networks.Count == 0)
            return null;

        if(chain == ChainName.Mainnet)
            return Networks["main"];
        else if(chain == ChainName.Testnet)
            return Networks["test"];
        else if(chain == ChainName.Regtest)
            return Networks["regtest"];

        throw new NotSupportedException("unsupported network type");
    }

    #region Overrides of CoinTemplate

    public override string GetAlgorithmName()
    {
        var hash = HeaderHasherValue;

        var type = hash.GetType() == typeof(DigestReverser)
            ? ((DigestReverser) hash).Upstream.GetType()
            : hash.GetType();

        // Prefer the [Identifier] attribute — it's the canonical miner-facing name
        // (e.g. "argon2id1024", "sha256d"). Falls back to the C# class name only
        // for implementations that haven't declared the attribute yet.
        return type.GetCustomAttribute<IdentifierAttribute>()?.Name ?? type.Name;
    }

    #endregion
}

public partial class PoolConfig
{
    /// <summary>
    /// Back-reference to coin template for this pool
    /// </summary>
    [JsonIgnore]
    public CoinTemplate Template { get; set; }
}
