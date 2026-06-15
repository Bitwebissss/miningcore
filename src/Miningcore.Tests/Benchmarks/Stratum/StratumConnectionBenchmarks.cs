// Benchmarks are excluded from the normal test run (Skip below).
// To run: remove Skip, execute `dotnet test -c Release`, then restore it.
// BenchmarkDotNet requires a Release build to produce meaningful numbers.
using System;
using System.Buffers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using BenchmarkDotNet.Attributes;
using Microsoft.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Miningcore.JsonRpc;
using Miningcore.Stratum;
using Miningcore.Time;
using NLog;
#pragma warning disable 8974

namespace Miningcore.Tests.Benchmarks.Stratum;

/// <summary>
/// Microbenchmarks for <see cref="StratumConnection.ProcessRequestAsync"/>.
/// Measures JSON deserialisation + handler dispatch overhead for a single
/// stratum request (mining.authorize) on the hot path.
/// </summary>
[MemoryDiagnoser]
public class StratumConnectionBenchmarks : TestBase
{
    private const string ConnectionId = "bench-conn";

    // A well-formed stratum mining.authorize request.
    private static readonly byte[] requestBytes =
        Encoding.UTF8.GetBytes("{\"params\": [\"slush.miner1\", \"password\"], \"id\": 42, \"method\": \"mining.authorize\"}\n");

    private const string ProcessRequestAsyncMethod = "ProcessRequestAsync";

    private RecyclableMemoryStreamManager rmsm;
    private ILogger logger;
    private StratumConnection connection;
    private PrivateObject wrapper;

    [GlobalSetup]
    public void Setup()
    {
        ModuleInitializer.Initialize();

        rmsm   = ModuleInitializer.Container.Resolve<RecyclableMemoryStreamManager>();
        logger = new NullLogger(LogManager.LogFactory);

        connection = new StratumConnection(logger, rmsm,
            ModuleInitializer.Container.Resolve<IMasterClock>(), ConnectionId, false);
        wrapper    = new PrivateObject(connection);
    }

    private static Task OnPlaceholderRequestAsync(StratumConnection con, JsonRpcRequest request, CancellationToken ct)
        => Task.CompletedTask;

    /// <summary>
    /// Benchmarks the full JSON parse + handler dispatch cycle for one
    /// valid stratum request. Allocations shown by [MemoryDiagnoser].
    /// </summary>
    [Benchmark]
    public async Task ProcessRequest_Handle_Valid_Request()
    {
        await (Task) wrapper.Invoke(ProcessRequestAsyncMethod,
            CancellationToken.None,
            (Func<StratumConnection, JsonRpcRequest, CancellationToken, Task>) OnPlaceholderRequestAsync,
            new ReadOnlySequence<byte>(requestBytes));
    }
}
