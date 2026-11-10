using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PowerPotion.Rendering.Capture;

/// <summary>P5 capture dispatcher: PNG/JPG via WIC, EXR via the minimal writer.</summary>
internal static class CaptureWriter
{
    public static void Write(string path, int width, int height, byte[] bgra)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        switch (extension)
        {
            case ".jpg":
            case ".jpeg":
                WriteJpg(path, width, height, bgra);
                break;
            case ".exr":
                ExrWriter.WriteBgra(path, width, height, bgra);
                break;
            default:
                PngWriter.WriteBgra(path, width, height, bgra);
                break;
        }
    }

    private static void WriteJpg(string path, int width, int height, byte[] bgra)
    {
        var bitmap = BitmapSource.Create(
            width, height, 96, 96, PixelFormats.Bgra32, null, bgra, width * 4);
        var encoder = new JpegBitmapEncoder { QualityLevel = 92 };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
