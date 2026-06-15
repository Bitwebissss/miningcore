using System;
using Autofac;
using Microsoft.IO;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Configuration;
using Miningcore.Crypto;
using Miningcore.Stratum;
using Miningcore.Tests.Util;
using NBitcoin;
using Newtonsoft.Json;
using NLog;
using Xunit;
#pragma warning disable 8974

namespace Miningcore.Tests.Blockchain.Bitcoin;

// Always returns [0x01, 0x00, ..., 0x00] (32 bytes).
// Guarantees: uint256 value=1 (< any valid target), BigInteger=1 (no div-by-zero in shareDiff).
// Lets tests validate pool framework logic (coinbase, header assembly, target comparison,
// block serialization) independently of any specific PoW hasher output.
internal sealed class MinHasher : IHashAlgorithm
{
    public void Digest(ReadOnlySpan<byte> data, Span<byte> result, params object[] extra)
    {
        result.Clear();
        if(result.Length > 0)
            result[0] = 0x01;
    }
}

public class BitcoinJobTests : TestBase
{
    [Fact]
    public void Process_Valid_Block()
    {
        var (job, worker) = CreateJob();

        var submitParams = JsonConvert.DeserializeObject<object[]>(
            "[\"miner1\",\"00000001\",\"01000000\",\"63445774\",\"51036775\"]",
            jsonSerializerSettings);

        var extraNonce2 = submitParams[2] as string;
        var nTime       = submitParams[3] as string;
        var nonce       = submitParams[4] as string;

        var (share, blockHex) = job.ProcessShare(worker, extraNonce2, nTime, nonce);

        Assert.NotNull(share);
        Assert.NotNull(blockHex);
        Assert.Equal(813750, share.BlockHeight);
        Assert.True(share.IsBlockCandidate);
    }

    [Fact]
    public void Process_Duplicate_Submission()
    {
        var (job, worker) = CreateJob();

        var submitParams = JsonConvert.DeserializeObject<object[]>(
            "[\"miner1\",\"00000001\",\"01000000\",\"63445774\",\"51036775\"]",
            jsonSerializerSettings);

        var extraNonce2 = submitParams[2] as string;
        var nTime       = submitParams[3] as string;
        var nonce       = submitParams[4] as string;

        var (share, _) = job.ProcessShare(worker, extraNonce2, nTime, nonce);

        Assert.NotNull(share);
        Assert.True(share.IsBlockCandidate);

        Assert.ThrowsAny<StratumException>(() => job.ProcessShare(worker, extraNonce2, nTime, nonce));
    }

    // 7 hex chars instead of required 8 -> "incorrect size of nonce"
    [Fact]
    public void Process_Invalid_Nonce()
    {
        var (job, worker) = CreateJob();

        var submitParams = JsonConvert.DeserializeObject<object[]>(
            "[\"miner1\",\"00000001\",\"01000000\",\"63445774\",\"6103677\"]",
            jsonSerializerSettings);

        var extraNonce2 = submitParams[2] as string;
        var nTime       = submitParams[3] as string;
        var nonce       = submitParams[4] as string;

        Assert.ThrowsAny<StratumException>(() => job.ProcessShare(worker, extraNonce2, nTime, nonce));
    }

    [Fact]
    public void Process_Invalid_Time()
    {
        var (job, worker) = CreateJob();

        // 0x13445774 = 323837812, far before curTime 1665423220 -> "ntime out of range"
        var submitParams = JsonConvert.DeserializeObject<object[]>(
            "[\"miner1\",\"00000001\",\"01000000\",\"13445774\",\"51036775\"]",
            jsonSerializerSettings);

        var extraNonce2 = submitParams[2] as string;
        var nTime       = submitParams[3] as string;
        var nonce       = submitParams[4] as string;

        Assert.ThrowsAny<StratumException>(() => job.ProcessShare(worker, extraNonce2, nTime, nonce));
    }

    private (BitcoinJob, StratumConnection) CreateJob()
    {
        var job  = new BitcoinJob();
        var coin = (BitcoinTemplate) ModuleInitializer.CoinTemplates["litecoin"];
        var pc   = new PoolConfig { Template = coin };

        // Standard Litecoin-like block template.
        // Target "0000000100...00": BigInteger = 2^224 (positive, no crash), uint256 >> MinHasher output.
        // MinHasher always returns hash=[0x01,0x00,...], so uint256(hash)=1 <= target=2^224 -> IsBlockCandidate=true.
        const string blockTemplateJson =
            "{\"version\":536870912" +
            ",\"previousblockhash\":\"0000011a86a1ad3609e5359b6b6411a1654108ee7c1afc003dec23b5a0400e4b\"" +
            ",\"coinbasevalue\":1801475949" +
            ",\"target\":\"0000000100000000000000000000000000000000000000000000000000000000\"" +
            ",\"noncerange\":\"00000000ffffffff\"" +
            ",\"curtime\":1665423220" +
            ",\"bits\":\"207fffff\"" +
            ",\"height\":813750" +
            ",\"transactions\":[]" +
            ",\"coinbaseaux\":{\"flags\":null}" +
            ",\"default_witness_commitment\":null" +
            ",\"capabilities\":[\"proposal\"]" +
            ",\"rules\":[\"csv\"]" +
            ",\"vbavailable\":{}" +
            ",\"vbrequired\":0" +
            ",\"longpollid\":\"0000011a86a1ad3609e5359b6b6411a1654108ee7c1afc003dec23b5a0400e4b814670\"" +
            ",\"mintime\":1665422408" +
            ",\"mutable\":[\"time\",\"transactions\",\"prevblock\"]" +
            ",\"sigoplimit\":40000" +
            ",\"sizelimit\":4000000}";

        var blockTemplate = JsonConvert.DeserializeObject<
            Miningcore.Blockchain.Bitcoin.DaemonResponses.BlockTemplate>(
            blockTemplateJson, jsonSerializerSettings);

        var clock = MockMasterClock.FromTicks(638010200200475015);

        var poolAddressDestination = BitcoinUtils.AddressToDestination(
            "mipcBbFg9gMiCh81Kj8tqqdgoZub1ZJRfn", Network.TestNet);
        var network = Network.GetNetwork("testnet");

        var context = new BitcoinWorkerContext
        {
            Miner       = "miner1",
            ExtraNonce1 = "60000001",
            Difficulty  = 1e-10,
            UserAgent   = "cpuminer-multi/1.3.1"
        };

        var worker = new StratumConnection(
            new NullLogger(LogManager.LogFactory),
            container.Resolve<RecyclableMemoryStreamManager>(),
            clock, "1", false);

        worker.SetContext(context);

        // MinHasher for headerHasher: returns hash=1 (guaranteed < target=2^224).
        // Real SHA256D for coinbaseHasher and blockHasher: exercises actual serialization paths.
        job.Init(blockTemplate, "1", pc, null, new ClusterConfig(), clock,
            poolAddressDestination, network, false,
            coin.ShareMultiplier, coin.CoinbaseHasherValue, new MinHasher(), coin.BlockHasherValue);

        return (job, worker);
    }
}
