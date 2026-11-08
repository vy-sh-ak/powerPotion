using System.Text;

namespace PowerPotion.Protocol;

/// <summary>Encodes/decodes framed messages to/from streams.</summary>
public static class MessageCodec
{
    public const uint CurrentVersion = 1;

    public static void Send(Stream stream, MessageType type, Action<ProtocolWriter>? body = null)
    {
        using var payload = new MemoryStream();
        using (var bw = new BinaryWriter(payload, Encoding.UTF8, leaveOpen: true))
        {
            body?.Invoke(new ProtocolWriter(bw));
        }
        var bytes = payload.ToArray();
        using var header = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        header.Write((uint)bytes.Length);
        header.Write((ushort)type);
        header.Flush();
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush();
    }

    public static async Task SendAsync(Stream stream, MessageType type, Action<ProtocolWriter>? body = null)
    {
        using var payload = new MemoryStream();
        using (var bw = new BinaryWriter(payload, Encoding.UTF8, leaveOpen: true))
        {
            body?.Invoke(new ProtocolWriter(bw));
        }
        var bytes = payload.ToArray();
        var frame = new byte[6 + bytes.Length];
        BitConverter.GetBytes((uint)bytes.Length).CopyTo(frame, 0);
        BitConverter.GetBytes((ushort)type).CopyTo(frame, 4);
        bytes.CopyTo(frame, 6);
        await stream.WriteAsync(frame);
        await stream.FlushAsync();
    }

    /// <summary>Reads one frame. Returns null when the stream ends.</summary>
    public static async Task<(MessageType Type, byte[] Payload)?> ReadFrameAsync(Stream stream, CancellationToken ct = default)
    {
        var header = new byte[6];
        if (!await ReadExactAsync(stream, header, ct)) return null;
        uint len = BitConverter.ToUInt32(header, 0);
        if (len > 512 * 1024 * 1024) throw new IOException("Frame too large: " + len);
        var payload = new byte[len];
        if (len > 0 && !await ReadExactAsync(stream, payload, ct)) return null;
        return ((MessageType)BitConverter.ToUInt16(header, 4), payload);
    }

    private static async Task<bool> ReadExactAsync(Stream s, byte[] buffer, CancellationToken ct)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            var n = await s.ReadAsync(buffer.AsMemory(read, buffer.Length - read), ct);
            if (n <= 0) return false;
            read += n;
        }
        return true;
    }

    // ------------------------------------------------------------------
    // Typed payload decoding
    // ------------------------------------------------------------------

    public static object Decode(MessageType type, byte[] payload)
    {
        using var ms = new MemoryStream(payload);
        using var br = new BinaryReader(ms, Encoding.UTF8);
        var r = new ProtocolReader(br);
        return type switch
        {
            MessageType.Hello => new Hello
            {
                ProtocolVersion = r.ReadUInt(),
                DocumentName = r.ReadString(),
                EngineVersion = r.ReadString(),
                HostVersion = r.ReadString(),
            },
            MessageType.EngineReady => new EngineReady { Started = r.ReadBool(), Error = r.ReadString() },
            MessageType.WorldSnapshot => DecodeWorld(r),
            MessageType.CameraUpdate => DecodeCamera(r),
            MessageType.SunUpdate => DecodeSun(r),
            MessageType.ConsoleCommand => new ConsoleCommand { Command = r.ReadString(), Deferred = r.ReadBool() },
            MessageType.CaptureRequest => new CaptureRequest { FilePath = r.ReadString(), Width = r.ReadInt(), Height = r.ReadInt(), Panorama = r.ReadBool() },
            MessageType.CaptureResult => new CaptureResult { Ok = r.ReadBool(), FilePath = r.ReadString(), Error = r.ReadString() },
            MessageType.MaterialUpdate => new MaterialUpdate { Material = MaterialIo.Read(r) },
            MessageType.LiveUpdateState => new LiveUpdateState { Paused = r.ReadBool() },
            MessageType.Status => new Status { Text = r.ReadString(), Level = r.ReadInt() },
            MessageType.ViewActivation => new ViewActivation { Name = r.ReadString(), Camera = DecodeCamera(r), Sun = DecodeSun(r) },
            MessageType.RendererClosed => new RendererClosed { Reason = r.ReadString() },
            MessageType.Shutdown => new Shutdown(),
            _ => throw new InvalidDataException($"Unknown message type {type}"),
        };
    }

    public static WorldSnapshot DecodeWorld(ProtocolReader r)
    {
        var world = new WorldSnapshot
        {
            DocumentName = r.ReadString(),
            ViewName = r.ReadString(),
        };
        int nMat = r.ReadInt();
        for (int i = 0; i < nMat; i++) world.Materials.Add(MaterialIo.Read(r));

        int nMod = r.ReadInt();
        for (int i = 0; i < nMod; i++)
        {
            var m = new ModelDto
            {
                Id = r.ReadInt(),
                FamilyClass = r.ReadULong(),
                Positions = r.ReadFloats(),
                Normals = r.ReadFloats(),
                Uvs = r.ReadFloats(),
                BacksideUvs = r.ReadFloats(),
                Indices = r.ReadUInts(),
            };
            int nGroups = r.ReadInt();
            m.Groups = new MaterialIndexGroupDto[nGroups];
            for (int g = 0; g < nGroups; g++)
            {
                m.Groups[g] = new MaterialIndexGroupDto
                {
                    StartIndex = r.ReadUInt(),
                    NumIndices = r.ReadUInt(),
                    FrontFaceMaterialId = r.ReadUInt(),
                    BackFaceMaterialId = r.ReadUInt(),
                    VisibleLodStart = r.ReadUInt(),
                    VisibleLodEnd = r.ReadUInt(),
                };
            }
            world.Models.Add(m);
        }

        int nInst = r.ReadInt();
        for (int i = 0; i < nInst; i++)
        {
            world.Instances.Add(new InstanceDto
            {
                ModelId = r.ReadInt(),
                InstanceId = r.ReadInt(),
                Matrix = r.ReadFloats(),
            });
        }

        int nLights = r.ReadInt();
        for (int i = 0; i < nLights; i++)
        {
            var l = new LightDto
            {
                Id = r.ReadInt(),
                X = r.ReadFloat(), Y = r.ReadFloat(), Z = r.ReadFloat(),
                R = r.ReadFloat(), G = r.ReadFloat(), B = r.ReadFloat(), A = r.ReadFloat(),
                Intensity = r.ReadFloat(),
                Distribution = r.ReadInt(),
                Shape = r.ReadInt(),
                BeamAngle = r.ReadFloat(),
                Rotation = r.ReadFloats(),
                ShapeParam1 = r.ReadFloat(),
                ShapeParam2 = r.ReadFloat(),
                IesPath = r.ReadString(),
            };
            if (l.Rotation.Length != 9) l.Rotation = new float[9];
            world.Lights.Add(l);
        }

        world.Sun = DecodeSun(r);
        world.Camera = DecodeCamera(r);
        return world;
    }

    public static CameraUpdate DecodeCamera(ProtocolReader r) => new()
    {
        PosX = r.ReadFloat(), PosY = r.ReadFloat(), PosZ = r.ReadFloat(),
        QuatX = r.ReadFloat(), QuatY = r.ReadFloat(), QuatZ = r.ReadFloat(), QuatW = r.ReadFloat(),
        FovDegrees = r.ReadFloat(),
        FovType = r.ReadInt(),
        ResX = r.ReadUInt(), ResY = r.ReadUInt(),
        Ortho = r.ReadBool(),
        OrthoLeft = r.ReadFloat(), OrthoRight = r.ReadFloat(), OrthoBottom = r.ReadFloat(), OrthoTop = r.ReadFloat(),
        AutoExposure = r.ReadBool(),
        Exposure = r.ReadFloat(),
        TwoPointPerspective = r.ReadBool(),
    };

    public static SunUpdate DecodeSun(ProtocolReader r) => new()
    {
        Mode = r.ReadInt(),
        Latitude = r.ReadDouble(),
        Longitude = r.ReadDouble(),
        NorthAngle = r.ReadDouble(),
        TimeZoneHours = r.ReadDouble(),
        Day = r.ReadInt(), Month = r.ReadInt(), Year = r.ReadInt(),
        Hour = r.ReadFloat(),
        SunX = r.ReadFloat(), SunY = r.ReadFloat(), SunZ = r.ReadFloat(),
    };

    // ------------------------------------------------------------------
    // Typed payload encoding
    // ------------------------------------------------------------------

    public static void WriteWorld(ProtocolWriter w, WorldSnapshot world)
    {
        w.Write(world.DocumentName);
        w.Write(world.ViewName);

        w.Write(world.Materials.Count);
        foreach (var m in world.Materials) MaterialIo.Write(w, m);

        w.Write(world.Models.Count);
        foreach (var m in world.Models)
        {
            w.Write(m.Id);
            w.Write(m.FamilyClass);
            w.Write(m.Positions);
            w.Write(m.Normals);
            w.Write(m.Uvs);
            w.Write(m.BacksideUvs);
            w.Write(m.Indices);
            w.Write(m.Groups.Length);
            foreach (var g in m.Groups)
            {
                w.Write(g.StartIndex);
                w.Write(g.NumIndices);
                w.Write(g.FrontFaceMaterialId);
                w.Write(g.BackFaceMaterialId);
                w.Write(g.VisibleLodStart);
                w.Write(g.VisibleLodEnd);
            }
        }

        w.Write(world.Instances.Count);
        foreach (var i in world.Instances)
        {
            w.Write(i.ModelId);
            w.Write(i.InstanceId);
            w.Write(i.Matrix);
        }

        w.Write(world.Lights.Count);
        foreach (var l in world.Lights)
        {
            w.Write(l.Id);
            w.Write(l.X); w.Write(l.Y); w.Write(l.Z);
            w.Write(l.R); w.Write(l.G); w.Write(l.B); w.Write(l.A);
            w.Write(l.Intensity);
            w.Write(l.Distribution);
            w.Write(l.Shape);
            w.Write(l.BeamAngle);
            w.Write(l.Rotation);
            w.Write(l.ShapeParam1);
            w.Write(l.ShapeParam2);
            w.Write(l.IesPath);
        }

        WriteSun(w, world.Sun);
        WriteCamera(w, world.Camera);
    }

    public static void WriteCamera(ProtocolWriter w, CameraUpdate c)
    {
        w.Write(c.PosX); w.Write(c.PosY); w.Write(c.PosZ);
        w.Write(c.QuatX); w.Write(c.QuatY); w.Write(c.QuatZ); w.Write(c.QuatW);
        w.Write(c.FovDegrees);
        w.Write(c.FovType);
        w.Write(c.ResX); w.Write(c.ResY);
        w.Write(c.Ortho);
        w.Write(c.OrthoLeft); w.Write(c.OrthoRight); w.Write(c.OrthoBottom); w.Write(c.OrthoTop);
        w.Write(c.AutoExposure);
        w.Write(c.Exposure);
        w.Write(c.TwoPointPerspective);
    }

    public static void WriteSun(ProtocolWriter w, SunUpdate s)
    {
        w.Write(s.Mode);
        w.Write(s.Latitude);
        w.Write(s.Longitude);
        w.Write(s.NorthAngle);
        w.Write(s.TimeZoneHours);
        w.Write(s.Day); w.Write(s.Month); w.Write(s.Year);
        w.Write(s.Hour);
        w.Write(s.SunX); w.Write(s.SunY); w.Write(s.SunZ);
    }
}
