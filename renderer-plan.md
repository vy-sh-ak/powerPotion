# PowerPotion — Standalone Renderer Plan

**Goal:** Remove every dependency on a Chaos Enscape installation. PowerPotion becomes a
self-contained Revit 2026 plugin with its own real-time renderer, built on the same public
(MIT/Apache/BSD) libraries Enscape uses under the hood, and packaged with one installer /
uninstaller.

**Status:** planning complete, all P0 decisions confirmed; stack + inventory re-verified against
the installed Enscape 4.7 via REA on 2026-11-10 (see §13); ready for implementation.

**P0 status (2026-11-10):** complete — standalone Vulkan cube, resize, PNG capture and clean
shutdown verified on the RTX 5060; see `docs/p0-spike-report.md`.

**P1 status (2026-11-10):** complete — Vulkan context/swapchain split, allocator, descriptor
manager, pipeline cache, frame graph, build-time SPIR-V, validation/debug markers, vsync/HDR
selection; 68 swapchain recreations with 0 validation errors and stable allocation stats; see
`docs/p1-report.md`.

**P2 status (2026-11-10):** complete — `WorldSnapshot` scene upload, material SSBO, 4-slot
textures with mips/aniso, GGX PBR (direct + ambient; split-sum IBL follows with P3's sky), normal
mapping + cutout + double-sided, camera, async revision-gated build and live material hot-swap;
see `docs/p2-report.md`.

**P3 status (2026-11-10):** complete — NOAA date/location sun, procedural sky/sun/moon/stars,
`.hdr` and minimal `.exr` skyboxes, 4-cascade PCF sun shadows, point/hemispherical/spot lights,
IES profiles and CVS toggles; NOAA parity and validation-clean captures verified.

**P4 status (2026-11-10):** complete — perspective/orthographic camera with scene-bounds near/far,
two-point perspective, `SectionPlanesUpdate` protocol message + Revit section-box extraction,
shader clipping, Fresnel glass/refraction with per-frame transparency sorting and water/grass
hooks; see `docs/p4-report.md`.

**P5 status (2026-11-10):** complete — offscreen post pass (exposure/adaptation, bloom,
saturation, colour temperature, contrast, vignette, chromatic aberration, motion blur, lens
flare, safe frame), HDR ACES tonemapping, TAA, PNG/JPG/EXR capture (EXR from the HDR target),
run-length panorama and CvarAdapter coverage; see `docs/p5-report.md`.

**P6 status (2026-11-10):** in progress — SSAO from a linear-depth G-buffer, screen-space GI
with a depth-weighted denoise, height fog, raymarched clouds with wind, CPU frustum culling and a
dynamic render-scale/upscale path are implemented and verified; DDGI/OIDN, full FSR 2/3,
ray-traced shadows/reflections and art modes remain follow-ups; see `docs/p6-report.md`.

**P7 status (2026-11-10):** source cutover complete — `EngineBridge`, `RendererSurface`,
`EnscapeLocator` and the entire Enscape namespace are removed; the host always uses the standalone
Vulkan renderer; build/installer/README/architecture docs are updated; `tests/no-enscape.ps1` is
the CI guard. Clean-machine installer testing remains a manual release check.

> **Deferred work and approximations** are tracked in `docs/NOT-IMPLEMENTED.md` (P6 long-tail,
> MVP gaps such as `g_sunVec` and rect/line area lights, `sys_spec` presets, golden/perf tests,
> and the settings that are accepted but have no effect).

---

## 1. Confirmed decisions

| # | Decision | Value |
|---|---|---|
| 1 | Renderer stack | **Vulkan 1.3 via Silk.NET (MIT)** — mirrors Enscape's native Vulkan renderer |
| 2 | Settings surface | **Keep the existing CVar façade** (`r_*`, `g_*`, `sp_*`) and map each command to a typed renderer parameter, so the current WPF settings UI and `HostSettings` keep working |
| 3 | Fidelity order | **MVP first**: raster PBR + sun/sky + shadows + capture; GI / volumetric clouds / FSR / RT afterwards |
| 4 | Deployment | **Standalone** — no Enscape installed, no Enscape files referenced, loaded, or shipped |
| 5 | Process model | Keep `PowerPotion.Revit.dll` ⇄ TCP ⇄ `PowerPotion.Host.exe`; the renderer runs **in-process in the host**, owning a Vulkan surface on an `HwndHost` child window |
| 6 | Section planes | **Add the small `SectionPlanesUpdate` protocol message** (P4) + Revit-side extraction of active section/clip boxes; renderer clips via shader — section planes ship in MVP |
| 7 | EXR support | **Minimal hand-written EXR reader/writer** (uncompressed + zlib scanline) — for skybox loading and EXR capture; **no third-party EXR dependency** (decided 2026-11-10) |
| 8 | Vulkan baseline | **Vulkan 1.3 required** — no 1.2 fallback path (decided 2026-11-10); weaker 1.3 GPUs get a reduced quality preset, logged GPU caps |

If (2) or (3) should change, only the affected phases below are re-scoped; the rest stands.

---

## 2. What exists today (the thing being replaced)

```
PowerPotion.Revit.dll
  └─ PowerPotion.Protocol (binary frames, renderer-agnostic DTOs)
       ⇄ localhost TCP ⇄
PowerPotion.Host.exe (WPF)
  ├─ EngineBridge.cs        ← Enscape.RendererApi + native Enscape engine (TO REMOVE)
  ├─ RendererSurface.cs     ← HwndHost that reparents the Enscape render window (TO REWRITE)
  ├─ EnscapeLocator.cs      ← finds C:\Program Files\Enscape (TO DELETE)
  ├─ MainWindow.xaml.cs     ← UI, CVar emission, capture, live sync (KEEP, minor edits)
  ├─ NavigationController   ← host-owned fly/walk camera (KEEP)
  └─ HostSettings.cs        ← CVar list (KEEP; becomes renderer parameters)
```

The **renderer seam** is small and already clean. `MainWindow`, `RendererSurface`,
`NavigationController` and `SelfTest` only use these members of `EngineBridge`
(Enscape-specific extras that vanish with the rewrite: the `EngineBridge(enscapeDir, dataDir)`
constructor and the static `InstallAssemblyResolver`):

- lifecycle: `StartRendererNative`, `AttachWhenReadyAsync`, `StopAsync`, `Dispose`, `IsRunning`
- surface: `NativeWindowHandle`, `Resize`, `NotifyFocus`, `NotifyMinimize`, `FocusRenderWindow`
- scene: `ApplyWorld`, `OverwriteMaterial`, `ApplySun`, `ApplyCamera`, `ReadCamera`,
  `SetSectionPlanes`
- settings: `ExecuteConsoleCommand`, `WaitForIdle`, `WaitForFrames`
- capture: `CaptureScreenshot`, `CaptureDone`, `CaptureFinished`
- diagnostics: `EngineVersion`, `Log`, `ClosedFromEngine`, `WorldUpdateFinished`,
  `QueryInstanceBoundsAsync`, `PreInitCommands`

`NavigationController` consumes the bridge too (`ApplyCamera`/`ReadCamera`/`ExecuteConsoleCommand`
for `sp_fly`, `FocusRenderWindow`) **and** needs the render-window HWND
(`SetRenderWindow`) for raw-input capture; `MainWindow` uses the HWND for focus-forwarding
(`IsOverWindow`). Keep both needs in mind for §4.2.

`PowerPotion.Protocol` needs **only one small addition** for MVP (see below). The DTOs already
define everything else the renderer consumes — `WorldSnapshot` (models, materials, lights,
sun, camera; defined in `ProtocolIo.cs` together with `TextureSlotDto`, the rest in
`Messages.cs`), `ModelDto` (positions/normals/UVs/backside UVs/indices/material groups),
`MaterialDto` (PBR + 4 texture slots), `LightDto` (photometric + IES), `SunUpdate`,
`CameraUpdate` (perspective/ortho/two-point), `CaptureRequest`, `ConsoleCommand`,
`MaterialUpdate`.

> **Correction (verified in code):** there is **no** `SectionPlanes` message in the protocol.
> `EngineBridge.SetSectionPlanes` exists but has **zero callers** — `MainWindow` never invokes
> it, and the Revit add-in exports nothing section-related either. Section planes are currently
> dead plumbing end-to-end. **Decision (confirmed 2026-11-10):** P4 adds the tiny
> `SectionPlanesUpdate` protocol message (`float[]` xyzd quads, ≤ 8 planes) plus Revit-side
> extraction, and the renderer implements the clip — so section planes ship in MVP.

---

## 3. Third-party stack

### 3.1 Reused — the public libraries Enscape itself ships

| Concern | Enscape artifact | License | Our package |
|---|---|---|---|
| Graphics API | native Vulkan (delay-imported `vulkan-1.dll`) | Apache-2.0 | Vulkan 1.3 |
| Vulkan binding | — | — | **Silk.NET.Vulkan** (MIT) |
| Window/surface | — | — | raw **`VK_KHR_win32_surface`** on the `HwndHost` child HWND (Silk.NET.Windowing is **not** used — it creates its own windows and cannot embed in WPF) |
| Shader compiler | `shaderc_shared.dll` (imported by `Enscape.Renderer.dll`) | Apache-2.0 | **Silk.NET.Shaderc** (MIT binding, 2.23.0) + shaderc — build-time only if `.spv` is embedded |
| Shading language | 367 `.glsl` sources | — | **our own GLSL** compiled to SPIR-V |
| Math | `GlmSharpStd.dll` 0.9.8 | MIT | **GlmSharpStd 0.9.8** NuGet (exact version Enscape uses; netstandard2.0, single published version) |
| Asset import | `assimp-vc143-mt.dll` 5.0 | BSD-3 (modified 3-clause) | **AssimpNet** (MIT wrapper) — for optional asset import |
| Upscaling | `ffx_fsr2_api*.dll`, `ffx_fsr3_*` (delay-imported) | MIT | **FSR 2/3** (phase 6) |
| HDR imaging | `OpenEXR-3_1.dll`, `Imath-3_1.dll` | BSD-3 | **our own minimal hand-written EXR reader/writer** (uncompressed + zlib-compressed scanline; write for capture, read for skyboxes) — decided 2026-11-10, no third-party EXR dependency (no mainstream .NET EXR package exists anyway); Radiance `.hdr` (RGBE) skyboxes also supported (trivial public-domain format) |
| Compression | `zlib1.dll` 1.2.8 | zlib | no native zlib needed — .NET `System.IO.Compression` (deflate) covers EXR zip scanlines if compression is enabled; PNG/JPG codecs carry their own |
| Texture container | `*.ktx2` horizons | Apache-2.0 | **KTX2 / Basis Universal** — optional, only if we ship BC-compressed skybox/ground textures of our own; MVP needs WIC/STB/EXR only. Never ship Enscape's `.ktx2` files |
| Image decode | — | — | Windows WIC (built-in) and/or **StbImageSharp** (public domain, 2.30.x) |

### 3.2 Replaced — Enscape proprietary components

| Enscape | Replacement |
|---|---|
| `nvngx_dlss.dll` (DLSS, "NVIDIA DLSS – DVS PRODUCTION") | FSR 2/3 (MIT): `r_dlss` maps to FSR on/off |
| REBLUR / RELAX / SIGMA (NVIDIA NRD denoisers, shipped as precompiled `*.cs.spirv` + `NRDWrapper.glsl`) | Intel OIDN (Apache-2.0) or a custom spatiotemporal denoiser |
| `cudart64_12.dll` (CUDA Runtime 12.3 — a DLSS/NGX-side dependency, **not** imported by `Enscape.Renderer.dll`) | not needed |
| NoesisGUI (`Noesis.dll` 2.2.6 — in-engine UI, 861 imports) | WPF (already used) |
| 3Dconnexion `TDx.SpaceMouse.Navigation3D.dll` | optional raw HID; not in scope |
| Autodesk RPC (`RPC*.dll`) | already replaced by Revit `CustomExporter` |
| Audio: `sfml-audio-2.dll`, `OpenAL32.dll`, `mpg123.dll` (sound sources / auralization) | out of scope — our protocol carries no sound sources; no replacement needed |
| Video: `avcodec-58`/`avformat-58`/`swscale-5` (FFmpeg, LGPL) | `rec_*` capture, if ever in scope → Windows Media Foundation first (see §12) |
| Enscape `.glsl` / `.spirv` / `.ktx2` / textures | **our own shaders and assets** — no Enscape content is copied or shipped |

### 3.3 Legal / clean-room rules

- Enscape's shader sources, textures, meshes and native binaries are **copyrighted**. Use the
  shipped file/pass names only as a *feature inventory*; never copy code, constants or assets.
- Implement techniques from public literature: Cook–Torrance/GGX, split-sum IBL, CSM, LTC area
  lights, Hosek–Wilkie/Preetham atmosphere, shadow-map filtering, TAA, OIDN, FSR.
- Keep `docs/rea-report-*.md` and `docs/rendererapi-surface*.txt` as historical evidence, but
  mark them as no longer used by the product. Add `docs/THIRD-PARTY-NOTICES.md`.

---

## 4. Target architecture

```
src/
├─ PowerPotion.Protocol/          (one addition: SectionPlanesUpdate, see §2)
├─ PowerPotion.Revit/             (one addition: section-plane extraction, see P4)
├─ PowerPotion.Rendering/         (NEW — the renderer + Enscape-shaped facade)
│   ├─ Vulkan/
│   │   ├─ VulkanContext.cs       instance, physical/logical device, queues, surface, swapchain
│   │   ├─ Swapchain.cs           acquire/present, resize, HDR/format selection
│   │   ├─ Allocator.cs           dedicated + sub-allocated memory, staging ring, deferred frees (no VMA dependency in MVP — see §12)
│   │   ├─ DescriptorManager.cs   bindless (VK_EXT_descriptor_indexing), per-frame sets
│   │   ├─ PipelineCache.cs       pipeline layout/cache, shader module cache
│   │   └─ FrameGraph.cs          passes, barriers, transient images/buffers, async compute
│   ├─ Scene/
│   │   ├─ GpuScene.cs            uploaded models/instances/materials/lights/sections
│   │   ├─ MeshUploader.cs        positions/normals/uv/backside-uv/indices/groups → GPU
│   │   ├─ MaterialBuffer.cs      PBR params + 4 texture slots, UV mat3, flags
│   │   ├─ LightBuffer.cs         sun, spot, point, line, rect, circle + IES
│   │   ├─ TextureCache.cs        WIC/STB/DDS/KTX2 + .hdr/.exr skybox load, sRGB↔linear, mips, samplers
│   │   └─ SceneUpdater.cs        revision-aware async upload, resize-safe
│   ├─ Camera/
│   │   ├─ CameraState.cs         engine-space pos/quat, FOV, ortho, two-point
│   │   └─ Projection.cs          perspective/ortho/two-point matrices, clipping
│   ├─ Passes/
│   │   ├─ DepthPrepass.cs
│   │   ├─ ShadowPass.cs          CSM sun + spot/cube artificial lights
│   │   ├─ OpaquePass.cs          forward+ PBR
│   │   ├─ AlphaPass.cs           glass / alpha-blend / cutout
│   │   ├─ SkyPass.cs             atmosphere, sun disk, moon, stars, skybox
│   │   ├─ CloudPass.cs           (phase 6) volumetric/2.5D clouds
│   │   ├─ AoPass.cs              (phase 6) SSAO/SSGI
│   │   ├─ CompositePass.cs
│   │   ├─ PostPass.cs            exposure, ACES, bloom, grading, vignette, CA, flare, TAA
│   │   ├─ SectionPass.cs         clipping planes
│   │   └─ CapturePass.cs         offscreen + panorama
│   ├─ Shaders/                   our GLSL sources + build-time compile to .spv
│   ├─ Settings/
│   │   ├─ RendererSettings.cs    typed parameters (quality, sky, sun, post, capture)
│   │   └─ CvarAdapter.cs         "key value [line]" → RendererSettings
│   ├─ Capture/
│   │   ├─ Screenshot.cs          PNG/JPG via WIC or STB, EXR via our minimal writer
│   │   └─ Panorama.cs            6 cube faces → equirect, GPU or CPU stitch
│   ├─ RendererBridge.cs          implements the host seam (same names as EngineBridge)
│   └─ Renderer.cs                public entry point / lifetime
└─ PowerPotion.Host/
    ├─ Rendering/VulkanSurface.cs (NEW — HwndHost that creates the child HWND for Vulkan)
    ├─ MainWindow.xaml.cs         (keep; point at RendererBridge)
    ├─ NavigationController.cs    (keep; camera + sp_fly + focus; HWND now comes from VulkanSurface)
    ├─ HostSettings.cs            (keep; CVar names unchanged)
    ├─ SelfTest.cs                (rewrite: no enscapeDir, use RendererBridge)
    └─ EngineBridge/RendererSurface/EnscapeLocator  (DELETE)
```

### 4.1 Window / surface path

```
WPF Window
  └─ VulkanSurface : HwndHost
       ├─ CreateWindowEx(WS_CHILD)  → child HWND
       └─ Renderer.Initialize(hwnd, w, h)
            └─ vkCreateWin32SurfaceKHR(hwnd) → swapchain → present loop
```

Replace the Enscape-specific "start with top-level HWND + offscreen rect, then reparent the
native window" dance with a direct child-HWND surface. Delete the splash-hiding watchdog, the
`ToggleChildWindowStyle` logic, `SetParent`, `EnableWindow`, and the engine-window enumeration.
The branded splash stays, but it can now disappear as soon as the first frame is presented.

### 4.2 Renderer contract (the "binding" we recreate)

`RendererBridge` keeps the names `MainWindow` already calls, so the UI diff stays small:

```csharp
public sealed class RendererBridge : IDisposable
{
    public bool IsRunning { get; }
    public string EngineVersion { get; }          // our renderer version + Vulkan device
    public event Action<string>? Log;
    public event Action? ClosedFromEngine;
    public event Action<bool>? CaptureFinished;
    public event Action<int>? WorldUpdateFinished;

    public void Initialize(IntPtr hwnd, int width, int height);
    public void Resize(int width, int height);
    public void NotifyFocus(bool focused);        // pause input-driven updates when host unfocused
    public void NotifyMinimize(bool minimized);   // skip present while minimized
    public void ApplyWorld(WorldSnapshot world, bool applyCamera = true);
    public void OverwriteMaterial(MaterialDto material);
    public void ApplySun(SunUpdate sun);
    public void ApplyCamera(CameraUpdate camera);
    public CameraUpdate? ReadCamera();
    public void SetSectionPlanes(float[] planes);
    public void ExecuteConsoleCommand(string command, bool deferred);
    public void WaitForIdle();
    public void WaitForFrames(ulong frames);
    public void CaptureScreenshot(string filePath, bool panorama);
    public Task<bool> CaptureDone { get; }
    public Task StopAsync();
    public void Dispose();
}
```

**Correction (verified in code):** the host still needs the child HWND — but it now comes from
`VulkanSurface` (the `HwndHost` that created it), not from the bridge. `NavigationController`
uses it for raw-input capture (`SetRenderWindow`) and `MainWindow` for focus-forwarding
(`IsOverWindow`). `VulkanSurface` therefore owns and exposes:

```csharp
public sealed class VulkanSurface : HwndHost
{
    public IntPtr ChildHwnd { get; }              // feeds NavigationController + focus logic
    public void FocusChild();                     // replaces EngineBridge.FocusRenderWindow
}
```

`PreInitCommands` disappears (no engine pre-init); `r_hwrt`/`r_dlss` become normal settings.
Note the current `EngineBridge.Resize` is `(x, y, w, h)` — the rewritten surface calls
`Resize(w, h)`; position is implicit in the child HWND.

### 4.3 CVar → renderer mapping

| CVar | Renderer parameter |
|---|---|
| `sys_spec 0..3` | quality preset: shadow map size, SSAO/SSGI samples, cloud steps, mip bias, FSR mode |
| `r_hwrt`, `r_rtSunShadows`, `r_restir` | RT toggles (phase 6; no-op before) |
| `r_dlss` | FSR enable (label can stay for UI compatibility) |
| `r_skyboxFile`, `r_skyBoxFromFile`, `r_skyboxRotation`, `r_skyboxBrightnessCorrection`, `r_skyBoxUseSunSpot` | skybox / environment |
| `r_cloud*`, `r_fog`, `r_fogHeight`, `r_starBrightness` | atmosphere |
| `g_SunPower`, `g_sunVec`, `g_moonPower`, `r_moonSize`, `r_sunShadowSharpness` | sun/moon/shadows |
| `r_giAmbientAmount` | ambient/GI intensity |
| `r_bloomIntensity`, `r_postSaturation`, `r_postColorTemperature`, `r_postAutoContrast`, `r_vignette`, `r_lensFlare`, `r_motionBlur`, `r_chromaticAberration`, `r_exposureAdaptionSpeed`, `r_lightBrightness`, `r_whiteBackground` | post/lighting |
| `r_showCaptureSafeFrame` | capture overlay |
| `sp_fly`, `sp_viewSync`, `sp_moveSpeedMultiplier`, `sp_mouse*` | navigation (host-side; renderer only consumes camera) |
| `g_displayLoadingScreen`, `g_locale`, `g_trial` | ignored / removed |

`CvarAdapter` must accept the existing quoting (`r_skyboxFile "C:\x.hdr"`), booleans (`0`/`1`),
invariant-culture floats, and unknown commands (log + ignore, never throw).

> Verified scope: the commands the adapter must actually handle today are exactly
> `HostSettings.ToPreInitCommands()` (`r_hwrt`, `r_dlss`) + `ToCommands()` (the `sys_spec`,
> sky, cloud, fog, sun/moon, post and capture rows above), plus `sp_fly` from
> `NavigationController` and `g_sunVec x,y,z` from the explicit-sun path. `g_moonPower`,
> `g_displayLoadingScreen`, `g_locale`, `g_trial` are Enscape-internal (the latter three were
> pre-init/licensing plumbing) — our UI never emits them; map them only if trivial, otherwise
> they fall under "unknown command → log + ignore".

---

## 5. Render graph (MVP)

```
frame:
  ├─ update: camera, sun, settings, scene deltas (revision-gated)
  ├─ depth prepass (optional, quality-dependent)
  ├─ shadow pass
  │    ├─ sun: 4-cascade CSM (2048/4096, PCF/PCSS)
  │    └─ artificial: spot maps, cube maps, area-light LTC (no shadow or cached)
  ├─ sky/atmosphere → environment cubemap + SH/IBL (prefiltered)
  ├─ opaque forward+ PBR (metallic/roughness/specular, normal/bump, AO, emissive, alpha-test)
  ├─ alpha/glass (sorted, refraction via screen-space copy)
  ├─ composite lighting + section-plane clipping
  ├─ post: exposure → ACES → bloom → color grading → vignette → CA → flare → TAA
  └─ present  |  capture (offscreen) / panorama (6 faces → equirect)
```

Phase 6 adds: SSAO/SSGI (or DDGI probe grid), volumetric clouds, height fog, FSR 2/3, OIDN
denoise, optional RT sun shadows / reflections.

### 5.1 Coordinate and unit contract (already established)

- Engine space: **meters, right-handed, Y-up**, Revit `(X,Y,Z) ft → (X, Z, −Y) m`.
- Camera forward = −Z; `CameraUpdate.Quat*` is engine-space.
- Geometry is already world-space (instance matrices may be identity).
- Facets keep CCW outward winding.
- Pushed geometry, lights and camera are **1:1 meters** (no calibration factor).
- `CameraUpdate.FovDegrees` is degrees in the protocol; renderer converts to radians.
- `CameraUpdate.FovType`: 0 = horizontal, 1 = vertical.

---

## 6. Phased plan

### P0 — Contract + walking skeleton (1–2 weeks)

- Add `PowerPotion.Rendering` project (`net8.0-windows`, `x64`, `AllowUnsafeBlocks`).
- Add `Silk.NET.Vulkan`, `Silk.NET.Shaderc` (2.23.0), `GlmSharpStd 0.9.8` packages (all three
  verified on NuGet; shaderc natives come with the Shaderc package — build-time tool only if
  we embed `.spv`).
- Define `RendererBridge` with the seam above; stub everything.
- `VulkanSurface` creates a child HWND; `VulkanContext` creates instance/device/swapchain;
  render a clear color + a rotating test cube with a hard-coded camera.
- Wire `RendererSurface`/`MainWindow` to the new bridge behind a feature flag
  (`POWERPOTION_RENDERER=pp`), keeping the Enscape path temporarily for A/B.

**Exit:** the WPF host shows a Vulkan-rendered clearing inside the existing layout, resizes
correctly, and exits cleanly.

### P1 — Vulkan foundation (2–3 weeks)

- Swapchain recreation (resize/minimize/DPI), HDR format selection, vsync toggle.
- Frame graph: transient resources, barriers, per-frame command buffers, 2–3 frames in flight.
- Allocator (dedicated + sub-allocation, staging ring, deferred frees — no VMA in MVP, see §12),
  descriptor manager (bindless where available), pipeline cache.
- Shader build: `.glsl` → `.spv` at build time via shaderc; embed or ship in `Shaders/`.
- Debug: Vulkan validation layers in Debug; GPU markers; log device/driver.

**Exit:** stable frame loop, no validation errors, resize without leaks.

### P2 — Scene + PBR (3–4 weeks)

- Upload `WorldSnapshot`: interleaved vertex buffer (pos/normal/uv/backside-uv), index buffer,
  `MaterialIndexGroup` ranges, instances.
- Material buffer: diffuse, tint, opacity, emissive, roughness, metallic, specular, refraction,
  bump amount, `MaterialType`/`BumpMapKind`, double-sided, solid glass, white-mode.
- Textures: 4 slots (diffuse/bump/roughness/mask), UV `mat3` transform, inversion, brightness,
  WCS (planar/box), sRGB decode, mips, aniso; WIC/STB/DDS/KTX2.
- PBR: GGX Cook–Torrance, split-sum IBL, normal mapping, alpha-test/cutout, double-sided.
- Scene updates: revision-gated async upload; `MaterialUpdate` hot-swap; no full re-upload on
  camera-only frames.

**Exit:** a Revit scene renders with correct geometry, materials, textures and colors; live
material edits update without stutter.

### P3 — Sun, sky, artificial lights (3–4 weeks)

- Sun from `SunUpdate` (mode 0 date/location/time, mode 1 explicit vector) + north angle.
- CSM shadows with PCF/PCSS, `r_sunShadowSharpness`; contact hardening optional.
- Atmosphere: Hosek–Wilkie/Preetham sky, sun disk, moon, stars, `g_SunPower`.
- Skybox file path (Radiance `.hdr` / our minimal-reader `.exr`) + rotation/brightness/sun-spot;
  procedural fallback.
- Artificial lights: spherical/hemispherical/spot, point/line/rect/circle shapes, photometric
  units (cd/lm/lux/W), IES profiles, `r_lightBrightness`.
- Ambient/IBL from sky, `r_giAmbientAmount`.

**Exit:** time-of-day matches Revit; shadows and artificial lights look correct; IES works.

### P4 — Camera, sections, transparency (2–3 weeks)

- Perspective/ortho projection, horizontal/vertical FOV, near/far from scene bounds.
- Two-point perspective (vertical lines stay vertical) per `CameraUpdate.TwoPointPerspective`.
- Section planes (up to N) via clip distances / shader discard; `SetSectionPlanes`.
  **Includes the missing producer plumbing** (decided, see §2 correction): add the small
  `SectionPlanesUpdate` protocol message + Revit-side extraction of active section/clip boxes,
  wire it through `HostConnection` → `MainWindow` → `RendererBridge.SetSectionPlanes`.
- Glass/refraction (screen-space), opacity sorting, `isSolidGlass`, water/grass material hooks.

**Exit:** ortho and two-point views match Revit; section planes and glass render correctly.

### P5 — Post, capture, settings (2–3 weeks)

- Exposure (auto/manual, adaptation speed), ACES tonemap, bloom, saturation, color temperature,
  auto-contrast, vignette, chromatic aberration, lens flare, motion blur, TAA.
- Screenshot to PNG/JPG (WIC/STB) and EXR (our minimal hand-written writer — no third-party
  EXR dependency); requested resolution.
- Panorama: 6 cube faces → equirect, stereo optional later.
- Safe-frame overlay; capture callbacks (`CaptureFinished`, `CaptureDone`).
- `CvarAdapter`: implement every command in `HostSettings.ToCommands()` + `ToPreInitCommands()`.

**Exit = MVP:** full feature set of the current UI works on our renderer; installer runs on a
machine without Enscape.

### P6 — Quality (4–8 weeks, iterative)

- SSAO + SSGI (or DDGI probe grid), OIDN/custom denoise.
- Volumetric clouds (raymarch) + height fog + wind (`g_windStrength`, `g_windDirection`).
- FSR 2/3 (replace `r_dlss` semantics).
- Optional: Vulkan RT sun shadows / reflections / AO (`r_hwrt`, `r_rtSunShadows`, `r_restir`).
- Art modes (white mode, hatching/watercolor) if desired — separate sub-project.
- Performance: GPU culling, indirect draws, occlusion culling, dynamic resolution.

### P7 — Cutover & packaging (1–2 weeks)

- Delete `EngineBridge.cs`, `RendererSurface.cs`, `EnscapeLocator.cs`, `Enscape/` namespace.
- Remove `Enscape.RendererApi` / `GlmSharpStd` HintPath references; remove `EnscapeDir` and
  `EnsureEnscapeApi` from `PowerPotion.Host.csproj`.
- `Program.cs`: remove `EnscapeLocator.FindRendererHostDir()`, `InstallAssemblyResolver`,
  the `POWERPOTION_ENSCAPE_DIR` env override and the `--locate` Enscape probe (optionally
  repurpose `--locate` to report the selected Vulkan device — the installer's
  `[Run] ... --locate` step stays meaningful).
- `build.cmd`: drop `ENSCAPE_DIR` checks and `-p:EnscapeDir`.
- `installer/PowerPotion.iss`: remove the Enscape prerequisite text (`WelcomeLabel2`),
  the "Verify the rendering engine is found" step wording, and Enscape mentions; ship shaders
  and assets.
- Update `README.md`, `docs/ARCHITECTURE.md`, `LICENSE.txt`; add `docs/THIRD-PARTY-NOTICES.md`.
- Add a CI guard: no `Enscape` path/binary/assembly references in the product projects.
- Clean-machine test: Windows 10/11 + Revit 2026, **no Enscape installed**.

---

## 7. Acceptance criteria

### MVP

- [ ] Revit 3D view renders with correct meters, axes, winding and normals.
- [ ] Materials: diffuse/tint/opacity/emissive/roughness/metallic/specular, 4 texture slots,
      UV transforms, alpha/cutout, double-sided, glass.
- [ ] Sun/date/location drives sky and shadows; explicit sun vector works.
- [ ] Point/spot/rect/line lights with photometric units and IES profiles.
- [ ] Live camera at 60 Hz; debounced geometry and material updates.
- [ ] Two-point/ortho cameras match Revit; section planes clip the render (P4 protocol
      addition + Revit extraction, decided — see §2).
- [ ] Screenshot + panorama written to the requested path/resolution.
- [ ] Every control in the existing settings panels changes the render.
- [ ] 1080p ≥ 60 fps on a mid-range GPU for a typical model (with FSR/dynamic resolution).
- [ ] No Enscape DLL/file is referenced, loaded or shipped (verify with Process Monitor /
      `dotnet` assembly list / `dumpbin`).
- [ ] Installer + uninstaller work on a clean machine without Enscape.

### Non-goals (explicit)

- Bit-identical visual parity with Enscape (different shader implementation).
- DLSS (proprietary), NRD/SIGMA (proprietary), CUDA, NoesisGUI, SpaceMouse SDK.
- VR / HMD support (already removed by design).
- Cloud/asset uploading, accounts, licensing, telemetry.

---

## 8. Testing strategy

| Level | What |
|---|---|
| Unit | `CvarAdapter` (quotes, bools, invariants, unknown keys), projection math (ortho/two-point), IES parser, KTX2/EXR/DDS loading, material mapping |
| Headless render | Offscreen Vulkan render of synthetic scenes (sphere grid, material chart, sun angles) → golden PNG comparison with tolerance |
| Integration | `PowerPotion.SmokeTest` unchanged (protocol) + updated `SelfTest` (no Enscape dir; renders, waits frames, captures PNG) |
| Visual | Reference screenshots per feature (materials, shadows, sky, glass, sections) checked into `tests/reference/` with a diff threshold |
| Performance | Frame-time capture per scene; regression budgets per quality preset |
| Packaging | Clean VM install/uninstall; no Enscape on disk; no unexpected DLLs loaded |

---

## 9. Risks & mitigations

| Risk | Mitigation |
|---|---|
| Renderer scope is large | Strict MVP (P0–P5); P6 is iterative and optional per release |
| Visual quality gap vs Enscape | Calibrate against reference screenshots; use proven public techniques; prioritize lighting/sky/materials |
| Vulkan driver coverage | **Vulkan 1.3 required** (decided 2026-11-10 — no 1.2 fallback path built); weaker 1.3 GPUs get a reduced pipeline/quality preset; log GPU caps. Contingency only: if a 1.2-only GPU ever matters, descriptor indexing is core in 1.2 and 1.3-only features (dynamic rendering, sync2) can be gated — re-open then |
| WPF + Vulkan surface issues (DPI, resize, minimize) | P1 hardening; test on 100/125/150/200% DPI and multi-monitor |
| Shader compile/packaging | Build-time shaderc step; embed `.spv` or ship `Shaders/`; validate on clean machine |
| GPU memory / large models | Staging ring buffers, async upload, texture streaming, instancing, culling |
| Clean-room / legal | No Enscape code/assets; public literature only; third-party notices; keep evidence docs historical |
| FSR/DLSS expectation | FSR only; `r_dlss` maps to FSR; document the difference |
| Denoising | OIDN (Apache-2.0) or custom; no NRD/SIGMA |

---

## 10. Milestones

| Milestone | When (indicative) | Contents |
|---|---|---|
| M0 | end of week 2 | Vulkan window + clear + camera in WPF |
| M1 | end of week 6 | Revit scene visible, PBR + textures |
| M2 | end of week 10 | Sun/sky/shadows/lights |
| M3 | end of week 13 | Camera modes, sections, transparency |
| M4 | end of week 16 | Post + capture + settings → **MVP** |
| M5 | week 20–24 | GI, clouds, FSR, performance; RT optional |
| M6 | week 24–26 | Cutover, installer, clean-machine validation |

Durations assume one experienced graphics engineer; parallelize P2/P3 shader work where
possible.

---

## 11. Immediate next steps

1. Create `src/PowerPotion.Rendering/PowerPotion.Rendering.csproj` with Silk.NET.Vulkan,
   Silk.NET.Shaderc, GlmSharpStd 0.9.8.
2. Define `RendererBridge` + `Renderer` stubs with the seam in §4.2.
3. Add `VulkanSurface` to the host behind `POWERPOTION_RENDERER=pp`; keep the old path for A/B
   until P5.
4. Implement `VulkanContext` + swapchain + clear-pass; verify on the target machine.
5. Add the shader build step (`Shaders/*.glsl` → `.spv`) and a test shader.
6. Only after P0 passes, start P1/P2.

---

## 12. Open items to confirm during P0

- Whether to keep the `r_dlss` UI label mapped to FSR, or rename it "Upscaling (FSR)".
- Whether video capture (`rec_*` CVars) is in scope; if yes, use Windows Media Foundation first
  and FFmpeg (LGPL) only if needed.
- Whether the "art modes" (white mode, hatching/watercolor) are required for v1 or deferred.
- Exact panorama output format(s): equirect PNG/EXR, mono/stereo.
- **Memory allocator** (verified: there is **no** first-party Silk.NET VMA binding —
  `Silk.NET.VulkanMemoryAllocator` does not exist on NuGet). MVP ships a simple dedicated +
  sub-allocation + staging-ring allocator. If fragmentation shows up on large models, pick one:
  `piko.VulkanMemoryAllocator.Silk.NET` (community, 0.0.6, MIT — very early),
  `Ahjo.Vulkan.Vma.Native` (P/Invoke + shipped natives, MIT), or hand-rolled P/Invoke over VMA.

> **Decided 2026-11-10** (now in §1): section planes ship in MVP via the P4
> `SectionPlanesUpdate` protocol message + Revit extraction; EXR via our minimal hand-written
> reader/writer; Vulkan 1.3 required with no 1.2 fallback.

---

## 13. Verification record (REA re-audit, 2026-11-10)

The stack and inventory claims above were re-verified against the installed Chaos Enscape
4.7 (`C:\Program Files\Enscape`) using REA (PE imports via `inspect_pe_imports`, managed
metadata via `inspect_managed_artifact`) plus direct file observation:

- `Enscape.Renderer.dll` (4.7.0.13, x64 PE) **delay-imports `vulkan-1.dll`** (49 functions),
  `ffx_fsr2_api.dll`, `ffx_fsr2_api_vk.dll`, `amd_fidelityfx_vk.dll`, `mpg123.dll`; **statically
  imports** `shaderc_shared.dll` (13 fns), `Noesis.dll` (861 fns), `NoesisApp.dll`,
  `OPENGL32.dll`, `dxgi.dll`, `sfml-audio-2.dll`, `OpenAL32.dll`. Binary contains
  `VK_KHR_win32_surface`, `VK_KHR_swapchain`, `VK_KHR_ray_tracing_pipeline`,
  `VK_KHR_acceleration_structure`, `VK_KHR_ray_query`, `VK_KHR_push_descriptor` strings →
  the "native Vulkan renderer with HWRT" premise is confirmed.
- `nvngx_dlss.dll` = "NVIDIA DLSS – DVS PRODUCTION" (NVIDIA); `cudart64_12.dll` = CUDA Runtime
  12.3.101 and is **not** an import of `Enscape.Renderer.dll` (DLSS/NGX-side only).
- `GlmSharpStd.dll` assembly version **0.9.8.0**; NuGet has `GlmSharpStd` **0.9.8** (only
  published version, fork of Philip-Trettner/GlmSharp, netstandard2.0, MIT).
- `assimp-vc143-mt.dll` 5.0.0.0 ("Open Asset Import Library", assimp team; BSD 3-clause).
- `zlib1.dll` 1.2.8; `OpenEXR-3_1.dll`/`Imath-3_1.dll`/`Iex`/`IlmThread`/`OpenEXRCore` 3.1
  ship in `RendererHost` with a managed `Enscape.OpenExr.dll` wrapper.
- `Renderer\SystemData\Shaders`: **exactly 367 `.glsl`** sources (plus 29 precompiled `.spirv`
  and `ffx_a.h`/`ffx_spd.h`), including NRD `REBLUR_*`/`RELAX_*`/`SIGMA_Shadow*` compute SPIR-V,
  `RTXSunLight.rgen`/`RTXReSTIR.rgen`, `BindlessTexturing.glsl`, `SectionPlanes*.glsl`,
  `SkyAtmosphere.glsl`/`SkyCache.glsl`/`SphericalHarmonics.glsl`, `TemporalAA.glsl`,
  `PanoramaStitch.glsl`, `FsrReactiveMask.glsl` — validating both the §3 inventory and the §5
  MVP render-graph shape. 18 `.ktx2` texture files ship (horizons).
- `Silk.NET.Shaderc` (2.18.0–2.23.0), `Silk.NET.Vulkan`, `StbImageSharp` (2.30.16) exist on
  NuGet; `Silk.NET.VulkanMemoryAllocator` does **not**.
- Host source audit confirms the §2 seam exactly (including the offscreen `-32000` top-level
  start, splash watchdog via `EnumWindows`, `ToggleChildWindowStyle`, `SetParent`), the
  CVar set of `HostSettings.ToCommands()/ToPreInitCommands()`, the `g_sunVec` explicit-sun
  path, and the missing section-plane plumbing described in the §2 correction.

**Post-audit decisions (2026-11-10):** section planes ship in MVP via the P4
`SectionPlanesUpdate` protocol message + Revit-side extraction; EXR support is a minimal
hand-written reader/writer (uncompressed + zlib scanline — no third-party EXR dependency);
Vulkan 1.3 is required, no 1.2 fallback path.
