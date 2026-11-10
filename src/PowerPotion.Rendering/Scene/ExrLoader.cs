using System.IO;
using System.IO.Compression;
using System.Text;

namespace PowerPotion.Rendering.Scene;

/// <summary>
/// Minimal OpenEXR reader for equirectangular skyboxes: uncompressed and ZIP/ZIPS scanline blocks,
/// HALF and FLOAT channels. PIZ/DWAA/RLE are rejected (P3 scope).
/// </summary>
internal static class ExrLoader
{
    private const uint Magic = 0x01312f76;

    public static HdrImage Load(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: false);

        if (reader.ReadUInt32() != Magic)
            throw new InvalidDataException("Not an OpenEXR file.");

        int version = reader.ReadInt32();
        if ((version & 0xFF) != 2)
            throw new InvalidDataException($"Unsupported EXR version {version & 0xFF}.");

        var channels = new List<ExrChannel>();
        int compression = 0;
        int xMin = 0, yMin = 0, xMax = -1, yMax = -1;

        while (true)
        {
            var name = ReadCString(reader);
            if (name.Length == 0) break;
            var type = ReadCString(reader);
            int size = reader.ReadInt32();
            long position = stream.Position;

            switch (name)
            {
                case "channels":
                    channels = ParseChannels(reader);
                    break;
                case "compression":
                    compression = reader.ReadByte();
                    break;
                case "dataWindow":
                    xMin = reader.ReadInt32();
                    yMin = reader.ReadInt32();
                    xMax = reader.ReadInt32();
                    yMax = reader.ReadInt32();
                    break;
            }

            stream.Position = position + size;
        }

        if (xMax < xMin || yMax < yMin)
            throw new InvalidDataException("EXR data window is invalid.");
        if (compression != 0 && compression != 2 && compression != 3)
            throw new InvalidDataException($"Unsupported EXR compression {compression}.");

        int width = xMax - xMin + 1;
        int height = yMax - yMin + 1;
        int blockRows = compression == 3 ? 16 : 1;
        int blockCount = (height + blockRows - 1) / blockRows;
        var offsets = new long[blockCount];
        for (int i = 0; i < blockCount; i++) offsets[i] = reader.ReadInt64();

        var result = new HdrImage { Width = width, Height = height, Rgba = new float[width * height * 4] };

        for (int block = 0; block < blockCount; block++)
        {
            stream.Position = offsets[block];
            int yCoordinate = reader.ReadInt32();
            int dataSize = reader.ReadInt32();
            var compressed = reader.ReadBytes(dataSize);
            var data = compression == 0 ? compressed : Decompress(compressed);

            int rows = Math.Min(blockRows, height - (yCoordinate - yMin));
            int offset = 0;

            for (int row = 0; row < rows; row++)
            {
                int targetRow = yCoordinate - yMin + row;
                if ((uint)targetRow >= (uint)height) continue;
                foreach (var channel in channels)
                {
                    for (int x = 0; x < width; x++)
                    {
                        float value = ReadSample(data, offset, channel.PixelType);
                        offset += channel.BytesPerSample;
                        int pixel = (targetRow * width + x) * 4;
                        switch (channel.Name)
                        {
                            case "R": result.Rgba[pixel] = value; break;
                            case "G": result.Rgba[pixel + 1] = value; break;
                            case "B": result.Rgba[pixel + 2] = value; break;
                            case "A": result.Rgba[pixel + 3] = value; break;
                        }
                    }
                }
            }
        }

        for (int i = 0; i < result.Rgba.Length; i += 4)
            if (result.Rgba[i + 3] == 0) result.Rgba[i + 3] = 1f;
        return result;
    }

    private static List<ExrChannel> ParseChannels(BinaryReader reader)
    {
        var channels = new List<ExrChannel>();
        while (true)
        {
            var name = ReadCString(reader);
            if (name.Length == 0) break;
            int pixelType = reader.ReadInt32();
            reader.ReadByte(); // pLinear
            reader.ReadBytes(3); // reserved
            int xSampling = reader.ReadInt32();
            int ySampling = reader.ReadInt32();
            channels.Add(new ExrChannel
            {
                Name = name,
                PixelType = pixelType,
                BytesPerSample = pixelType == 1 ? 2 : pixelType == 2 ? 4 : 4,
            });
        }
        return channels;
    }

    private static byte[] Decompress(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }

    private static float ReadSample(byte[] data, int offset, int pixelType)
    {
        return pixelType switch
        {
            1 => (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(data, offset)),
            2 => BitConverter.ToSingle(data, offset),
            0 => BitConverter.ToUInt32(data, offset),
            _ => 0f,
        };
    }

    private static string ReadCString(BinaryReader reader)
    {
        var bytes = new List<byte>();
        while (true)
        {
            byte b = reader.ReadByte();
            if (b == 0) break;
            bytes.Add(b);
        }
        return Encoding.ASCII.GetString(bytes.ToArray());
    }

    private sealed class ExrChannel
    {
        public string Name = "";
        public int PixelType;
        public int BytesPerSample;
    }
}
