using System.Text;

namespace Iris.Evaluation;

/// <summary>
/// chrF – how well a translation matches a reference translation in character n-grams (1 to 6, spaces left out),
/// as F-score with β = 2 (recall weighs more). Suits Slovak with its many endings better than word-based BLEU. Computed
/// as sacreBLEU 2 computes it by default (CHRF(): average precision and recall over the orders that occur, statistics
/// summed over the whole test set).
/// </summary>
public static class Chrf
{
    public const int Order = 6;
    private const double Beta = 2;

    /// <summary>Statistics of one sentence: for each order n = 1..6 the n-grams of the hypothesis, of the reference and matching.</summary>
    public static long[] Statistics(string hypothesis, string reference)
    {
        var stats = new long[3 * Order];
        var hyp = Characters(hypothesis);
        var refs = Characters(reference);
        for (var n = 1; n <= Order; n++)
        {
            var h = NGrams(hyp, n);
            var r = NGrams(refs, n);
            long match = 0;
            foreach (var (gram, count) in h)
            {
                if (r.TryGetValue(gram, out var other))
                {
                    match += Math.Min(count, other);
                }
            }

            // as sacreBLEU: n-grams of the hypothesis count only where the reference has n-grams of that order
            var refCount = Math.Max(0, refs.Length - n + 1);
            stats[3 * (n - 1)] = refCount > 0 ? Math.Max(0, hyp.Length - n + 1) : 0;
            stats[3 * (n - 1) + 1] = refCount;
            stats[3 * (n - 1) + 2] = match;
        }

        return stats;
    }

    /// <summary>The score (0–100) of summed statistics.</summary>
    public static double Score(IReadOnlyList<long> stats)
    {
        double precision = 0, recall = 0;
        var orders = 0;
        for (var i = 0; i < Order; i++)
        {
            long hyp = stats[3 * i], refs = stats[3 * i + 1], match = stats[3 * i + 2];
            if (hyp > 0 && refs > 0)
            {
                precision += (double)match / hyp;
                recall += (double)match / refs;
                orders++;
            }
        }

        if (orders == 0)
        {
            return 0;
        }

        precision /= orders;
        recall /= orders;
        var factor = Beta * Beta;
        return precision + recall > 0 ? 100 * (1 + factor) * precision * recall / (factor * precision + recall) : 0;
    }

    /// <summary>The score of a whole test set (statistics of all sentences summed, then the score).</summary>
    public static double Corpus(IEnumerable<(string Hypothesis, string Reference)> pairs)
    {
        var total = new long[3 * Order];
        foreach (var (hypothesis, reference) in pairs)
        {
            var stats = Statistics(hypothesis, reference);
            for (var i = 0; i < total.Length; i++)
            {
                total[i] += stats[i];
            }
        }

        return Score(total);
    }

    // the characters (Unicode code points) of a text without its whitespace
    private static string[] Characters(string text) =>
        [.. text.EnumerateRunes().Where(r => !Rune.IsWhiteSpace(r)).Select(r => r.ToString())];

    private static Dictionary<string, int> NGrams(string[] characters, int n)
    {
        var grams = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i + n <= characters.Length; i++)
        {
            var gram = string.Concat(characters.AsSpan(i, n));
            grams[gram] = grams.GetValueOrDefault(gram) + 1;
        }

        return grams;
    }
}
