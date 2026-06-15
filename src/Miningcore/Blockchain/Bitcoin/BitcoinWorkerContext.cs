using Miningcore.Mining;

namespace Miningcore.Blockchain.Bitcoin;

public class BitcoinWorkerContext : WorkerContextBase
{
    /// <summary>
    /// Usually a wallet address
    /// </summary>
    public override string Miner { get; set; }

    /// <summary>
    /// Arbitrary worker identifier for miners using multiple rigs
    /// </summary>
    public string Worker { get; set; }

    /// <summary>
    /// Unique value assigned per worker
    /// </summary>
    public string ExtraNonce1 { get; set; }

    /// <summary>
    /// Mask for version-rolling (Overt ASIC-Boost)
    /// </summary>
    public uint? VersionRollingMask { get; internal set; }

    /// <summary>
    /// Per-worker queue of valid jobs. Eliminates global lock contention:
    /// each connection only locks its own context during job lookup.
    /// </summary>
    private readonly Queue<BitcoinJob> validJobs = new();
    private readonly HashSet<BitcoinJob> validJobsSet = new();

    /// <summary>
    /// Enqueues a job for this worker, evicting the oldest when the queue
    /// exceeds maxActiveJobs. Duplicate jobs (same reference) are ignored.
    /// </summary>
    public void AddJob(BitcoinJob job, int maxActiveJobs)
    {
        if(validJobsSet.Add(job))
            validJobs.Enqueue(job);

        while(validJobs.Count > maxActiveJobs)
        {
            var evicted = validJobs.Dequeue();
            validJobsSet.Remove(evicted);
        }
    }

    /// <summary>
    /// Returns the job matching jobId from this worker's queue, or null.
    /// </summary>
    public BitcoinJob GetJob(string jobId)
    {
        return validJobs.FirstOrDefault(x => x.JobId == jobId);
    }
}
