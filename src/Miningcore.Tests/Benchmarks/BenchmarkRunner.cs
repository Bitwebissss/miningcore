// To run benchmarks: change [Fact(Skip = ...)] to [Fact] below, then:
//   dotnet test src/Miningcore.Tests -c Release --filter "BenchmarkRunner"
// Restore Skip afterwards so CI does not run them on every build.
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Running;
using Miningcore.Tests.Benchmarks.Stratum;
using Xunit;
using Xunit.Abstractions;

namespace Miningcore.Tests.Benchmarks;

public class Benchmarks
{
    private readonly ITestOutputHelper output;

    public Benchmarks(ITestOutputHelper output)
    {
        this.output = output;
    }

    [Fact(Skip = "Manual only – remove Skip to run BenchmarkDotNet suite")]
    public void Run_Benchmarks()
    {
        var logger = new AccumulationLogger();

        var config = ManualConfig.Create(DefaultConfig.Instance)
            .AddLogger(logger)
            .WithOptions(ConfigOptions.DisableOptimizationsValidator);

        BenchmarkRunner.Run<StratumConnectionBenchmarks>(config);

        output.WriteLine(logger.GetLog());
    }
}
