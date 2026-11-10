using System.IO;
using System.Text;

namespace PowerPotion.Rendering.Capture;

/// <summary>
/// Minimal uncompressed scanline OpenEXR writer (float RGB). No third-party EXR dependency.
/// Accepts either linear float RGB or BGRA bytes (linearised from sRGB).
/// </summary>
internal static class ExrWriter
{
    public static void WriteBgra(string path, int width, int height, ReadOnlySpan<byte> bgra)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (bgra.Length < width * height * 4)
            throw new ArgumentException("Pixel buffer is smaller than the image.", nameof(bgra));

        var rgb = new float[width * height * 3];
        for (int i = 0; i < width * height; i++)
        {
            int src = i * 4;
            rgb[i * 3] = MathF.Pow(bgra[src + 2] / 255f, 2.2f);
            rgb[i * 3 + 1] = MathF.Pow(bgra[src + 1] / 255f, 2.2f);
            rgb[i * 3 + 2] = MathF.Pow(bgra[src] / 255f, 2.2f);
        }
        WriteFloatRgb(path, width, height, rgb);
    }

    /// <summary>Writes linear float RGB (3 floats per pixel, R,G,B order).</summary>
    public static void WriteFloatRgb(string path, int width, int height, ReadOnlySpan<float> rgb)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (rgb.Length < width * height * 3)
            throw new ArgumentException("Pixel buffer is smaller than the image.", nameof(rgb));

        var header = new List<byte>();
        void CString(string value)
        {
            header.AddRange(Encoding.ASCII.GetBytes(value));
            header.Add(0);
        }
        void Attribute(string name, string type, byte[] data)
        {
            CString(name);
            CString(type);
            header.AddRange(BitConverter.GetBytes(data.Length));
            header.AddRange(data);
        }

        header.AddRange(BitConverter.GetBytes(0x01312f76u));
        header.AddRange(BitConverter.GetBytes(2)); // version 2, scanline

        // Channel list: B, G, R (the order the scanline blocks are written in).
        var channels = new List<byte>();
        foreach (var name in new[] { "B", "G", "R" })
        {
            channels.AddRange(Encoding.ASCII.GetBytes(name));
            channels.Add(0);
            channels.AddRange(BitConverter.GetBytes(2)); // FLOAT
            channels.Add(0); // pLinear
            channels.AddRange(new byte[3]);
            channels.AddRange(BitConverter.GetBytes(1)); // xSampling
            channels.AddRange(BitConverter.GetBytes(1)); // ySampling
        }
        channels.Add(0);
        Attribute("channels", "chlist", channels.ToArray());
        Attribute("compression", "compression", new byte[] { 0 }); // NO_COMPRESSION

        var box = new List<byte>();
        box.AddRange(BitConverter.GetBytes(0));
        box.AddRange(BitConverter.GetBytes(0));
        box.AddRange(BitConverter.GetBytes(width - 1));
        box.AddRange(BitConverter.GetBytes(height - 1));
        Attribute("dataWindow", "box2i", box.ToArray());
        Attribute("displayWindow", "box2i", box.ToArray());
        Attribute("lineOrder", "lineOrder", new byte[] { 0 }); // INCREASING_Y
        Attribute("pixelAspectRatio", "float", BitConverter.GetBytes(1f));
        Attribute("screenWindowCenter", "v2f", new byte[8]);
        Attribute("screenWindowWidth", "float", BitConverter.GetBytes(1f));
        header.Add(0); // end of header

        long headerEnd = header.Count;
        int blockSize = 8 + width * 3 * 4;
        var offsets = new byte[height * 8];
        for (int y = 0; y < height; y++)
            BitConverter.GetBytes(headerEnd + height * 8 + y * blockSize).CopyTo(offsets, y * 8);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = File.Create(path);
        stream.Write(header.ToArray());
        stream.Write(offsets);

        var scanline = new byte[blockSize];
        for (int y = 0; y < height; y++)
        {
            BitConverter.GetBytes(y).CopyTo(scanline, 0);
            BitConverter.GetBytes(width * 3 * 4).CopyTo(scanline, 4);
            int dst = 8;
            for (int channel = 0; channel < 3; channel++)
            {
                int offset = channel == 0 ? 2 : channel == 1 ? 1 : 0; // B, G, R
                for (int x = 0; x < width; x++)
                {
                    int i = (y * width + x) * 3 + offset;
                    BitConverter.GetBytes(rgb[i]).CopyTo(scanline, dst);
                    dst += 4;
                }
            }
            stream.Write(scanline);
        }
    }
}
