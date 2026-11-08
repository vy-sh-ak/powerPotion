using System.Text;

namespace PowerPotion.Protocol;

/// <summary>Binary reader/writer for the PowerPotion wire format (little-endian).</summary>
public sealed class ProtocolWriter
{
    private readonly BinaryWriter _w;

    public ProtocolWriter(BinaryWriter w) => _w = w;

    public void Write(bool v) => _w.Write(v);
    public void Write(int v) => _w.Write(v);
    public void Write(uint v) => _w.Write(v);
    public void Write(long v) => _w.Write(v);
    public void Write(ulong v) => _w.Write(v);
    public void Write(float v) => _w.Write(v);
    public void Write(double v) => _w.Write(v);
    public void Write(byte[] b) { _w.Write(b.Length); _w.Write(b); }
    public void Write(string s)
    {
        var b = Encoding.UTF8.GetBytes(s ?? "");
        _w.Write(b.Length);
        _w.Write(b);
    }
    public void Write(float[] a)
    {
        _w.Write(a.Length);
        foreach (var v in a) _w.Write(v);
    }
    public void Write(uint[] a)
    {
        _w.Write(a.Length);
        foreach (var v in a) _w.Write(v);
    }
    public void Write(double[] a)
    {
        _w.Write(a.Length);
        foreach (var v in a) _w.Write(v);
    }
}

public sealed class ProtocolReader
{
    private readonly BinaryReader _r;

    public ProtocolReader(BinaryReader r) => _r = r;

    public bool ReadBool() => _r.ReadBoolean();
    public int ReadInt() => _r.ReadInt32();
    public uint ReadUInt() => _r.ReadUInt32();
    public long ReadLong() => _r.ReadInt64();
    public ulong ReadULong() => _r.ReadUInt64();
    public float ReadFloat() => _r.ReadSingle();
    public double ReadDouble() => _r.ReadDouble();
    public byte[] ReadBytes() => _r.ReadBytes(_r.ReadInt32());

    public string ReadString()
    {
        var b = ReadBytes();
        return b.Length == 0 ? string.Empty : Encoding.UTF8.GetString(b);
    }

    public float[] ReadFloats()
    {
        var n = _r.ReadInt32();
        var a = new float[n];
        for (int i = 0; i < n; i++) a[i] = _r.ReadSingle();
        return a;
    }

    public uint[] ReadUInts()
    {
        var n = _r.ReadInt32();
        var a = new uint[n];
        for (int i = 0; i < n; i++) a[i] = _r.ReadUInt32();
        return a;
    }

    public double[] ReadDoubles()
    {
        var n = _r.ReadInt32();
        var a = new double[n];
        for (int i = 0; i < n; i++) a[i] = _r.ReadDouble();
        return a;
    }
}

public sealed class TextureSlotIo
{
    public static void Write(ProtocolWriter w, TextureSlotDto t)
    {
        w.Write(t.File);
        w.Write(t.Transformation);
        w.Write(t.IsInverted);
        w.Write(t.Brightness);
        w.Write(t.Source);
        w.Write(t.WcsType);
    }

    public static TextureSlotDto Read(ProtocolReader r) => new()
    {
        File = r.ReadString(),
        Transformation = r.ReadFloats(),
        IsInverted = r.ReadBool(),
        Brightness = r.ReadFloat(),
        Source = r.ReadInt(),
        WcsType = r.ReadInt(),
    };
}

public sealed class MaterialIo
{
    public static void Write(ProtocolWriter w, MaterialDto m)
    {
        w.Write(m.Id);
        w.Write(m.Name);
        w.Write(m.DoubleSided);
        w.Write(m.Opacity);
        w.Write(m.DiffuseR); w.Write(m.DiffuseG); w.Write(m.DiffuseB);
        w.Write(m.ImageFade);
        w.Write(m.EmissiveR); w.Write(m.EmissiveG); w.Write(m.EmissiveB);
        w.Write(m.EmissiveStrength);
        w.Write(m.TintR); w.Write(m.TintG); w.Write(m.TintB);
        w.Write(m.Roughness);
        w.Write(m.BumpAmount);
        w.Write(m.Metallic);
        w.Write(m.Specular);
        w.Write(m.Refraction);
        w.Write(m.MaterialType);
        w.Write(m.BumpMapKind);
        w.Write(m.IsSolidGlass);
        TextureSlotIo.Write(w, m.DiffuseTexture);
        TextureSlotIo.Write(w, m.BumpTexture);
        TextureSlotIo.Write(w, m.RoughnessTexture);
        TextureSlotIo.Write(w, m.MaskTexture);
    }

    public static MaterialDto Read(ProtocolReader r) => new()
    {
        Id = r.ReadInt(),
        Name = r.ReadString(),
        DoubleSided = r.ReadBool(),
        Opacity = r.ReadFloat(),
        DiffuseR = r.ReadFloat(), DiffuseG = r.ReadFloat(), DiffuseB = r.ReadFloat(),
        ImageFade = r.ReadFloat(),
        EmissiveR = r.ReadFloat(), EmissiveG = r.ReadFloat(), EmissiveB = r.ReadFloat(),
        EmissiveStrength = r.ReadFloat(),
        TintR = r.ReadFloat(), TintG = r.ReadFloat(), TintB = r.ReadFloat(),
        Roughness = r.ReadFloat(),
        BumpAmount = r.ReadFloat(),
        Metallic = r.ReadFloat(),
        Specular = r.ReadFloat(),
        Refraction = r.ReadFloat(),
        MaterialType = r.ReadULong(),
        BumpMapKind = r.ReadInt(),
        IsSolidGlass = r.ReadBool(),
        DiffuseTexture = TextureSlotIo.Read(r),
        BumpTexture = TextureSlotIo.Read(r),
        RoughnessTexture = TextureSlotIo.Read(r),
        MaskTexture = TextureSlotIo.Read(r),
    };
}

public sealed class WorldSnapshot
{
    /// <summary>Monotonic scene revision from the plugin; 0 = unversioned (tests/legacy).</summary>
    public long Revision;
    public string DocumentName = "";
    public List<MaterialDto> Materials = new();
    public List<ModelDto> Models = new();
    public List<InstanceDto> Instances = new();
    public List<LightDto> Lights = new();
    public SunUpdate Sun = new();
    public CameraUpdate Camera = new();
    public string ViewName = "";
}
