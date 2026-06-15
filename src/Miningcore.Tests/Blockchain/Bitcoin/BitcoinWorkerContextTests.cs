using System;
using System.Linq;
using System.Threading.Tasks;
using Miningcore.Blockchain.Bitcoin;
using Xunit;

namespace Miningcore.Tests.Blockchain.Bitcoin;

/// <summary>
/// Tests for the per-worker job queue introduced by the job-per-worker
/// architectural change. Verifies that each BitcoinWorkerContext maintains
/// its own independent job queue with correct eviction and lookup behaviour.
/// </summary>
public class BitcoinWorkerContextTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Minimal subclass that sets JobId (protected set) without needing a full
    /// block template. Only the queue mechanics are under test here.
    /// </summary>
    private sealed class TestableJob : BitcoinJob
    {
        public TestableJob(string jobId)
        {
            JobId = jobId;
        }
    }

    private static TestableJob Job(string id) => new(id);

    // -------------------------------------------------------------------------
    // AddJob / GetJob
    // -------------------------------------------------------------------------

    [Fact]
    public void GetJob_Returns_Null_When_Queue_Is_Empty()
    {
        var ctx = new BitcoinWorkerContext();

        Assert.Null(ctx.GetJob("any-id"));
    }

    [Fact]
    public void GetJob_Returns_Correct_Job_By_Id()
    {
        var ctx = new BitcoinWorkerContext();
        var job = Job("abc");

        ctx.AddJob(job, maxActiveJobs: 4);

        Assert.Same(job, ctx.GetJob("abc"));
    }

    [Fact]
    public void GetJob_Returns_Null_For_Unknown_Id()
    {
        var ctx = new BitcoinWorkerContext();
        ctx.AddJob(Job("abc"), maxActiveJobs: 4);

        Assert.Null(ctx.GetJob("xyz"));
    }

    [Fact]
    public void AddJob_Allows_Lookup_Of_Multiple_Jobs()
    {
        var ctx = new BitcoinWorkerContext();
        var j1  = Job("j1");
        var j2  = Job("j2");
        var j3  = Job("j3");

        ctx.AddJob(j1, maxActiveJobs: 4);
        ctx.AddJob(j2, maxActiveJobs: 4);
        ctx.AddJob(j3, maxActiveJobs: 4);

        Assert.Same(j1, ctx.GetJob("j1"));
        Assert.Same(j2, ctx.GetJob("j2"));
        Assert.Same(j3, ctx.GetJob("j3"));
    }

    // -------------------------------------------------------------------------
    // Eviction: oldest job is dropped when queue exceeds maxActiveJobs
    // -------------------------------------------------------------------------

    [Fact]
    public void AddJob_Evicts_Oldest_When_Queue_Full()
    {
        var ctx = new BitcoinWorkerContext();
        var j1  = Job("j1"); // will be evicted
        var j2  = Job("j2");
        var j3  = Job("j3");
        var j4  = Job("j4");
        var j5  = Job("j5"); // triggers eviction of j1

        ctx.AddJob(j1, maxActiveJobs: 4);
        ctx.AddJob(j2, maxActiveJobs: 4);
        ctx.AddJob(j3, maxActiveJobs: 4);
        ctx.AddJob(j4, maxActiveJobs: 4);
        ctx.AddJob(j5, maxActiveJobs: 4);

        // j1 must be gone
        Assert.Null(ctx.GetJob("j1"));

        // j2..j5 must still be present
        Assert.Same(j2, ctx.GetJob("j2"));
        Assert.Same(j3, ctx.GetJob("j3"));
        Assert.Same(j4, ctx.GetJob("j4"));
        Assert.Same(j5, ctx.GetJob("j5"));
    }

    [Fact]
    public void AddJob_With_MaxActiveJobs_1_Keeps_Only_Latest()
    {
        var ctx = new BitcoinWorkerContext();
        var j1  = Job("j1");
        var j2  = Job("j2");

        ctx.AddJob(j1, maxActiveJobs: 1);
        ctx.AddJob(j2, maxActiveJobs: 1);

        Assert.Null(ctx.GetJob("j1"));
        Assert.Same(j2, ctx.GetJob("j2"));
    }

    // -------------------------------------------------------------------------
    // Duplicate guard: same job reference added twice must not inflate queue
    // -------------------------------------------------------------------------

    [Fact]
    public void AddJob_Ignores_Duplicate_Reference()
    {
        var ctx = new BitcoinWorkerContext();
        var j1  = Job("j1");
        var j2  = Job("j2");
        var j3  = Job("j3");
        var j4  = Job("j4");

        // Fill queue to maxActiveJobs=4
        ctx.AddJob(j1, maxActiveJobs: 4);
        ctx.AddJob(j2, maxActiveJobs: 4);
        ctx.AddJob(j3, maxActiveJobs: 4);
        ctx.AddJob(j4, maxActiveJobs: 4);

        // Re-adding j1 (duplicate) must NOT push j2 out
        ctx.AddJob(j1, maxActiveJobs: 4);

        Assert.Same(j1, ctx.GetJob("j1"));
        Assert.Same(j2, ctx.GetJob("j2"));
    }

    // -------------------------------------------------------------------------
    // Isolation: two contexts are fully independent
    // -------------------------------------------------------------------------

    [Fact]
    public void Two_Worker_Contexts_Are_Independent()
    {
        var ctxA = new BitcoinWorkerContext();
        var ctxB = new BitcoinWorkerContext();

        var jobA = Job("jobA");
        var jobB = Job("jobB");

        ctxA.AddJob(jobA, maxActiveJobs: 4);
        ctxB.AddJob(jobB, maxActiveJobs: 4);

        // Each context finds only its own job
        Assert.Same(jobA, ctxA.GetJob("jobA"));
        Assert.Null(ctxA.GetJob("jobB"));

        Assert.Same(jobB, ctxB.GetJob("jobB"));
        Assert.Null(ctxB.GetJob("jobA"));
    }

    [Fact]
    public void Eviction_In_One_Context_Does_Not_Affect_Other()
    {
        var ctxA = new BitcoinWorkerContext();
        var ctxB = new BitcoinWorkerContext();

        // Fill ctxA to capacity and trigger eviction
        var j1 = Job("j1");
        ctxA.AddJob(j1,       maxActiveJobs: 1);
        ctxA.AddJob(Job("j2"), maxActiveJobs: 1); // evicts j1

        // ctxB never saw j1, but its own queue is unaffected
        ctxB.AddJob(j1, maxActiveJobs: 4);

        Assert.Null(ctxA.GetJob("j1"));
        Assert.Same(j1, ctxB.GetJob("j1"));
    }

    // -------------------------------------------------------------------------
    // Thread-safety: concurrent AddJob/GetJob on a single context
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Concurrent_AddJob_And_GetJob_Do_Not_Throw()
    {
        // This test exercises the lock(context) path that SubmitShareAsync uses.
        // We don't assert specific job order under concurrency - we assert that
        // no exception is thrown and the context remains usable afterwards.
        var ctx = new BitcoinWorkerContext();
        var jobs = Enumerable.Range(0, 20)
            .Select(i => Job($"j{i}"))
            .ToArray();

        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        var writers = Enumerable.Range(0, 4).Select(n => Task.Run(() =>
        {
            foreach(var job in jobs)
            {
                try { lock(ctx) { ctx.AddJob(job, maxActiveJobs: 4); } }
                catch(Exception ex) { exceptions.Add(ex); }
            }
        }));

        var readers = Enumerable.Range(0, 4).Select(n => Task.Run(() =>
        {
            foreach(var job in jobs)
            {
                try { lock(ctx) { ctx.GetJob(job.JobId); } }
                catch(Exception ex) { exceptions.Add(ex); }
            }
        }));

        await Task.WhenAll(writers.Concat(readers));

        Assert.Empty(exceptions);
        // Context still functional after concurrent access
        ctx.AddJob(Job("final"), maxActiveJobs: 4);
        Assert.NotNull(ctx.GetJob("final"));
    }
}
