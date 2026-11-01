namespace Iris.Vision.Imaging;

/// <summary>Reads a picture (PNG or JPEG, by its content, not its name) into gray, turned as the camera meant it.</summary>
public static class ImageFile
{
    public static GrayImage Read(byte[] data)
    {
        if (Png.IsPng(data))
        {
            return Png.Read(data);
        }

        if (Jpeg.IsJpeg(data))
        {
            var (image, orientation) = Jpeg.Read(data);
            return image.Oriented(orientation);
        }

        throw new NotSupportedException("IRIS reads pictures in PNG and JPEG (photos from a phone, scans)");
    }

    public static GrayImage Read(string path) => Read(File.ReadAllBytes(path));
}
