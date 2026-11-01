using Iris.Evaluation;
using Iris.Translation;

namespace Iris.Assistant.Study;

/// <summary>A sentence (or two) of the original and the part of the translation that belongs to it, with how sure the pairing is (0–100).</summary>
public sealed record AlignedPair(string Source, string Target, double Confidence)
{
    /// <summary>Pairs below this need the owner's look; they are not taken in by default.</summary>
    public const double Sure = 35;

    public bool IsSure => Confidence >= Sure;
}

/// <summary>
/// Pairs the sentences of an original with those of its translation (a page of a book and the same page of its
/// translation). Each sentence of the original is translated by IRIS, and the order of pairs that makes the IRIS
/// translations most like the given translation (chrF, and similar length) wins – one sentence to one, one to two,
/// two to one, or a sentence without a partner. Dynamic programming over both texts, so the order is kept.
/// </summary>
public static class SentenceAligner
{
    private const double Threshold = 20;    // a pair must be at least this like the IRIS translation to beat leaving both out
    private const double MergePenalty = 5;  // one-to-two and two-to-one only when clearly better

    public static IReadOnlyList<AlignedPair> Align(string original, string translation, Func<string, string> translate)
    {
        var sources = Segmenter.Sentences(original).ToList();
        var targets = Segmenter.Sentences(translation).ToList();
        if (sources.Count == 0 || targets.Count == 0)
        {
            return [];
        }

        var machine = sources.Select(translate).ToList();
        int n = sources.Count, m = targets.Count;
        var best = new double[n + 1, m + 1];
        var move = new (int Source, int Target)[n + 1, m + 1];
        for (var i = 0; i <= n; i++)
        {
            for (var j = 0; j <= m; j++)
            {
                if (i == 0 && j == 0)
                {
                    continue;
                }

                best[i, j] = double.NegativeInfinity;
                foreach (var (a, b) in new[] { (1, 1), (1, 2), (2, 1), (1, 0), (0, 1) })
                {
                    if (i < a || j < b || double.IsNegativeInfinity(best[i - a, j - b]))
                    {
                        continue;
                    }

                    var gain = a == 0 || b == 0 ? 0 : Similarity(Join(machine, i - a, a), Join(targets, j - b, b)) - Threshold -
                        (a + b > 2 ? MergePenalty : 0);
                    if (best[i - a, j - b] + gain > best[i, j])
                    {
                        best[i, j] = best[i - a, j - b] + gain;
                        move[i, j] = (a, b);
                    }
                }
            }
        }

        var pairs = new List<AlignedPair>();
        for (int i = n, j = m; i > 0 || j > 0;)
        {
            var (a, b) = move[i, j];
            if (a > 0 && b > 0)
            {
                pairs.Add(new AlignedPair(Join(sources, i - a, a), Join(targets, j - b, b), Similarity(Join(machine, i - a, a), Join(targets, j - b, b))));
            }

            i -= a;
            j -= b;
        }

        pairs.Reverse();
        return pairs;
    }

    // chrF of the IRIS translation against the given one, less when the lengths differ much
    private static double Similarity(string machine, string target)
    {
        var chrf = Chrf.Score(Chrf.Statistics(machine, target));
        var ratio = Math.Log((machine.Length + 1.0) / (target.Length + 1.0));
        return chrf - 10 * Math.Abs(ratio);
    }

    private static string Join(List<string> sentences, int start, int count) => string.Join(" ", sentences.GetRange(start, count));
}
