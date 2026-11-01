using System.IO.Compression;
using System.Text.Json.Nodes;
using Iris.Vision.Imaging;
using Iris.Vision.Layout;

namespace Iris.Tests;

/// <summary>O1 – pictures of pages: own PNG and JPEG readers against Pillow, page cleaning, slant and lines.</summary>
public static class VisionTests
{
    private static string Data(string name) => Path.Combine(AppContext.BaseDirectory, "testdata", "vision", name);

    private static JsonObject Expected => JsonNode.Parse(File.ReadAllText(Data("expected.json")))!.AsObject();

    private static byte[] ExpectedGray(string name)
    {
        using var input = new GZipStream(File.OpenRead(Data(name + ".gray.gz")), CompressionMode.Decompress);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }

    [Test]
    public static void PngKindsAreReadExactly()
    {
        foreach (var name in new[] { "page.png", "rgb.png", "rgba.png", "la.png", "palette.png", "gray16.png" })
        {
            var image = ImageFile.Read(Data(name));
            var meta = Expected[name]!;
            Assert.Equal(meta["width"]!.GetValue<int>(), image.Width, $"{name} width");
            Assert.Equal(meta["height"]!.GetValue<int>(), image.Height, $"{name} height");
            var expected = ExpectedGray(name);
            var worst = expected.Select((v, i) => Math.Abs(v - image.Pixels[i])).Max();
            Assert.True(worst <= 1, $"{name}: gray differs from the reference by up to {worst}");
        }
    }

    [Test]
    public static void JpegPhotosAreReadLikeLibjpeg()
    {
        foreach (var name in new[] { "photo.jpg", "restart.jpg", "sideways.jpg" })
        {
            var image = ImageFile.Read(Data(name));
            var meta = Expected[name]!;
            Assert.Equal(meta["width"]!.GetValue<int>(), image.Width, $"{name} width (turned as the camera meant)");
            Assert.Equal(meta["height"]!.GetValue<int>(), image.Height, $"{name} height");
            var expected = ExpectedGray(name);
            var differences = expected.Select((v, i) => Math.Abs(v - image.Pixels[i])).ToArray();
            var mean = differences.Average();
            var worst = differences.Max();
            // the inverse DCT of libjpeg works in integers, ours in float: a level or two apart, never more
            Assert.True(mean < 0.3 && worst <= 4, $"{name}: mean difference {mean:0.000}, largest {worst}");
        }
    }

    [Test]
    public static void PngIsWrittenAndReadBack()
    {
        var image = new GrayImage(37, 11);
        for (var i = 0; i < image.Pixels.Length; i++)
        {
            image.Pixels[i] = (byte)(i * 7 % 256);
        }

        Assert.Sequence(image.Pixels, Png.Read(Png.Write(image)).Pixels, "the same pixels");
        var damaged = Png.Write(image);
        damaged[40] ^= 0xFF;
        Assert.Throws<InvalidDataException>(() => Png.Read(damaged), "a damaged chunk is noticed");
        Assert.Throws<NotSupportedException>(() => ImageFile.Read([1, 2, 3, 4]), "not a picture");
    }

    [Test]
    public static void ImagesTurnAndScale()
    {
        var image = new GrayImage(3, 2, [1, 2, 3, 4, 5, 6]);
        Assert.Sequence([4, 1, 5, 2, 6, 3], image.Oriented(6).Pixels.Select(p => (int)p), "orientation 6: turned right");
        Assert.Sequence([3, 6, 2, 5, 1, 4], image.Oriented(8).Pixels.Select(p => (int)p), "orientation 8: turned left");
        Assert.Sequence([6, 5, 4, 3, 2, 1], image.Oriented(3).Pixels.Select(p => (int)p), "orientation 3: upside down");
        var flat = new GrayImage(4, 4, Enumerable.Repeat((byte)100, 16).ToArray());
        Assert.True(flat.Resized(2, 2).Pixels.All(p => p == 100) && flat.Resized(7, 5).Pixels.All(p => p == 100), "flat stays flat");
        Assert.Equal(2, image.Crop(1, 0, 2, 1).Width, "crop");
    }

    [Test]
    public static void APhotoOfAPageIsStraightenedAndItsLinesFound()
    {
        var clean = PageReader.Prepare(ImageFile.Read(Data("page.png")));
        Assert.True(Math.Abs(clean.Skew) <= 0.1, $"a straight scan stays straight ({clean.Skew}°)");
        Assert.Equal(Expected["page.png"]!["lines"]!.GetValue<int>(), clean.Lines.Count, "lines of the scan");

        var photo = PageReader.Prepare(ImageFile.Read(Data("photo.jpg")));
        var skew = Expected["photo.jpg"]!["skew"]!.GetValue<double>();
        Assert.True(Math.Abs(photo.Skew - skew) <= 0.15, $"the slant of the photo: {photo.Skew}°, made with {skew}°");
        var again = PageCleaner.Skew(photo.Ink, photo.Image.Width, photo.Image.Height);
        Assert.True(Math.Abs(again) <= 0.15, $"after turning it is straight ({again}°)");
        Assert.Equal(Expected["photo.jpg"]!["lines"]!.GetValue<int>(), photo.Lines.Count, "lines of the photo (uneven light)");
        Assert.True(photo.Lines.Zip(photo.Lines.Skip(1)).All(p => p.First.Y < p.Second.Y), "lines from the top down");
        Assert.True(photo.Lines.All(l => l.Width > 450 && l.Height is > 25 and < 70), "each line whole, one line high: " +
            string.Join(", ", photo.Lines.Select(l => $"{l.Width}×{l.Height}")));
    }
}
