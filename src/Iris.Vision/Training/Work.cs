namespace Iris.Vision.Training;

/// <summary>Splitting element-wise work among a set number of threads (the learning uses only the cores it is given).</summary>
internal static class Work
{
    // elements below which one thread is faster than several
    private const long Small = 1 << 15;

    /// <summary>body(from, to) over [0, count) in contiguous ranges, on at most `threads` threads.</summary>
    public static void For(int count, long elements, int threads, Action<int, int> body)
    {
        if (threads <= 1 || count < 2 || elements < Small)
        {
            body(0, count);
            return;
        }

        var ranges = Math.Min(count, threads * 4);
        Parallel.For(0, ranges, new ParallelOptions { MaxDegreeOfParallelism = threads },
            r => body((int)((long)count * r / ranges), (int)((long)count * (r + 1) / ranges)));
    }

    /// <summary>The same with a result per range, combined in the order of the ranges (so it does not depend on timing).</summary>
    public static void For<TPart>(int count, long elements, int threads, Func<int, int, TPart> body, Action<TPart> combine)
    {
        if (threads <= 1 || count < 2 || elements < Small)
        {
            combine(body(0, count));
            return;
        }

        var ranges = Math.Min(count, threads * 4);
        var parts = new TPart[ranges];
        Parallel.For(0, ranges, new ParallelOptions { MaxDegreeOfParallelism = threads },
            r => parts[r] = body((int)((long)count * r / ranges), (int)((long)count * (r + 1) / ranges)));
        foreach (var part in parts)
        {
            combine(part);
        }
    }
}

/// <summary>Adds the seconds since the last mark to named totals (where the time of a step goes).</summary>
internal sealed class Clock(Dictionary<string, double> totals, string prefix)
{
    private long _start = System.Diagnostics.Stopwatch.GetTimestamp();

    public void Mark(string part)
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var key = prefix + part;
        totals[key] = totals.GetValueOrDefault(key) + (now - _start) / (double)System.Diagnostics.Stopwatch.Frequency;
        _start = now;
    }
}
