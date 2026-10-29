using System.Text.Json.Nodes;
using Iris.Numerics;

namespace Iris.Model;

/// <summary>
/// Writes an int8 copy of a model file: every weight matrix (the token vectors "embed" and every "*.w") becomes int8
/// with one scale per row ("*.scale"); biases, normalizations, blobs, config and provenance stay as they are.
/// The model takes about a quarter of the memory and of the time to read its weights.
/// </summary>
public static class Quantizer
{
    public static bool IsWeightMatrix(TensorInfo tensor) =>
        tensor.DType == "f32" && tensor.Shape.Length == 2 && (tensor.Name == "embed" || tensor.Name.EndsWith(".w", StringComparison.Ordinal));

    public static void Write(ModelFile source, string path)
    {
        var tensors = new List<TensorData>();
        foreach (var tensor in source.Tensors.OrderBy(t => t.Offset))
        {
            if (IsWeightMatrix(tensor))
            {
                var matrix = Matrix.FromFloats(source.Floats(tensor.Name), tensor.Shape[0], tensor.Shape[1]).ToInt8();
                tensors.AddRange(matrix.ToTensors(tensor.Name));
            }
            else
            {
                tensors.Add(source.Raw(tensor.Name));
            }
        }

        var provenance = (JsonObject)source.Provenance.DeepClone();
        provenance["quantized"] = "int8, symmetric, one scale per row (iris quantize)";
        ModelFile.Write(path, source.Kind, source.Config, provenance, tensors,
            source.BlobNames.Select(name => (name, source.Blob(name))).ToList());
    }
}
