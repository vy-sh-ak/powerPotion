# PowerPotion — Architecture & Reverse-Engineering Notes

PowerPotion is a clean-room, personalized re-implementation of the **Chaos Enscape 4.7.0.57 Revit plugin**,
built from static evidence gathered with the REA reverse-engineering toolkit on the installed product
(`C:\Program Files\Enscape`). It keeps the rendering functionality (real-time render, materials, lights,
sun/cloud/sky), removes VR and authentication/licensing, and adds its own installer/uninstaller.

## What was reverse engineered (evidence-backed)

| Area | Evidence |
|---|---|
| Revit plugin structure (`Enscape.Revit.Plugin.2026.dll`) | REA managed-members evidence `ev_5d78d938…` (463 types / 2102 methods / 587 strings) — see `rea-report-revit-plugin.md` |
| IPC layer (`Enscape.Cad.Remote.dll`) | REA evidence `ev_89e328cd…` — custom binary RPC over a localhost TCP socket, `--port` argument |
| Renderer host (`Enscape.RendererHost.dll`) | REA evidence `ev_d41427bf…` (614 types) — see `rea-report-rendererhost.md` |
| Native renderer API (`Enscape.RendererApi.dll`, P/Invoke over `Enscape.Renderer.dll`) | REA native-boundary evidence `ev_7464e9e6…` (186 imports) + full reflection surface dump (`rendererapi-surface*.txt`) |
| Native engine exports | 213 `Enscape_*` C exports in `Renderer\Bin64\Enscape.Renderer.dll` + 8 `EnscapeEmbeddedWindow_*` in `Enscape.WindowApi.dll` |

### Enscape's architecture (observed)

```
Revit (Revit.exe, .NET 8)
 ├─ Enscape.Revit.Plugin.2026.dll          managed add-in (ribbon UI, scene export, live sync)
 │   ├─ RenderingSync: CustomExporter/IExportContext → ExportedWorld → FlatWorld deltas
 │   ├─ Materials: 19 Revit appearance schemas parsed (generic_diffuse, …) + ExtensibleStorage overrides
 │   ├─ Lights: "Generic Photometric Light" asset (IES, temperature, intensity units)
 │   └─ Sun: SunAndShadowSettings → PlaceAndTime / Explicit sun vector
 ├─ Enscape.Cad.Remote.dll                 RemoteCadServer: TCP loopback listener, custom binary protocol
 └─ launches → RendererHost\Enscape.RendererHost.exe --port <n>  (Avalonia UI host)
      ├─ Enscape.License.* / Chaos account  ← license + auth gates (REMOVED in PowerPotion)
      ├─ en2vr / vr_* CVars, HmdType        ← VR support (REMOVED in PowerPotion)
      └─ Enscape.RendererApi.dll → Enscape.Renderer.dll (native Vulkan engine, DLSS/FSR)
```

Key facts that shaped PowerPotion's design:

1. **The renderer engine has no license API.** All licensing in the shipped product lives in the *managed*
   host (`RendererWindow.PerformLicenseCheckAsync`, `CommandState.VerifyLicense`,
   `ScreenshotExecutor.ThrowIfNotAvailable`, `PreInitCommandsCollector` pushing `g_trial`). The native
   `Enscape.Renderer.dll` exports 213 C functions, none license-related. A host that simply never calls the
   license layer gets full engine functionality. (The engine remains the user's licensed Enscape
   installation — PowerPotion does not patch or circumvent anything inside it.)
2. **The engine is embedded via `EnscapeEmbeddedWindow_StartRenderer2`** (from `Enscape.WindowApi.dll`),
   which takes a parent HWND + rect, an init-params struct (working dir, pre-init console commands), and
   callbacks; it creates the native render window as a child HWND and returns an instance handle used by
   every other `Enscape_*` API.
3. **Everything (geometry, materials, lights, sun, camera) is pushed through 24 API scopes** obtained via
   `EnscapeInstance.BeginApiScope()` (`IEnscapeApiScope.TryGetWorldApi/CaptureApi/SettingsApi/…`).
4. **Settings are console-command (CVar) strings** (`r_skyboxFile`, `r_cloudDensity`, `r_fog`,
   `g_SunPower`, `r_hwrt`, `sys_spec`, `r_dlss`, …). The full CVar list is in
   `rea-report-rendererhost.md` §2.
5. **Geometry format**: `ModelGeometryParams` = raw vertex/normal/UV/index buffers + `MaterialIndexGroup`
   ranges; helpers `ModelGeometryParams.Allocate(ModelData, out Action cleanup)` and
   `ModelInstanceParams.FromMatrix(mat4)` exist in the shipped managed wrapper and are used by PowerPotion.
6. **Enscape's coordinate convention** (derived from the plugin's `RevitGeometryParser.WorldTransformation`,
   camera math, and sun vector code): engine space is **meters, right-handed, Y-up**, Revit
   (X,Y,Z) ft → engine (X, Z, −Y) m. North = −Z engine. Camera forward = −Z (OpenGL style).

### PowerPotion's architecture (clean-room)

```
Revit 2026
 └─ PowerPotion.Revit.dll (this repo)      IExternalApplication, ribbon "PowerPotion" tab
      ├─ Export: SceneExportContext (IExportContext) — geometry, materials, lights, sun, views
      ├─ Sync: DocumentObserver (DocumentChanged → debounced re-export),
      │        CameraSynchronizer (Idling → engine-space camera), HostLauncher
      └─ PowerPotion.Protocol ←→ TCP loopback ←→ PowerPotion.Host.exe --port <n>  (WPF)
           ├─ Enscape/EngineBridge — loads installed Enscape.RendererApi.dll + native engine
           │   (no license calls, no VR, no accounts, no telemetry)
           ├─ MainWindow: HwndHost embedding + WPF toolbar (visual settings, sky/cloud/sun, quality, capture)
           └─ Panels: MaterialEditor, VisualSettings, SkySettings, SunSettings, CaptureSettings
```

Both processes exchange `PowerPotion.Protocol` messages (length-prefixed little-endian binary frames).

## Empirical engine calibration (validated against a live engine)

Facts established by rendering probe scenes and interrogating the running engine
(`GetCamera`, `GetEntityGroupBoundingBox`, console CVars, pixel measurements):

1. **World storage**: pushed geometry/instance transforms are stored 1:1
   (`GetEntityGroupBoundingBox` returns exactly the pushed raw values).
2. **Render scale**: the render pipeline displays pushed geometry at **1/11.25** of the
   pushed values while the `SetCamera` position renders 1:1. The bridge therefore pushes
   all world-space quantities × `GeometryScale = 11.25` (`EngineBridge.GeometryScale`)
   and the camera raw, so the scene renders at true meters.
3. **Culling**: frustum culling runs in RAW pushed space against the RAW camera — geometry whose
   raw (×11.25) bounding box lies above the raw camera or far outside its raw frustum is culled
   even when its rendered position is inside the view. The camera's raw Y must therefore stay
   above the highest raw geometry Y for a scene to be fully visible.
4. **Init params**: `EnscapeEmbeddedWindow_StartRenderer2` requires
   * `WorkingDirectory = <Enscape>\RendererHost\Renderer` (engine resolves its
     `systemPre.cfg`/`userPre.cfg` and SystemData from there),
   * `BaseDirectoryWriteable = %APPDATA%\Enscape` (engine extracts runtime textures there;
     sharing the real product's dir is required for e.g. the loading overlay),
   * `PreInitCommands` as `wchar_t**` (an array of pointers to NUL-terminated UTF-16
     strings — verified against the decompiled parser in `Enscape.WindowAPI.dll`), and
   * the call to be made on the windowing thread.
5. **Coordinate map**: Revit feet (X-east, Y-north, Z-up) → engine meters
   (X-east, Y-up, Z-south): `(x, y, z) → (x, z, −y) · 0.3048` (`EngineSpace`).
   Camera quaternions follow the GLM right-handed lookAt basis
   `[right = cross(f,u), up, −forward]` (validated with a top-down calibration camera).
6. **Fov**: `CameraParams.fov` is horizontal degrees with `fovType=HORIZONTAL`.
7. **Startup sequence** (mirrors Chaos' host):
   `Enscape_InitializeLogging` → `EnscapeEmbeddedWindow_StartRenderer2` →
   await `OnInitialized` callback → `EnscapeInstance.AttachToExistingInstance` →
   `BeginApiScope` → world/camera/sun/capture APIs.
8. **World update**: `UpdateWorld(callback)` is asynchronous — the world becomes visible
   only when the completion callback fires (return code 0).

These constants and behaviors live in `EngineBridge` (host) and `EngineSpace` (plugin)
and are the only places that would need adjustment for a different Enscape build.

## Legal / ethical scope

* PowerPotion **does not modify, patch, or redistribute** any Chaos/Enscape binary. It *uses* the
  locally-installed, licensed Enscape engine at runtime through its shipped managed API assembly
  (`Enscape.RendererApi.dll`), the same way the shipped RendererHost does.
* License/DRM code is **not ported** — the personalized host simply never calls it. Your Enscape
  installation remains licensed and untouched; the engine's own runtime behavior (including any internal
  entitlement enforcement, if present) is preserved as-is.
* VR, account, telemetry and cloud-upload features are **not implemented**.
* `docs/rendererapi-surface*.txt` are reference notes of the observed public API surface, not Chaos code.

## References

* `docs/rea-report-revit-plugin.md` — full REA evidence report on the Revit-side plugin.
* `docs/rea-report-rendererhost.md` — full REA evidence report on the renderer host + native API + license-gate map.
* `docs/rendererapi-surface*.txt` — reflection dumps of the shipped managed API assembly.
