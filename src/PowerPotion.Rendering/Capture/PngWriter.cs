using System.IO;
using System.IO.Compression;
using System.Text;

namespace PowerPotion.Rendering.Capture;

/// <summary>
/// Minimal, dependency-free PNG writer (8-bit RGBA, no interlace). Used by the P0 capture path
/// and later by the renderer's screenshot/EXR pipeline.
/// </summary>
internal static class PngWriter
{
    private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>Writes a PNG from tightly packed BGRA pixels (Vulkan <c>B8G8R8A8_UNORM</c>).</summary>
    public static void WriteBgra(string path, int width, int height, ReadOnlySpan<byte> bgra)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (bgra.Length < width * height * 4)
            throw new ArgumentException("Pixel buffer is smaller than the image.", nameof(bgra));

        var raw = new byte[height * (1 + width * 4)];
        for (int y = 0; y < height; y++)
        {
            int src = y * width * 4;
            int dst = y * (1 + width * 4);
            raw[dst++] = 0; // filter type: None
            for (int x = 0; x < width; x++)
            {
                raw[dst++] = bgra[src + 2]; // R
                raw[dst++] = bgra[src + 1]; // G
                raw[dst++] = bgra[src];     // B
                raw[dst++] = bgra[src + 3]; // A
                src += 4;
            }
        }

        byte[] compressed;
        using (var ms = new MemoryStream())
        {
            using (var zlib = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
                zlib.Write(raw, 0, raw.Length);
            compressed = ms.ToArray();
        }

        var ihdr = new byte[13];
        WriteBigEndian(ihdr, 0, (uint)width);
        WriteBigEndian(ihdr, 4, (uint)height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 6;  // color type: RGBA
        ihdr[10] = 0; // compression
        ihdr[11] = 0; // filter
        ihdr[12] = 0; // interlace

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var fs = File.Create(path);
        fs.Write(Signature);
        WriteChunk(fs, "IHDR", ihdr);
        WriteChunk(fs, "IDAT", compressed);
        WriteChunk(fs, "IEND", Array.Empty<byte>());
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        WriteBigEndian(length, 0, (uint)data.Length);
        stream.Write(length);

        var typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);

        uint crc = UpdateCrc(0xFFFFFFFFu, typeBytes);
        crc = UpdateCrc(crc, data) ^ 0xFFFFFFFFu;
        Span<byte> crcBytes = stackalloc byte[4];
        WriteBigEndian(crcBytes, 0, crc);
        stream.Write(crcBytes);
    }

    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static void WriteBigEndian(Span<byte> target, int offset, uint value)
    {
        target[offset] = (byte)(value >> 24);
        target[offset + 1] = (byte)(value >> 16);
        target[offset + 2] = (byte)(value >> 8);
        target[offset + 3] = (byte)value;
    }
}
