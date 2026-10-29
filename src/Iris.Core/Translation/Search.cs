using Iris.Numerics;

namespace Iris.Translation;

/// <summary>A decoder that gives the logits of the next token for several hypotheses (rows) side by side.</summary>
public interface IDecoderSession
{
    int Rows { get; }

    int VocabSize { get; }

    /// <summary>Feeds the last token of every row; returns the logits of the next token, [Rows, VocabSize].</summary>
    float[] Step(ReadOnlySpan<int> tokens);

    /// <summary>Row r continues the hypothesis of row parents[r].</summary>
    void Reorder(ReadOnlySpan<int> parents);
}

/// <summary>A translation found by the search: generated token ids (the end of sentence included when it was reached) and its score.</summary>
public sealed record Hypothesis(int[] Tokens, float Score);

/// <summary>
/// Finds the translation: greedy (always the most probable next token) or beam search. Follows the reference
/// (Hugging Face transformers 5) step by step, so that the same model gives the same tokens:
/// beam search keeps 2 × beams candidates per step, the beams that end are scored by sum of log-probabilities /
/// length^penalty (the end of sentence counted) and the search stops when no running beam can improve.
/// </summary>
public static class Search
{
    // the "very large negative value" by which the reference shuts out hypotheses
    private const float Shut = -1.0e9f;

    public static Hypothesis Greedy(IDecoderSession session, GenerationSettings settings)
    {
        if (session.Rows != 1)
        {
            throw new ArgumentException("greedy search decodes one row");
        }

        var tokens = new List<int>();
        var last = settings.DecoderStartId;
        var length = 1;
        float score = 0;
        while (true)
        {
            // the choice is made on the logits themselves (as the reference does); the score is the log-probability
            var logits = session.Step([last]);
            var normalizer = LogSumExp(logits);
            Restrict(logits, length, settings);
            var next = Kernels.ArgMax(logits);
            score += (float)(logits[next] - normalizer);
            tokens.Add(next);
            length++;
            if (next == settings.EosId || length >= settings.MaxLength)
            {
                return new Hypothesis([.. tokens], score);
            }

            last = next;
        }
    }

    public static Hypothesis Beam(IDecoderSession session, GenerationSettings settings)
    {
        int beams = session.Rows, vocab = session.VocabSize, keep = 2 * beams;
        if (beams * (long)vocab > int.MaxValue)
        {
            throw new ArgumentException("too many beams for this vocabulary");
        }

        var running = new int[beams][];
        var runningScores = new float[beams];
        var last = new int[beams];
        for (var b = 0; b < beams; b++)
        {
            running[b] = [];
            runningScores[b] = b == 0 ? 0 : Shut;
            last[b] = settings.DecoderStartId;
        }

        var finished = new int[beams][];
        var finishedScores = new float[beams];
        var done = new bool[beams];
        Array.Fill(finished, []);
        Array.Fill(finishedScores, Shut);
        var improvable = true;
        var length = 1;
        var all = new float[beams * vocab];
        while (true)
        {
            // log-probabilities of every continuation of every beam, added to the beam's score
            var logits = session.Step(last);
            for (var b = 0; b < beams; b++)
            {
                var row = logits.AsSpan(b * vocab, vocab);
                Kernels.LogSoftmax(row);
                Restrict(row, length, settings);
                if (settings.Renormalize)
                {
                    Kernels.LogSoftmax(row);
                }

                for (var v = 0; v < vocab; v++)
                {
                    all[b * vocab + v] = row[v] + runningScores[b];
                }
            }

            // the best 2 × beams continuations
            var top = Top(all, keep);
            var parent = new int[keep];
            var token = new int[keep];
            var score = new float[keep];
            var ends = new bool[keep];
            var allEnd = true;
            for (var c = 0; c < keep; c++)
            {
                parent[c] = top[c] / vocab;
                token[c] = top[c] % vocab;
                score[c] = all[top[c]];
                ends[c] = token[c] == settings.EosId || length + 1 >= settings.MaxLength;
                allEnd &= ends[c];
            }

            // the beams that go on: the best continuations that did not end
            var going = new float[keep];
            for (var c = 0; c < keep; c++)
            {
                going[c] = score[c] + (ends[c] ? Shut : 0f);
            }

            var next = Top(going, beams);

            // the finished hypotheses: the old ones and the continuations among the best `beams` that ended
            var divisor = (float)Math.Pow(length, settings.LengthPenalty);
            var full = settings.EarlyStopping == EarlyStopping.Always && done.All(f => f);
            var merged = new float[beams + keep];
            finishedScores.CopyTo(merged, 0);
            for (var c = 0; c < keep; c++)
            {
                var s = score[c] / divisor;
                if (full)
                {
                    s += Shut;
                }

                if (!improvable)
                {
                    s += Shut;
                }

                if (!(ends[c] && c < beams))
                {
                    s += Shut;
                }

                merged[beams + c] = s;
            }

            var best = Top(merged, beams);
            var newFinished = new int[beams][];
            var newScores = new float[beams];
            var newDone = new bool[beams];
            for (var j = 0; j < beams; j++)
            {
                var m = best[j];
                newScores[j] = merged[m];
                if (m < beams)
                {
                    newFinished[j] = finished[m];
                    newDone[j] = done[m];
                }
                else
                {
                    var c = m - beams;
                    newFinished[j] = [.. running[parent[c]], token[c]];
                    newDone[j] = ends[c] && c < beams;
                }
            }

            (finished, finishedScores, done) = (newFinished, newScores, newDone);

            var parents = new int[beams];
            var newRunning = new int[beams][];
            for (var b = 0; b < beams; b++)
            {
                var c = next[b];
                parents[b] = parent[c];
                newRunning[b] = [.. running[parent[c]], token[c]];
                runningScores[b] = going[c];
                last[b] = token[c];
            }

            running = newRunning;
            session.Reorder(parents);
            length++;

            // can a running beam still beat the worst finished one?
            var hopeLength = settings.EarlyStopping == EarlyStopping.Never && settings.LengthPenalty > 0 ? settings.MaxLength - 1 : length - 1;
            var hope = runningScores[0] / (float)Math.Pow(hopeLength, settings.LengthPenalty);
            var worst = finishedScores.Min();
            var better = false;
            for (var j = 0; j < beams; j++)
            {
                better |= hope > (done[j] ? worst : Shut);
            }

            improvable &= better;
            var open = !(done.All(f => f) && settings.EarlyStopping == EarlyStopping.Always);
            if (!(improvable && open && !allEnd))
            {
                return new Hypothesis(finished[0], finishedScores[0]);
            }
        }
    }

    // forbidden tokens never come; at the last possible position only the end of sentence may come
    private static void Restrict(Span<float> row, int length, GenerationSettings settings)
    {
        foreach (var bad in settings.BadTokens)
        {
            row[bad] = float.NegativeInfinity;
        }

        if (settings.ForcedEosTokens.Length > 0 && length == settings.MaxLength - 1)
        {
            row.Fill(float.NegativeInfinity);
            foreach (var eos in settings.ForcedEosTokens)
            {
                row[eos] = 0;
            }
        }
    }

    private static double LogSumExp(ReadOnlySpan<float> row)
    {
        var max = float.NegativeInfinity;
        foreach (var v in row)
        {
            max = MathF.Max(max, v);
        }

        double sum = 0;
        foreach (var v in row)
        {
            sum += Math.Exp(v - max);
        }

        return max + Math.Log(sum);
    }

    // the indices of the k largest values, largest first; equal values keep their order
    internal static int[] Top(ReadOnlySpan<float> values, int k)
    {
        var index = new int[k];
        var value = new float[k];
        var count = 0;
        for (var i = 0; i < values.Length; i++)
        {
            var v = values[i];
            if (count == k && !(v > value[k - 1]))
            {
                continue;
            }

            var p = count < k ? count++ : k - 1;
            while (p > 0 && v > value[p - 1])
            {
                value[p] = value[p - 1];
                index[p] = index[p - 1];
                p--;
            }

            value[p] = v;
            index[p] = i;
        }

        return count == k ? index : index[..count];
    }
}
