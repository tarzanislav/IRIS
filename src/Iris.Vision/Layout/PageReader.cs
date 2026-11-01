using Iris.Vision.Imaging;

namespace Iris.Vision.Layout;

/// <summary>A page made ready to read: turned straight, its slant, its ink and its lines.</summary>
public sealed record PreparedPage(GrayImage Image, double Skew, bool[] Ink, IReadOnlyList<TextLine> Lines);

/// <summary>The steps from a picture of a page to its lines of text.</summary>
public static class PageReader
{
    public static PreparedPage Prepare(GrayImage picture)
    {
        // the slant is measured on a smaller copy (enough for an angle, much quicker)
        var scale = Math.Min(1.0, 1600.0 / Math.Max(picture.Width, picture.Height));
        var small = scale < 1 ? picture.Resized(Math.Max(1, (int)(picture.Width * scale)), Math.Max(1, (int)(picture.Height * scale))) : picture;
        var skew = PageCleaner.Skew(PageCleaner.Binarize(small), small.Width, small.Height);
        var straight = PageCleaner.Rotate(picture, -skew);
        var ink = PageCleaner.Binarize(straight);
        return new PreparedPage(straight, skew, ink, LineFinder.Find(straight, ink));
    }
}
