using System.IO;
using System.Text;

namespace PowerPotion.Rendering.Scene;

internal sealed class HdrImage
{
    public int Width;
    public int Height;
    public float[] Rgba = Array.Empty<float>();
}

/// <summary>Minimal Radiance RGBE (.hdr) reader for equirectangular skyboxes.</summary>
internal static class HdrLoader
{
    public static HdrImage Load(string path)
    {
        var data = File.ReadAllBytes(path);
        int pos = 0;

        // Header
        while (pos < data.Length)
        {
            int lineStart = pos;
            while (pos < data.Length && data[pos] != '\n') pos++;
            if (pos >= data.Length) throw new InvalidDataException("HDR header truncated.");
            var line = Encoding.ASCII.GetString(data, lineStart, pos - lineStart).TrimEnd('\r');
            pos++;
            if (line.Length == 0) break;
            if (line.StartsWith("FORMAT=", StringComparison.Ordinal) &&
                !line.Contains("32-bit_rle_rgbe", StringComparison.OrdinalIgnoreCase) &&
                !line.Contains("32-bit_rle_xyze", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Unsupported HDR format: {line}");
        }

        // Resolution: -Y height +X width
        while (pos < data.Length)
        {
            int lineStart = pos;
            while (pos < data.Length && data[pos] != '\n') pos++;
            if (pos >= data.Length) throw new InvalidDataException("HDR resolution missing.");
            var line = Encoding.ASCII.GetString(data, lineStart, pos - lineStart).Trim();
            pos++;
            if (line.Length == 0) continue;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 4) throw new InvalidDataException($"Bad HDR resolution: {line}");
            if (parts[0] != "-Y" || parts[2] != "+X")
                throw new InvalidDataException("Only -Y +X HDR orientation is supported.");
            int height = int.Parse(parts[1]);
            int width = int.Parse(parts[3]);
            var image = new HdrImage
            {
                Width = width,
                Height = height,
                Rgba = new float[width * height * 4],
            };

            var scanline = new byte[width * 4];
            for (int y = 0; y < height; y++)
            {
                ReadScanline(data, ref pos, scanline, width);
                for (int x = 0; x < width; x++)
                {
                    int src = x * 4;
                    int dst = (y * width + x) * 4;
                    DecodeRgbe(scanline[src], scanline[src + 1], scanline[src + 2], scanline[src + 3],
                        image.Rgba, dst);
                }
            }
            return image;
        }

        throw new InvalidDataException("HDR resolution missing.");
    }

    private static void ReadScanline(byte[] data, ref int pos, byte[] scanline, int width)
    {
        // New RLE scanline
        if (width >= 8 && width < 32768 && pos + 4 <= data.Length &&
            data[pos] == 2 && data[pos + 1] == 2 && ((data[pos + 2] << 8) | data[pos + 3]) == width)
        {
            pos += 4;
            for (int c = 0; c < 4; c++)
            {
                int x = 0;
                while (x < width)
                {
                    int count = data[pos++];
                    if (count > 128)
                    {
                        int run = count - 128;
                        byte value = data[pos++];
                        for (int i = 0; i < run && x < width; i++)
                            scanline[(x++) * 4 + c] = value;
                    }
                    else
                    {
                        for (int i = 0; i < count && x < width; i++)
                            scanline[(x++) * 4 + c] = data[pos++];
                    }
                }
            }
            return;
        }

        // Old flat format
        for (int i = 0; i < width; i++)
        {
            scanline[i * 4] = data[pos++];
            scanline[i * 4 + 1] = data[pos++];
            scanline[i * 4 + 2] = data[pos++];
            scanline[i * 4 + 3] = data[pos++];
        }
    }

    private static void DecodeRgbe(byte r, byte g, byte b, byte e, float[] target, int offset)
    {
        if (e == 0)
        {
            target[offset] = 0; target[offset + 1] = 0; target[offset + 2] = 0; target[offset + 3] = 1;
            return;
        }

        float f = MathF.ScaleB(1f, e - (128 + 8));
        target[offset] = r * f;
        target[offset + 1] = g * f;
        target[offset + 2] = b * f;
        target[offset + 3] = 1f;
    }
}
