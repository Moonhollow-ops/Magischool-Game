using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Magischool.Sim.Core;

/// <summary>Runs a per-chunk body over a list of chunks, in parallel when that is worth the overhead.</summary>
internal static class ChunkLoop
{
    public static void Run(Simulation sim, List<int> chunks, Action<int> body)
    {
        if (chunks.Count <= 1 || sim.Parallelism.MaxDegreeOfParallelism == 1)
        {
            foreach (int chunk in chunks) body(chunk);
            return;
        }
        Parallel.For(0, chunks.Count, sim.Parallelism, k => body(chunks[k]));
    }
}
