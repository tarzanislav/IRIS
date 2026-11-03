using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Iris.Vision.Training;

namespace Iris.Lab;

/// <summary>
/// The weights of the line reader as one block of bytes for the network: the learnable tensors and the running
/// statistics of the normalizations, always in the same order, float32 little endian; the header names them with
/// their lengths, so both ends check that they speak of the same model.
/// </summary>
public static class TensorPack
{
    /// <summary>All tensors that travel: what is learned and the running statistics (they are averaged too).</summary>
    public static IEnumerable<OcrTensor> Tensors(OcrWeights w) => w.Learnable().Concat(w.Statistics());

    public static (JsonArray Layout, byte[] Data) Pack(OcrWeights w) => Pack(Tensors(w).Select(t => (t.Name, t.Values)));

    public static (JsonArray Layout, byte[] Data) Pack(IEnumerable<(string Name, float[] Values)> tensors)
    {
        var list = tensors.ToList();
        var layout = new JsonArray();
        var data = new byte[list.Sum(t => (long)t.Values.Length) * sizeof(float)];
        var offset = 0;
        foreach (var (name, values) in list)
        {
            layout.Add((JsonNode)new JsonObject { ["name"] = name, ["length"] = values.Length });
            MemoryMarshal.AsBytes(values.AsSpan()).CopyTo(data.AsSpan(offset));
            offset += values.Length * sizeof(float);
        }

        return (layout, data);
    }

    /// <summary>Reads packed tensors into the tensors of w (names and lengths must match).</summary>
    public static void Unpack(JsonArray layout, ReadOnlySpan<byte> data, OcrWeights w) => Unpack(layout, data, Tensors(w).Select(t => (t.Name, t.Values)));

    public static void Unpack(JsonArray layout, ReadOnlySpan<byte> data, IEnumerable<(string Name, float[] Values)> targets)
    {
        var list = targets.ToList();
        if (layout.Count != list.Count)
        {
            throw new LabProtocolException("the tensors do not fit the model");
        }

        var offset = 0;
        for (var i = 0; i < list.Count; i++)
        {
            var (name, values) = list[i];
            if (layout[i]!["name"]!.GetValue<string>() != name || layout[i]!["length"]!.GetValue<int>() != values.Length)
            {
                throw new LabProtocolException($"tensor {i} is not {name} of {values.Length}");
            }

            var bytes = values.Length * sizeof(float);
            if (offset + bytes > data.Length)
            {
                throw new LabProtocolException("the tensors end too early");
            }

            data.Slice(offset, bytes).CopyTo(MemoryMarshal.AsBytes(values.AsSpan()));
            offset += bytes;
        }

        if (offset != data.Length)
        {
            throw new LabProtocolException("more data than tensors");
        }
    }

    /// <summary>What changed: after − before, tensor by tensor (learnable and statistics).</summary>
    public static List<(string Name, float[] Values)> Difference(OcrWeights after, OcrWeights before) =>
        [.. Tensors(after).Zip(Tensors(before), (a, b) => (a.Name, a.Values.Zip(b.Values, (x, y) => x - y).ToArray()))];
}

/// <summary>
/// The averaging of what the computers learned in a round (own implementation of local SGD / federated averaging):
/// every computer starts from the same weights, learns on its own lines, and sends the change; the new weights are
/// the old ones plus the mean of the changes, weighted by how many lines each computer learned from – a faster
/// computer counts for more, one that learned nothing for nothing.
/// </summary>
public sealed class RoundAverage
{
    private readonly OcrWeights _shape;
    private double[][]? _sum;
    private long _lines;

    public RoundAverage(OcrWeights shape) => _shape = shape;

    public int Parts { get; private set; }

    public long Lines => _lines;

    public void Add(IReadOnlyList<float[]> change, long lines)
    {
        if (lines <= 0)
        {
            return;
        }

        var tensors = TensorPack.Tensors(_shape).ToList();
        if (change.Count != tensors.Count)
        {
            throw new ArgumentException("the change does not fit the model");
        }

        _sum ??= [.. tensors.Select(t => new double[t.Values.Length])];
        for (var i = 0; i < change.Count; i++)
        {
            var sum = _sum[i];
            var values = change[i];
            for (var k = 0; k < sum.Length; k++)
            {
                sum[k] += lines * (double)values[k];
            }
        }

        _lines += lines;
        Parts++;
    }

    /// <summary>Adds the weighted mean of the changes to the weights; false when there was nothing to add.</summary>
    public bool ApplyTo(OcrWeights weights)
    {
        if (_sum is null || _lines == 0)
        {
            return false;
        }

        var i = 0;
        foreach (var t in TensorPack.Tensors(weights))
        {
            var sum = _sum[i++];
            for (var k = 0; k < sum.Length; k++)
            {
                t.Values[k] += (float)(sum[k] / _lines);
            }
        }

        return true;
    }
}
