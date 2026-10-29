using System.Text.Json.Nodes;
using Iris.Model;
using Iris.Numerics;

namespace Iris.Tests;

public static class ModelFileTests
{
    [Test]
    public static void TensorsAndBlobsComeBackAsWritten()
    {
        var path = Path.Combine(Path.GetTempPath(), $"iris-test-{Guid.NewGuid():N}.iris");
        try
        {
            float[] weights = [1.5f, -2f, 0f, 3.25f, 1e-7f, float.MaxValue];
            byte[] blob = [1, 2, 3, 250];
            ModelFile.Write(path, "test", new JsonObject { ["d_model"] = 3 }, new JsonObject { ["license"] = "Apache-2.0" },
                [("enc.0.w", [2, 3], weights), ("bias", [1], [7f])], [("source.spm", blob)]);

            using var model = ModelFile.Load(path);
            Assert.Equal("test", model.Kind);
            Assert.Equal(3, model.Config["d_model"]!.GetValue<int>());
            Assert.Sequence(weights, model.Floats("enc.0.w", 2, 3));
            Assert.Sequence([7f], model.Floats("bias"));
            Assert.Sequence(blob, model.Blob("source.spm"));
            Assert.Throws<FormatException>(() => model.Floats("enc.0.w", 3, 2), "wrong shape");
            Assert.Throws<FormatException>(() => model.Floats("missing"), "missing tensor");
            Assert.True(model.Tensors.All(t => t.Offset % 64 == 0), "tensors on 64-byte boundaries");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public static void OtherFilesAreRefused()
    {
        Assert.Throws<FormatException>(() => ModelFile.Parse("not a model file at all"u8.ToArray()), "other file");
    }

    [Test]
    public static void KernelsComputeWhatTheyShould()
    {
        // x [2×3] · Wᵀ (W [2×3]) + b
        var y = Kernels.Linear([1, 2, 3, 4, 5, 6], 2, [1, 0, 1, 0, 1, 0], [0.5f, -1], 3, 2);
        Assert.Sequence([4.5f, 1, 10.5f, 4], y);

        var dot = Kernels.Dot(Enumerable.Range(1, 37).Select(i => (float)i).ToArray(), Enumerable.Repeat(1f, 37).ToArray());
        Assert.Near(703, dot, 1e-3);

        float[] row = [1, 2, 3, 4];
        Kernels.LayerNorm(row, 1, 4, [1, 1, 1, 1], [0, 0, 0, 0]);
        Assert.Near(0, row.Sum(), 1e-5);
        Assert.Near(-1.3416, row[0], 1e-3);

        float[] p = [1, 2, 3];
        Kernels.Softmax(p);
        Assert.Near(1, p.Sum(), 1e-6);
        Assert.Near(0.66524, p[2], 1e-4);

        float[] l = [1, 2, 3];
        Kernels.LogSoftmax(l);
        Assert.Near(Math.Log(0.66524), l[2], 1e-4);

        float[] s = [0, 1, -1];
        Kernels.Silu(s);
        Assert.Near(0.73106, s[1], 1e-4);
        Assert.Near(-0.26894, s[2], 1e-4);
    }
}
