// PowerPotion.Protocol — wire contract between the Revit add-in and the renderer host.
//
// Frame layout (little-endian):
//   [u32 payloadLength][u16 messageType][payload bytes]
//
// All geometry is in ENGINE space (meters, right-handed, Y-up, north = -Z),
// already converted from Revit internal units (feet, Z-up) by the plugin side.

namespace PowerPotion.Protocol;

public enum MessageType : ushort
{
    /// <summary>Host → plugin after connect. Payload: Hello.</summary>
    Hello = 1,
    /// <summary>Plugin → host: full world (models, materials, lights, sun, section planes).</summary>
    WorldSnapshot = 2,
    /// <summary>Plugin → host: camera change.</summary>
    CameraUpdate = 3,
    /// <summary>Plugin → host: sun / location change.</summary>
    SunUpdate = 4,
    /// <summary>Plugin → host or host → plugin: execute engine console command (CVar).</summary>
    ConsoleCommand = 5,
    /// <summary>Plugin → host: capture a screenshot to a file path.</summary>
    CaptureRequest = 6,
    /// <summary>Host → plugin: capture finished (or failed).</summary>
    CaptureResult = 7,
    /// <summary>Plugin → host: replace one material live.</summary>
    MaterialUpdate = 8,
    /// <summary>Host → plugin: renderer window closed / engine stopped.</summary>
    RendererClosed = 9,
    /// <summary>Plugin → host: pause/resume live updates from Revit.</summary>
    LiveUpdateState = 10,
    /// <summary>Both: free-form status/log line (diagnostics).</summary>
    Status = 11,
    /// <summary>Plugin → host: activate a stored view (name + camera payload).</summary>
    ViewActivation = 12,
    /// <summary>Either side: graceful shutdown of the connection.</summary>
    Shutdown = 13,
    /// <summary>Host → plugin: engine started, world may be pushed.</summary>
    EngineReady = 14,
    /// <summary>Plugin → host: active section/clip planes (xyzd, up to 8).</summary>
    SectionPlanesUpdate = 15,
}

// ---------------------------------------------------------------------------
// DTOs
// ---------------------------------------------------------------------------

public sealed class Hello
{
    public uint ProtocolVersion = 1;
    public string DocumentName = "";
    public string EngineVersion = "";
    public string HostVersion = "";

    public const uint CurrentVersion = 1;
}

public sealed class EngineReady
{
    public bool Started;
    public string Error = "";
}

/// <summary>Plugin → host: active section/clip planes. Each plane is xyzd (normal.xyz, distance), up to 8.</summary>
public sealed class SectionPlanesUpdate
{
    public float[] Planes = Array.Empty<float>();
}

public sealed class TextureSlotDto
{
    public string File = "";
    /// <summary>Row-major 3x3 UV transform (engine expects GLM mat3; transmitted as 9 floats m00..m22 row-major).</summary>
    public float[] Transformation = { 1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f }; // identity by default
    public bool IsInverted;
    public float Brightness = 1f;
    public int Source;      // SlotSource
    public int WcsType;      // WcsType

    public bool IsDefined => !string.IsNullOrEmpty(File);

    public static TextureSlotDto Identity(string file) => new() { File = file ?? "", Brightness = 1f };
}

public sealed class MaterialDto
{
    public int Id;
    public string Name = "";
    public bool DoubleSided;
    public float Opacity = 1f;
    public float DiffuseR = 0.7f, DiffuseG = 0.7f, DiffuseB = 0.7f;
    public float ImageFade = 1f;
    public float EmissiveR, EmissiveG, EmissiveB;
    public float EmissiveStrength;
    public float TintR = 1f, TintG = 1f, TintB = 1f;
    public float Roughness = 0.5f;
    public float BumpAmount;
    public float Metallic;
    public float Specular = 0.04f;
    public float Refraction = 1.5f;
    public ulong MaterialType;   // MaterialType enum
    public int BumpMapKind;      // BumpMapKind enum
    public bool IsSolidGlass;
    // texture slots
    public TextureSlotDto DiffuseTexture = new();
    public TextureSlotDto BumpTexture = new();
    public TextureSlotDto RoughnessTexture = new();
    public TextureSlotDto MaskTexture = new();
}

public sealed class MaterialIndexGroupDto
{
    public uint StartIndex;
    public uint NumIndices;
    public uint FrontFaceMaterialId;
    public uint BackFaceMaterialId; // 0xFFFFFFFF = none
    public uint VisibleLodStart;
    public uint VisibleLodEnd;
}

public sealed class ModelDto
{
    public int Id;
    public ulong FamilyClass;
    public float[] Positions = Array.Empty<float>();    // xyz * n
    public float[] Normals = Array.Empty<float>();      // xyz * n
    public float[] Uvs = Array.Empty<float>();         // xy * n
    public float[] BacksideUvs = Array.Empty<float>(); // xy * n (or empty → reuse Uvs)
    public uint[] Indices = Array.Empty<uint>();
    public MaterialIndexGroupDto[] Groups = Array.Empty<MaterialIndexGroupDto>();
}

public sealed class InstanceDto
{
    public int ModelId;
    public int InstanceId;
    /// <summary>Engine-space 4x4 column-major (m[col][row]: col0..col2 rotation/scale, col3 translation).</summary>
    public float[] Matrix = new float[16];
}

public sealed class LightDto
{
    public int Id;
    public float X, Y, Z;
    public float R = 1f, G = 1f, B = 1f, A = 1f;
    public float Intensity;
    public int Distribution;   // LightDistribution: 1 spherical, 2 hemispherical, 3 spot
    public int Shape;          // LightShape: 1 point, 2 line, 3 rect, 4 circle
    public float BeamAngle;   // radians
    public float[] Rotation = new float[9]; // row-major 3x3
    public float ShapeParam1;
    public float ShapeParam2;
    public string IesPath = "";
}

public sealed class SunUpdate
{
    public int Mode;                 // 0 = place & time, 1 = explicit vector, -1 = undefined
    // place & time
    public double Latitude;
    public double Longitude;
    public double NorthAngle;        // radians
    public double TimeZoneHours;
    public int Day = 21, Month = 6, Year = 2026;
    public float Hour = 12f;
    // explicit
    public float SunX, SunY, SunZ;  // normalized engine-space direction towards the sun
}

public sealed class CameraUpdate
{
    public float PosX, PosY, PosZ;          // engine space
    public float QuatX, QuatY, QuatZ, QuatW; // engine-space orientation
    public float FovDegrees = 60f;
    public int FovType;                      // 0 horizontal, 1 vertical
    public uint ResX, ResY;                  // 0 = keep engine default
    public bool Ortho;
    public float OrthoLeft, OrthoRight, OrthoBottom, OrthoTop;
    public bool AutoExposure = true;
    public float Exposure = 0f;
    public bool TwoPointPerspective;
}

public sealed class ConsoleCommand
{
    public string Command = "";
    public bool Deferred = true;
}

public sealed class CaptureRequest
{
    public string FilePath = "";
    public int Width;   // 0 = window size
    public int Height;
    public bool Panorama;
}

public sealed class CaptureResult
{
    public bool Ok;
    public string FilePath = "";
    public string Error = "";
}

public sealed class MaterialUpdate
{
    public MaterialDto Material = new();
}

public sealed class LiveUpdateState
{
    public bool Paused;
}

public sealed class Status
{
    public string Text = "";
    public int Level; // 0 info, 1 warn, 2 error
}

public sealed class ViewActivation
{
    public string Name = "";
    public CameraUpdate Camera = new();
    public SunUpdate Sun = new();
}

public sealed class RendererClosed
{
    public string Reason = "";
}

public sealed class Shutdown { }
