using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Enscape.RendererApi;
using Enscape.RendererApi.DataModel;
using GlmSharp;
using PowerPotion.Protocol;

namespace PowerPotion.Host.Enscape;

/// <summary>
/// Bridges the installed Enscape native rendering engine (loaded through its shipped managed API
/// assembly, exactly like Chaos' own RendererHost does) into a WPF child window.
///
/// Contains no license, VR, account or telemetry code — by construction. See docs/ARCHITECTURE.md.
/// </summary>
public sealed class EngineBridge : IDisposable
{
    private readonly string _enscapeDir;        // .../RendererHost
    private readonly string _bin64Dir;          // .../RendererHost/Renderer/Bin64
    private readonly string _dataDir;           // writable engine data dir
    private readonly string _logDir;

    private Assembly? _apiAssembly;
    private GCHandle _preInitCommandsPin;
    private GCHandle[]? _preInitCommandPins;
    private IntPtr _instance;
    private EnscapeInstance? _managed;
    private IEnscapeApiScope? _scope;

    // native callbacks must stay alive
    private EnscapeAPI.EnscapeCallbackOnCloseExt? _onClose;
    private EnscapeAPI.EnscapeCallbackProcRendererErrorExt? _onError;
    private EnscapeAPI.EnscapeCallbackProc? _onWorldUpdated;
    private EnscapeAPI.EnscapeCallbackProc? _onCaptureFinished;
    // NOTE: the callbacks struct must NOT be a typed field here — a value-type field from
    // Enscape.RendererApi would force that assembly to load while this class is laid out,
    // i.e. before the assembly resolver is installed. Keep it boxed instead.
    private object? _callbacksBoxed;
    private EnscapeNativeWindowApi.EnscapeEmbeddedWindowCallback_EnscapeInitialized? _onInitialized;
    private EnscapeNativeWindowApi.EnscapeEmbeddedWindowCallback_FocusChange? _onFocus;
    private EnscapeNativeWindowApi.EnscapeEmbeddedWindowCallback_MinimizeChange? _onMinimize;

    private TaskCompletionSource<EnscapeResult>? _initialized;
    private readonly List<Action> _bufferCleanups = new();
    private readonly HashSet<int> _modelIds = new();
    private readonly HashSet<int> _instanceIds = new(); // (modelId << 20) ^ instanceId is too fragile; track separately below
    private readonly HashSet<(int, int)> _instances = new();
    private readonly HashSet<int> _lightIds = new();
    private readonly HashSet<int> _materialIds = new();
    private int _nextModelId = 1;
    private int _nextInstanceId = 1;

    public IntPtr NativeWindowHandle { get; private set; }
    public bool IsRunning => _instance != IntPtr.Zero && (_managed?.IsRunning ?? false);
    public string EngineVersion { get; private set; } = "";
    public event Action<string>? Log;
    public event Action? ClosedFromEngine;
    public event Action<bool>? CaptureFinished;

    public EngineBridge(string enscapeDir, string dataDir)
    {
        _enscapeDir = enscapeDir;
        _bin64Dir = Path.Combine(enscapeDir, "Renderer", "Bin64");
        _logDir = Path.Combine(dataDir, "Logs");

        // The engine expects its per-user system data (SystemData\Textures — loading overlay,
        // lens flare, HUD icons — plus shader/pipeline caches) under BaseDirectoryWriteable.
        // The shipped host uses %APPDATA%\Enscape, where a licensed Enscape installation has
        // already materialized it. Reuse it when present; otherwise fall back to our own dir
        // (the engine will populate what it can).
        var appdataEnscape = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Enscape");
        _dataDir = Directory.Exists(Path.Combine(appdataEnscape, "Data"))
            ? appdataEnscape
            : dataDir;

        Directory.CreateDirectory(_dataDir);
        Directory.CreateDirectory(_logDir);
    }

    private void LogLine(string msg) => Log?.Invoke(msg);

    /// <summary>
    /// Empirical engine calibration factor. The engine stores pushed geometry 1:1 (verified via
    /// GetEntityGroupBoundingBox) but its RENDER pipeline displays pushed geometry at 1/11.25 of
    /// the pushed values, while the SetCamera position renders 1:1 (all measured precisely on
    /// identity-instanced quads across camera heights and floor sizes). Frustum culling, however,
    /// runs in RAW pushed space against the RAW camera: geometry whose raw (unscaled) bounding
    /// box lies above the raw camera or far outside its raw frustum is culled even when its
    /// rendered position is inside the view.
    ///
    /// Consequences (all encoded in this bridge):
    ///  • model vertices and instance translations are multiplied by this factor on push
    ///    (protocol carries true meters → renders at true meters relative to the camera);
    ///  • camera positions are pushed raw (meters);
    ///  • the camera's raw Y should stay above the raw Y extent of nearby geometry so the raw
    ///    culling frustum (which opens downward from the camera) contains it.
    /// </summary>
    public const float GeometryScale = 11.25f;

    // ------------------------------------------------------------------
    // Assembly + native loading
    // ------------------------------------------------------------------

    /// <summary>Must run before any Enscape.RendererApi type is touched (call at process start).</summary>
    public static void InstallAssemblyResolver(string enscapeDir)
    {
        AssemblyLoadContext.Default.Resolving += (ctx, name) =>
        {
            var candidate = Path.Combine(enscapeDir, name.Name + ".dll");
            return File.Exists(candidate) && (name.Name == "Enscape.RendererApi" || name.Name == "GlmSharpStd")
                ? ctx.LoadFromAssemblyPath(candidate)
                : null;
        };
    }

    private void EnsureApiLoaded()
    {
        if (_apiAssembly != null) return;
        var apiPath = Path.Combine(_enscapeDir, "Enscape.RendererApi.dll");
        _apiAssembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(apiPath);

        // Native DllImport resolution: "Enscape.Renderer" / "Enscape.WindowApi" live in Bin64.
        NativeLibrary.SetDllImportResolver(_apiAssembly, (name, assembly, searchPath) =>
        {
            var candidate = Path.Combine(_bin64Dir, name + ".dll");
            return File.Exists(candidate) ? NativeLibrary.Load(candidate) : IntPtr.Zero;
        });

        // Pre-load the engine by absolute path so its sibling DLLs (sfml-audio, OpenAL, Noesis,
        // shaderc_shared, ...) resolve via the altered search path.
        NativeLibrary.Load(Path.Combine(_bin64Dir, "Enscape.Renderer.dll"));

        // Read engine version (no instance needed).
        try
        {
            var sb = new System.Text.StringBuilder(128);
            EnscapeNativeApi.Enscape_GetVersion(sb, 128);
            EngineVersion = sb.ToString().TrimEnd('\0');
        }
        catch { EngineVersion = "unknown"; }
        LogLine($"Engine API loaded from '{_enscapeDir}' (renderer {EngineVersion}).");
    }

    // ------------------------------------------------------------------
    // Embedded renderer lifecycle
    // ------------------------------------------------------------------

    /// <summary>
    /// Starts the engine rendering into a child of <paramref name="parentHwnd"/>.
    /// MUST be called from the windowing/UI thread (Chaos' own host guards this with Dispatcher.VerifyAccess).
    /// </summary>
    public EnscapeResult StartRendererNative(IntPtr parentHwnd, int x, int y, int width, int height)
    {
        EnsureApiLoaded();

        EnscapeNativeApi.Enscape_InitializeLogging(Path.Combine(_logDir, "engine.log"));

        var preInit = new List<string>
        {
            "g_displayLoadingScreen 0",
            "g_locale en",
            "sp_viewSync 1",
        };

        _initialized = new TaskCompletionSource<EnscapeResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        var init = new EnscapeNativeWindowApi.EnscapeEmbeddedWindowInitParams_Native
        {
            IsStandalone = false,
            EnableRpc = true,
            EnableVideoPathEditor = false,
            HostApplication = HostApplication.SDK, // geometry is pushed in meters by PowerPotion
                                                   // (HostApplication.REVIT makes the engine apply
                                                   // a Revit-feet unit conversion to pushed geometry)
            // The engine resolves its config files (systemPre.cfg etc.) and SystemData relative to
            // WorkingDirectory — that is the "Renderer" subdirectory of the install (verified against
            // the decompiled config loader in Enscape.WindowAPI.dll and a live init).
            WorkingDirectory = WorkDirOverride ?? Path.Combine(_enscapeDir, "Renderer"),
            BaseDirectoryWriteable = WritableOverride ?? _dataDir,
        };

        // PreInitCommands is a wchar_t** on the native side: an array of `count` pointers, each to a
        // NUL-terminated UTF-16 string (verified against the decompiled parser in Enscape.WindowAPI.dll).
        if (!NoPreInit && preInit.Count > 0)
        {
            _preInitCommandPins = new GCHandle[preInit.Count];
            var pointers = new IntPtr[preInit.Count];
            for (int i = 0; i < preInit.Count; i++)
            {
                var buffer = System.Text.Encoding.Unicode.GetBytes(preInit[i] + "\0");
                _preInitCommandPins[i] = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                pointers[i] = _preInitCommandPins[i].AddrOfPinnedObject();
            }
            _preInitCommandsPin = GCHandle.Alloc(pointers, GCHandleType.Pinned);
            init.PreInitCommands = _preInitCommandsPin.AddrOfPinnedObject();
            init.PreInitCommandsCount = preInit.Count;
        }
        else
        {
            init.PreInitCommands = IntPtr.Zero;
            init.PreInitCommandsCount = 0;
        }

        _onInitialized = (userInfo, result) =>
        {
            LogLine($"Engine initialized: {result}");
            if (EnscapeAPI.IsError(result) && _instance != IntPtr.Zero)
            {
                try
                {
                    var sb = new System.Text.StringBuilder(4096);
                    EnscapeNativeApi.Enscape_GetLastError(_instance, sb, 4096);
                    var lastError = sb.ToString().TrimEnd('\0');
                    if (lastError.Length > 0)
                        LogLine($"Engine last error: {lastError}");
                }
                catch { }
            }
            _initialized?.TrySetResult(result);
        };
        _onFocus = (userInfo, focus) => { };
        _onMinimize = (userInfo, minimize) => { };
        var callbacks = new EnscapeNativeWindowApi.EnscapeEmbeddedWindowCallbacks
        {
            OnInitialized = _onInitialized,
            OnFocusChange = _onFocus,
            OnMinimizeChange = _onMinimize,
        };
        _onClose = (instance, code) =>
        {
            LogLine($"Engine closed (code {code}).");
            _instance = IntPtr.Zero;
            ClosedFromEngine?.Invoke();
        };
        _onError = (instance, errorState) =>
        {
            LogLine($"Renderer error: {errorState}");
            return true; // handled — don't let the engine show its own crash UI
        };

        LogLine("Starting embedded renderer…");
        // Mirror the shipped host: its exe runs with CWD = RendererHost dir; the engine's early
        // file probing may rely on that.
        var previousCwd = Environment.CurrentDirectory;
        Environment.CurrentDirectory = _enscapeDir;
        try
        {
            var result = UseLegacyStart
                ? EnscapeNativeWindowApi.EnscapeEmbeddedWindow_StartRenderer(
                    ref init, out _instance, _onClose, _onError,
                    parentHwnd, IntPtr.Zero, x, y, width, height, ref callbacks)
                : EnscapeNativeWindowApi.EnscapeEmbeddedWindow_StartRenderer2(
                    ref init, out _instance, _onClose, _onError,
                    parentHwnd, IntPtr.Zero, x, y, width, height, ref callbacks);
            _callbacksBoxed = callbacks; // keep the callback delegates alive

            if (EnscapeAPI.IsError(result))
            {
                LogLine($"StartRenderer failed: {result}");
                FreePreInitPins();
                return result;
            }
            return EnscapeResult.OK;
        }
        finally
        {
            Environment.CurrentDirectory = previousCwd;
        }
    }

    private void FreePreInitPins()
    {
        if (_preInitCommandsPin.IsAllocated) _preInitCommandsPin.Free();
        if (_preInitCommandPins != null)
        {
            foreach (var p in _preInitCommandPins)
                if (p.IsAllocated) p.Free();
            _preInitCommandPins = null;
        }
    }

    /// <summary>Selects the original StartRenderer path instead of StartRenderer2 (feature-flag dependent in Chaos' host).</summary>
    public static bool UseLegacyStart { get; set; } = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("POWERPOTION_START1"));

    /// <summary>Diagnostics: skip pre-init commands entirely (pass null).</summary>
    public static bool NoPreInit { get; set; } = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("POWERPOTION_NOPREINIT"));

    /// <summary>Diagnostics: override WorkingDirectory passed to the engine.</summary>
    public static string? WorkDirOverride { get; set; } = Environment.GetEnvironmentVariable("POWERPOTION_WORKDIR");

    /// <summary>Diagnostics: override BaseDirectoryWriteable passed to the engine.</summary>
    public static string? WritableOverride { get; set; } = Environment.GetEnvironmentVariable("POWERPOTION_WRITABLE");

    /// <summary>Waits for engine initialization and attaches the managed API facade.</summary>
    public async Task<EnscapeResult> AttachWhenReadyAsync(CancellationToken ct)
    {
        if (_initialized == null)
            return EnscapeResult.INVALID_STATE;

        var completed = await Task.WhenAny(_initialized.Task, Task.Delay(TimeSpan.FromSeconds(120), ct));
        if (completed != _initialized.Task)
            return EnscapeResult.INTERNAL_TIMEOUT;
        var initResult = _initialized.Task.Result;
        if (EnscapeAPI.IsError(initResult))
            return initResult;

        // Attach the managed facade to the native instance.
        var startupParams = new StartupParameters
        {
            EnableRpc = true,
            Standalone = false,
        };
        _managed = CreateManagedInstance();
        _managed.AttachToExistingInstance(_instance, startupParams, out _, out _);
        _scope = _managed.BeginApiScope();

        // Availability diagnostics
        try
        {
            var s = _scope;
            LogLine($"API scopes: world={s.TryGetWorldApi(out _)} camera={s.TryGetCameraApi(out _)} " +
                    $"settings={s.TryGetSettingsApi(out _)} sun={s.TryGetSunAndLocationApi(out _)} " +
                    $"capture={s.TryGetCaptureApi(out _)} window={s.TryGetWindowApi(out _)} " +
                    $"scene={s.TryGetSceneApi(out _)} section={s.TryGetSectionPlanesApi(out _)}");
        }
        catch (Exception ex) { LogLine("Scope probe failed: " + ex.Message); }

        var hwnd = IntPtr.Zero;
        EnscapeNativeWindowApi.EnscapeEmbeddedWindow_GetNativeWindowHandle(_instance, out hwnd);
        NativeWindowHandle = hwnd;
        LogLine($"Renderer window handle: 0x{hwnd:X}");

        LogLine("Engine ready.");
        return EnscapeResult.OK;
    }

    public void Resize(int x, int y, int width, int height)
    {
        if (_instance == IntPtr.Zero) return;
        EnscapeNativeWindowApi.EnscapeEmbeddedWindow_SetWindowRect(_instance, x, y, (uint)width, (uint)height);
    }

    public void NotifyFocus(bool focused) =>
        EnscapeNativeWindowApi.EnscapeEmbeddedWindow_TriggerFocusChanged(_instance, focused);

    public void NotifyMinimize(bool minimized) =>
        EnscapeNativeWindowApi.EnscapeEmbeddedWindow_TriggerMinimizeChanged(_instance, minimized);

    public async Task StopAsync()
    {
        if (_instance == IntPtr.Zero) return;
        try
        {
            _scope?.Dispose();
            _scope = null;
            _managed?.Stop();
        }
        catch (Exception ex) { LogLine("Stop managed: " + ex.Message); }

        var inst = _instance;
        var result = EnscapeNativeWindowApi.EnscapeEmbeddedWindow_Uninitialize(ref inst);
        _instance = IntPtr.Zero;
        NativeWindowHandle = IntPtr.Zero;
        LogLine($"Renderer uninitialized: {result}");

        FreePreInitPins();
        CleanupBuffers();
    }

    public void Dispose()
    {
        try { StopAsync().GetAwaiter().GetResult(); } catch { }
    }

    private static EnscapeInstance CreateManagedInstance()
    {
        // EnscapeInstance requires the internal native-API wrapper (private nested NativeApiInterface,
        // public ctor) and an IRendererLogger. Mirror of what Chaos' RendererHost does.
        var nativeApiType = typeof(EnscapeNativeApi).GetNestedType("NativeApiInterface",
            BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException("NativeApiInterface not found in Enscape.RendererApi.");
        var nativeApi = Activator.CreateInstance(nativeApiType, nonPublic: true)
            ?? throw new InvalidOperationException("Could not create NativeApiInterface.");
        return new EnscapeInstance((IEnscapeApiNative)nativeApi, new BridgeLogger());
    }

    private sealed class BridgeLogger : IRendererLogger
    {
        public void Log(RendererLogLevel level, string message) =>
            Console.WriteLine($"[renderer:{level}] {message}");

        public IDisposable BeginScope(string scope, string value) => new NoopScope();

        private sealed class NoopScope : IDisposable
        {
            public void Dispose() { }
        }
    }

    // ------------------------------------------------------------------
    // World push
    // ------------------------------------------------------------------

    private IEnscapeWorldApi World()
    {
        if (_scope?.TryGetWorldApi(out var api) == true) return api;
        throw new InvalidOperationException("Renderer engine is not running.");
    }

    private void CleanupBuffers()
    {
        foreach (var cleanup in _bufferCleanups) { try { cleanup(); } catch { } }
        _bufferCleanups.Clear();
    }

    /// <summary>Replaces the whole world with the given snapshot.</summary>
    public void ApplyWorld(WorldSnapshot world)
    {
        var worldApi = World();
        worldApi.ClearWorld();
        _modelIds.Clear();
        _instances.Clear();
        _lightIds.Clear();
        _materialIds.Clear();
        CleanupBuffers();

        worldApi.BeginUpdateWorld();
        try
        {
            foreach (var mat in world.Materials)
                AddMaterial(worldApi, mat);

            foreach (var model in world.Models)
                AddModel(worldApi, model);

            foreach (var inst in world.Instances)
                AddInstance(worldApi, inst);

            foreach (var light in world.Lights)
                AddLight(worldApi, light);
        }
        finally
        {
            worldApi.EndUpdateWorld();
        }

        // UpdateWorld commits asynchronously; the world becomes visible when the callback fires.
        var worldUpdated = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _onWorldUpdated = code =>
        {
            LogLine($"World update finished: {code}");
            worldUpdated.TrySetResult(code == 0);
        };
        worldApi.UpdateWorld(_onWorldUpdated);
        if (!worldUpdated.Task.Wait(TimeSpan.FromSeconds(30)))
            LogLine("World update timed out — the scene may appear incomplete.");

        ApplySun(world.Sun);
        ApplyCamera(world.Camera);
    }

    private void AddMaterial(IEnscapeWorldApi worldApi, MaterialDto dto)
    {
        var p = new MaterialParameter
        {
            doubleSided = dto.DoubleSided,
            opacity = dto.Opacity,
            diffuseColorR = dto.DiffuseR,
            diffuseColorG = dto.DiffuseG,
            diffuseColorB = dto.DiffuseB,
            imageFade = dto.ImageFade,
            emissiveColorR = dto.EmissiveR,
            emissiveColorG = dto.EmissiveG,
            emissiveColorB = dto.EmissiveB,
            emissiveStrength = dto.EmissiveStrength,
            tintColorR = dto.TintR,
            tintColorG = dto.TintG,
            tintColorB = dto.TintB,
            roughness = dto.Roughness,
            bumpAmount = dto.BumpAmount,
            metallic = dto.Metallic,
            specular = dto.Specular,
            refraction = dto.Refraction,
            materialType = dto.MaterialType,
            bumpMapKind = (BumpMapKind)dto.BumpMapKind,
            isSolidGlass = dto.IsSolidGlass,
        };

        var tex = new TextureParams
        {
            DiffuseTexture = ToSlot(dto.DiffuseTexture),
            BumpTexture = ToSlot(dto.BumpTexture),
            RoughnessTexture = ToSlot(dto.RoughnessTexture),
            MaskTexture = ToSlot(dto.MaskTexture),
        };

        var result = worldApi.AddMaterial(dto.Id, ref p, ref tex);
        if (EnscapeAPI.IsError(result)) LogLine($"AddMaterial '{dto.Name}' ({dto.Id}): {result}");
        else _materialIds.Add(dto.Id);
    }

    private static TextureSlotParams ToSlot(TextureSlotDto dto)
    {
        var m = new mat3(
            dto.Transformation[0], dto.Transformation[1], dto.Transformation[2],
            dto.Transformation[3], dto.Transformation[4], dto.Transformation[5],
            dto.Transformation[6], dto.Transformation[7], dto.Transformation[8]);
        return new TextureSlotParams
        {
            File = dto.File,
            Transformation = m,
            IsInverted = dto.IsInverted,
            Brightness = dto.Brightness,
            Source = (TextureSlotParams.SlotSource)dto.Source,
            wcsType = (TextureSlotParams.WcsType)dto.WcsType,
        };
    }

    private void AddModel(IEnscapeWorldApi worldApi, ModelDto dto)
    {
        var modelData = new ModelData
        {
            FamilyClass = (FamilyClass)dto.FamilyClass,
            Uvs = new List<vec2>(),
            BacksideUvs = new List<vec2>(),
        };
        int vertexCount = dto.Positions.Length / 3;
        var s = GeometryScale;
        for (int i = 0; i < vertexCount; i++)
        {
            modelData.Vertices.Add(new vec3(dto.Positions[3 * i] * s, dto.Positions[3 * i + 1] * s, dto.Positions[3 * i + 2] * s));
            modelData.Normals.Add(new vec3(dto.Normals[3 * i], dto.Normals[3 * i + 1], dto.Normals[3 * i + 2]));
            modelData.Uvs.Add(new vec2(dto.Uvs[2 * i], dto.Uvs[2 * i + 1]));
            if (dto.BacksideUvs.Length == dto.Uvs.Length)
                modelData.BacksideUvs.Add(new vec2(dto.BacksideUvs[2 * i], dto.BacksideUvs[2 * i + 1]));
            else
                modelData.BacksideUvs.Add(new vec2(dto.Uvs[2 * i], dto.Uvs[2 * i + 1]));
        }
        foreach (var g in dto.Groups)
        {
            var front = unchecked((int)g.FrontFaceMaterialId);
            var back = g.BackFaceMaterialId == 0xFFFFFFFF ? front : unchecked((int)g.BackFaceMaterialId);
            var collector = new IndexCollector(front, back)
            {
                LodStart = 0,
                LodEnd = 1000, // wide LOD visibility range (null → 0..0 = never visible)
            };
            for (uint i = 0; i < g.NumIndices; i++)
                collector.Indices.Add(unchecked((int)dto.Indices[g.StartIndex + i]));
            modelData.IndexCollectors.Add(collector);
        }
        modelData.NumIndices = dto.Indices.Length;

        var result = worldApi.CreateModelFromGeometry(dto.Id, modelData);
        if (EnscapeAPI.IsError(result)) LogLine($"CreateModelFromGeometry {dto.Id}: {result}");
        else _modelIds.Add(dto.Id);
    }

    /// <summary>Asks the engine for the world-space bounding box of an instance (ground-truth diagnostics).</summary>
    public async Task QueryInstanceBoundsAsync(int groupId, uint modelId, uint instanceId)
    {
        if (_scope?.TryGetEntityGroupApi(out var api) != true) { LogLine("QueryInstanceBounds: no entity group api"); return; }
        var r = api.CreateEntityGroup(groupId);
        if (EnscapeAPI.IsError(r)) { LogLine($"CreateEntityGroup: {r}"); return; }
        r = api.AddModelInstanceToEntityGroup(groupId, modelId, instanceId);
        if (EnscapeAPI.IsError(r)) { LogLine($"AddModelInstanceToEntityGroup: {r}"); return; }
        try
        {
            var result = await api.GetEntityGroupBoundingBox(groupId);
            if (result.Result == EnscapeResult.OK)
                LogLine($"ENGINE-REPORTED instance bounds: Min={result.BoundingBox.Min} Max={result.BoundingBox.Max}");
            else
                LogLine($"GetEntityGroupBoundingBox: {result.Result}");
        }
        catch (Exception ex) { LogLine("GetEntityGroupBoundingBox failed: " + ex.Message); }
        finally
        {
            api.RemoveEntityGroup(groupId);
        }
    }

    private void AddInstance(IEnscapeWorldApi worldApi, InstanceDto dto)
    {
        // Scale instance translations by the calibration factor (protocol is true meters).
        // Column-major protocol matrix: translation = elements [12],[13],[14].
        var scaled = (float[])dto.Matrix.Clone();
        scaled[12] *= GeometryScale;
        scaled[13] *= GeometryScale;
        scaled[14] *= GeometryScale;
        var m = ToMat4(scaled);
        var p = ModelInstanceParams.FromMatrix(m);
        var result = worldApi.AddModelInstance(dto.ModelId, dto.InstanceId, ref p);
        if (EnscapeAPI.IsError(result)) LogLine($"AddModelInstance {dto.ModelId}/{dto.InstanceId}: {result}");
        else _instances.Add((dto.ModelId, dto.InstanceId));
    }

    private static string FmtRow(vec4 r) => $"{r.x:0.##},{r.y:0.##},{r.z:0.##},{r.w:0.##}";

    private static mat4 ToMat4(float[] colMajor)
    {
        // Protocol matrices are column-major (m[col][row]) — identical to GlmSharp's 16-float
        // mat4 constructor, which takes values column by column (verified empirically:
        // ModelInstanceParams.FromMatrix reads translation from column 3 = m03/m13/m23).
        return new mat4(
            colMajor[0], colMajor[1], colMajor[2], colMajor[3],
            colMajor[4], colMajor[5], colMajor[6], colMajor[7],
            colMajor[8], colMajor[9], colMajor[10], colMajor[11],
            colMajor[12], colMajor[13], colMajor[14], colMajor[15]);
    }

    private void AddLight(IEnscapeWorldApi worldApi, LightDto dto)
    {
        var s = GeometryScale;
        var p = new EnscapeAPI.LightParams
        {
            x = dto.X * s, y = dto.Y * s, z = dto.Z * s,
            red = dto.R, green = dto.G, blue = dto.B, alpha = dto.A,
            lightIntensity = dto.Intensity,
            lightDistribution = (EnscapeAPI.LightDistribution)dto.Distribution,
            lightShape = (EnscapeAPI.LightShape)dto.Shape,
            beamAngle = dto.BeamAngle,
            rotation = dto.Rotation,
            shapeParam1 = dto.ShapeParam1 * s,
            shapeParam2 = dto.ShapeParam2 * s,
            lightProfilePath = dto.IesPath,
        };
        var result = worldApi.AddLight(dto.Id, ref p);
        if (EnscapeAPI.IsError(result)) LogLine($"AddLight {dto.Id}: {result}");
        else _lightIds.Add(dto.Id);
    }

    /// <summary>Live-updates one material (remove + re-add with same id, mirroring Enscape's OverwriteMaterial semantics).</summary>
    public void OverwriteMaterial(MaterialDto dto)
    {
        var worldApi = World();
        if (_materialIds.Contains(dto.Id))
            worldApi.RemoveMaterial(dto.Id);
        // wrap in a world update batch so the swap is atomic
        worldApi.BeginUpdateWorld();
        try { AddMaterial(worldApi, dto); }
        finally { worldApi.EndUpdateWorld(); }
        worldApi.UpdateWorld(null);
    }

    // ------------------------------------------------------------------
    // Sun / camera / section planes
    // ------------------------------------------------------------------

    public void ApplySun(SunUpdate sun)
    {
        if (_scope?.TryGetSunAndLocationApi(out var api) != true) return;
        switch (sun.Mode)
        {
            case 0: // place & time
                var location = new ProjectLocation
                {
                    Latitude = sun.Latitude,
                    Longitude = sun.Longitude,
                    NorthAngle = sun.NorthAngle,
                    TimeZone = sun.TimeZoneHours,
                };
                api.SetProjectLocation(location);
                api.SetDateAndTime(new DateTime(sun.Year, sun.Month, sun.Day)
                    .AddHours(sun.Hour));
                break;
            case 1: // explicit sun vector
                api.SetExplicitSun(new ProjectLocation
                {
                    Latitude = sun.Latitude,
                    Longitude = sun.Longitude,
                    NorthAngle = sun.NorthAngle,
                    TimeZone = sun.TimeZoneHours,
                }, new ExplicitSunPosition { Altitude = 0, Azimuth = 0 });
                // explicit vector path uses the sun CVar directly
                ExecuteConsoleCommand($"g_sunVec {Fmt(sun.SunX)},{Fmt(sun.SunY)},{Fmt(sun.SunZ)}", true);
                break;
        }
    }

    // CameraParams is a shipped struct whose FOV/flags members are only fully settable through
    // its fields (Chaos' own host fills it via NRBF deserialization). Public property setters
    // cover position/orientation/etc.; fov, fovType and the ActiveFields backing field use reflection.
    // Nested class = lazy initialization: must not run before InstallAssemblyResolver.
    private static class CamFields
    {
        public static readonly System.Reflection.FieldInfo? Fov =
            typeof(EnscapeAPI.CameraParams).GetField("fov", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
        public static readonly System.Reflection.FieldInfo? FovType =
            typeof(EnscapeAPI.CameraParams).GetField("fovType", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
        public static readonly System.Reflection.FieldInfo? ActiveFields =
            typeof(EnscapeAPI.CameraParams).GetField("<ActiveFields>k__BackingField", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
    }

    public void ApplyCamera(CameraUpdate cam)
    {
        if (_scope?.TryGetCameraApi(out var api) != true) return;
        if (cam.PosX == 0 && cam.PosY == 0 && cam.PosZ == 0 && cam.QuatW == 0)
            return; // empty camera update → keep engine default
        var flags = EnscapeAPI.CameraFlags.POSITION | EnscapeAPI.CameraFlags.ORIENTATION;

        // NOTE: camera positions are pushed RAW (meters) — the engine's render camera is NOT
        // subject to GeometryScale (verified empirically; see the GeometryScale remark).
        var p = new EnscapeAPI.CameraParams
        {
            Position = new vec3(cam.PosX, cam.PosY, cam.PosZ),
            Orientation = new quat(cam.QuatX, cam.QuatY, cam.QuatZ, cam.QuatW),
        };

        if (cam.FovDegrees > 0 && CamFields.Fov != null && CamFields.FovType != null)
        {
            flags |= EnscapeAPI.CameraFlags.FOV;
            p = WithBoxed(p, boxed =>
            {
                CamFields.Fov.SetValue(boxed, cam.FovDegrees);
                CamFields.FovType.SetValue(boxed, (EnscapeAPI.FovType)cam.FovType);
            });
        }
        if (cam.ResX > 0 && cam.ResY > 0)
        {
            flags |= EnscapeAPI.CameraFlags.RESOLUTION;
            p.Resolution = new uvec2(cam.ResX, cam.ResY);
        }

        flags |= EnscapeAPI.CameraFlags.PROJECTION_TYPE;
        if (cam.Ortho)
        {
            flags |= EnscapeAPI.CameraFlags.ORTHO_WINDOW;
            p.ProjectionType = EnscapeAPI.ProjectionType.ORTHO;
            // Ortho window is a camera-space extent → raw meters, like the camera position.
            p.OrthoWindow = new vec4(cam.OrthoLeft, cam.OrthoRight, cam.OrthoBottom, cam.OrthoTop);
        }
        else
        {
            p.ProjectionType = EnscapeAPI.ProjectionType.PERSPECTIVE;
        }

        flags |= EnscapeAPI.CameraFlags.AUTOEXPOSURE;
        p.AutoExposure = cam.AutoExposure;
        if (!cam.AutoExposure)
        {
            flags |= EnscapeAPI.CameraFlags.EXPOSURE;
            p.Exposure = cam.Exposure;
        }
        flags |= EnscapeAPI.CameraFlags.TWO_POINT;
        p.TwoPoint = cam.TwoPointPerspective;

        if (CamFields.ActiveFields != null)
            p = WithBoxed(p, boxed => CamFields.ActiveFields.SetValue(boxed, flags));

        // Read-back diagnostics: confirm the reflection writes actually landed in the struct.
        try
        {
            var f = (EnscapeAPI.CameraFlags?)(CamFields.ActiveFields?.GetValue(BoxOf(p)));
            var fv = (float?)(CamFields.Fov?.GetValue(BoxOf(p)));
            var ft = (EnscapeAPI.FovType?)(CamFields.FovType?.GetValue(BoxOf(p)));
            LogLine($"SetCamera fields: flags={f} fov={fv} fovType={ft} pos={p.Position} quat={p.Orientation}");
        }
        catch { }

        var result = api.SetCamera(ref p, CameraTransitionFlags.NONE);
        LogLine($"SetCamera result: {result}");
        if (EnscapeAPI.IsError(result)) LogLine($"SetCamera: {result}");

        try
        {
            var readBack = new EnscapeAPI.CameraParams();
            var gr = api.GetCamera(out readBack);
            LogLine($"GetCamera after set: {gr} pos={readBack.Position} quat={readBack.Orientation} fov={readBack.Fov}");
            // Ask the engine to verbosely report its own camera state (diagnostics).
            if (_scope?.TryGetSettingsApi(out var settings) == true)
                settings.ExecuteConsoleCommand("api_cameraHeavyReportInterval 1", false);
        }
        catch (Exception ex) { LogLine("GetCamera failed: " + ex.Message); }
    }

    private static EnscapeAPI.CameraParams WithBoxed(EnscapeAPI.CameraParams p, Action<object> mutate)
    {
        object boxed = p;
        mutate(boxed);
        return (EnscapeAPI.CameraParams)boxed;
    }

    private static object BoxOf(EnscapeAPI.CameraParams p) => p;

    public void SetSectionPlanes(float[] planesXyzd)
    {
        if (_scope?.TryGetSectionPlanesApi(out var api) != true) return;
        if (planesXyzd.Length == 0) { api.RemoveAllSectionPlanes(); return; }
        var planes = new vec4[planesXyzd.Length / 4];
        for (int i = 0; i < planes.Length; i++)
            planes[i] = new vec4(planesXyzd[4 * i], planesXyzd[4 * i + 1], planesXyzd[4 * i + 2], planesXyzd[4 * i + 3]);
        api.SetSectionPlanes(planes);
    }

    // ------------------------------------------------------------------
    // Settings / console commands
    // ------------------------------------------------------------------

    public void ExecuteConsoleCommand(string command, bool deferred)
    {
        if (_scope?.TryGetSettingsApi(out var api) != true) return;
        api.ExecuteConsoleCommand(command, deferred);
    }

    public void WaitForIdle()
    {
        if (_scope?.TryGetWindowApi(out var api) != true) return;
        api.WaitForLoopModeIdle();
    }

    public void WaitForFrames(ulong frames)
    {
        try { World().WaitForNumFramesRendered(frames); } catch { }
    }

    // ------------------------------------------------------------------
    // Capture
    // ------------------------------------------------------------------

    private TaskCompletionSource<bool>? _captureTcs;

    /// <summary>Completes when the current capture finishes (true = success).</summary>
    public Task<bool> CaptureDone => (_captureTcs ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

    public void CaptureScreenshot(string filePath, bool panorama)
    {
        if (_scope?.TryGetCaptureApi(out var api) != true) return;
        _captureTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _onCaptureFinished = code =>
        {
            LogLine($"Capture finished: {code}");
            _captureTcs?.TrySetResult(code == 0);
            CaptureFinished?.Invoke(code == 0);
        };
        var result = panorama
            ? api.StartPanoramaCapturing(filePath, _onCaptureFinished)
            : api.StartScreenshotCapturing(filePath, _onCaptureFinished);
        if (EnscapeAPI.IsError(result))
        {
            LogLine($"Capture start failed: {result}");
            _captureTcs.TrySetResult(false);
        }
    }

    private static string Fmt(float v) => v.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
}
