All analysis complete. Here is the technical report.

---

# Chaos Enscape 4.7.0.57 — Managed RendererHost → Native Renderer Bridge: Technical Report

**Artifacts analyzed (read-only, no files modified):**
- `C:\Program Files\Enscape\RendererHost\Enscape.RendererApi.dll` — assembly `Enscape.RendererApi` v4.7.0.0, SHA-256 `4be4efe856f533ad887ef395ca5285124458145935137265ab6deb781ceb9d2c`, MVID `cb5d8413-9448-4344-afb9-9b7fab6ada22`, 311 types / 1725 methods / 186 P/Invoke imports, ReadyToRun.
- `C:\Program Files\Enscape\RendererHost\Enscape.RendererHost.dll` — assembly `Enscape.RendererHost` v**4.7.0.57**, 614 types / 3198 methods.

**Method.** All structural facts below come from REA managed-members Evidence (metadata, decoded IL signatures, call edges, #US strings) and the managed/native boundary Evidence. Coded signature operands (`class:NNN`/`valuetype:NNN`) were decoded by mapping TypeDefOrRef coded indices `(value>>2, tag)` against the retained TypeDef/TypeRef rows. `RendererSettings` string-constant *values* are not surfaced by any REA view (they live in the Constant-table blob, not #US); those were read directly from the DLL with `System.Reflection.Metadata` (read-only raw-metadata observation, cited as such). Facts vs. inference are marked per section.

**Primary evidence IDs cited:**

| ID | Content |
|---|---|
| `ev_f83b5b48…85c19` | RendererApi.dll full member inventory (summary: `ev_1b40482…3d40e`) |
| `ev_7464e9e6…bbbd6` | `inspect_managed_native_boundaries` — P/Invoke imports (186) |
| `ev_afbb9171…48ebd` | `EnscapeInstance` selection (methods/fields/edges/strings) |
| `ev_40dc4398…ac910` | `EnscapeInstance` nested display classes (the real init calls) |
| `ev_4504f345…cb5154` | `EnscapeNativeApi`/`IEnscapeApiNative`/`EnscapeAPI` (all P/Invoke signatures) |
| `ev_b6d8521e…3089f` | geometry/material/texture struct fields |
| `ev_6de33b6f…bd1d`, `ev_c8cdc904…628f` | camera/light/sun/sky/scene/embedded-window struct fields |
| `ev_0921b48c…cbc39` | `IStartupParameters`/`StartupParameters`/`RendererSettings`/`SettingsApi`/`CaptureApi` |
| `ev_9dda5b4e…462c9` | `EnscapeWorldApi`/`EnscapeSceneApi`/`EnscapeSunAndLocationApi` wrappers + CVars in ldstr |
| `ev_a0b8af09…821ce` | `EnscapeNativeWindowApi` + delegate types + `EnscapeApiScope`/`EnscapeApiHandle` |
| `ev_d41427bf…f1565` | RendererHost.dll full inventory (summary `ev_12784322…5158`) |
| `ev_7c0699c9…0bd1b` | `EnscapeRendererManager` |
| `ev_b4716109…3f751` | manager state machines `<StartRendererAsync>d__25`/`<StartAsync>d__26` |
| `ev_70763f6f…22200` | `RendererWindow`, `RendererLicenseStatus`, `LicenseServerConnectionTester`, `LicenseVersionProvider` |
| `ev_effb5a37…1a976` | license state machines, `EnscapeRendererApp`, `Startup`, `PreInitCommandsCollector`, `RendererControl` |
| `ev_bdd93f80…3fb3` | `<CollectPreInitCommandsAsync>d__9` |
| `ev_d0a9a2e4…9e9ac` | all 28 `Enscape.RendererHost.Commands.*` types |
| `ev_d6ebed1e…a47f` | `ToolbarPanel` |
| `ev_2cc49f22…6108f` | `RendererSettingsUpdater`, `ScreenshotExecutor`, `ShutdownManager` |

---

## 1. Renderer bring-up — the exact call sequence

There are **two distinct bring-up paths** in `Enscape.RendererApi.dll`. The host (`Enscape.RendererHost.dll`) uses **only the embedded-window path**; the classic `Enscape_Initialize_From_Plugin` path is what a minimal host would call if it inits the engine directly. Both are documented.

### 1.1 Path A — embedded window (what RendererHost 4.7 actually does)

Observed in `Enscape.RendererManager.<StartAsync>d__26.MoveNext` (ev_b4716109, IL offsets cited) and `RendererWindow.<StartRendererAsync>d__132.MoveNext` (ev_effb5a37):

**Phase 0 — host app boot** (`Enscape.RendererHost.Startup.Main`, `EnscapeRendererApp`, ev_effb5a37):
1. `Startup.RegisterDllImportResolver()` → `NativeLibrary.SetDllImportResolver(assembly, ResolveEnscapeLibraries)` — native DLLs resolve from `ApplicationDirectories.get_RendererBinaries()` (strings `"lib"`, `".dylib"`, `".dll"` confirm per-OS naming).
2. CLI special modes: `--queryGpus`, `--queryDlss`, `--materialPreview`, `--reportModules` (GpuProfileProvider / MaterialPreviewSubprocessProvider) — exit before UI.
3. `EnscapeRendererApp..ctor` connects to CAD (`RemoteCadClient.ConnectAndRunMessageLoopAsync`); `ICadRenderingSync.add_StartRendererRequested` → `EnscapeRendererApp.OnStartRendererRequested` → `StartRendererAndExportAsync` on the UI thread dispatcher.

**Phase 1 — `RendererWindow.<StartRendererAsync>d__132.MoveNext(CancellationToken)`** (ev_effb5a37, returns `Task<EnscapeRendererManager>`):
1. `ICadEnvironment.get_HostCad` (NotImplementedException branch otherwise).
2. `PreInitCommandsCollector.CollectPreInitCommandsAsync()` → `string[]` of pre-init console commands (see §5).
3. Wait for a previous instance to exit: `EnscapeRendererManager.WaitForRendererExitAsync` (error dialog “Start_Error_InstanceNotFullyStopped” if it doesn't).
4. **`TopLevel.TryGetPlatformHandle().Handle`** → the Avalonia host window HWND (this is the only window-handle input).
5. `RendererResourcesUpdater.UpdateResourcesAsync()`.
6. `RendererWindow.get_RendererScreenGeometry` → `Avalonia.PixelRect` (position/size for the native child window).
7. `new EnscapeCallbackOnCloseExt(OnRendererClosed)` (delegate: `void(nint, int)`).
8. **`Enscape.FeatureFlags.IFeatureFlagProvider.get_NewWindowEmbedding` → `IRuntimeFeature.get_IsEnabled`** — this bool is passed to `EnscapeRendererManager.StartRendererAsync` and selects which *native* entry point is used (derived: see below).
9. `EnscapeRendererManager.StartRendererAsync(...)` → awaited.
10. `EnscapeRendererManager.AttachApiInstance(RendererHostEnscapeInstance)` → on failure `DisposeAsync`.

**Phase 2 — `EnscapeRendererManager.StartRendererAsync` → `StartAsync`** (`<StartRendererAsync>d__25` creates the manager, guards a singleton via `Interlocked.CompareExchange(_currentManager)` and `Dispatcher.VerifyAccess`, then awaits `<StartAsync>d__26.MoveNext`):

Managed signature (ev_7c0699c9, decoded):
```csharp
Task<EnscapeRendererManager> StartRendererAsync(
    nint parentWindowHandle,
    Avalonia.PixelRect windowRect,
    EnscapeEmbeddedWindowInitParams initParams,
    EnscapeCallbackOnCloseExt onClose,
    EnscapeCallbackProcRendererErrorExt onRendererError,
    EnscapeEmbeddedWindowCallbacks callbacks,      // struct: OnInitialized/OnMinimizeChange/OnFocusChange
    CancellationToken cancellationToken,
    bool useNewWindowEmbedding);                   // name inferred; see step 8 above
```

Inside `<StartAsync>d__26.MoveNext` (ev_b4716109), in IL order:
1. `new TaskCompletionSource<EnscapeResult>(TaskCreationOptions.RunContinuationsAsynchronously)` (IL 51–54: `ldc.i4.s 64`).
2. Combine close delegate: `OnClose` (manager method) + the passed-in `EnscapeCallbackOnCloseExt` via `Delegate.Combine` → stored for the native call (IL 64–93).
3. `new EnscapeCallbackProcRendererErrorExt(<StartAsync>b__0)` — forwards native renderer-error events to both `EnscapeCallbackProcRendererError` and `…Ext` (IL 98–110).
4. Fill `EnscapeEmbeddedWindowCallbacks`: `OnInitialized += new EnscapeEmbeddedWindowCallback_EnscapeInitialized(<StartAsync>b__1)`, `OnMinimizeChange += …(OnMinimizeChanged)`, `OnFocusChange += …(OnFocusChanged)` (IL 111–218; field refs `EnscapeEmbeddedWindowCallbacks::OnInitialized/OnMinimizeChange/OnFocusChange` resolved from member-ref rows 0x314/0x316/0x318).
5. Pre-init commands pinning: if `initParams.PreInitCommands != null` → encoding is **`Encoding.Unicode` (UTF-16) on Windows, `Encoding.UTF32` otherwise** (IL 273–299), `Select(GetBytes)` → `ToArray` → `GCHandle.Alloc(bytes, GCHandleType.Pinned)` (IL 393).
6. Build `EnscapeEmbeddedWindowInitParams_Native` (IL 408–578) by copying `EnableRpc`, `IsStandalone`, `EnableVideoPathEditor`, `HostApplication`, `WorkingDirectory`, `PreInitCommands = pinned.AddrOfPinnedObject()` (or null), `PreInitCommandsCount = array.Length` (member refs 0x323–0x331). `BaseDirectoryWriteable` not observed being set here.
7. Build managed `StartupParameters` for later attach (IL 580–669): `new StartupParameters()`, `set_EnableRpc(initParams.EnableRpc)`, `set_SetWorkingDirectory(WorkingDirectory != null)`, `set_Standalone(initParams.IsStandalone)`, `set_PreInitCommands(ToList(initParams.PreInitCommands))` → stored in `manager._apiStartupParams`.
8. `_exitWait.Reset()` (AutoResetEvent; IL 726–737).
9. **The native call** (IL 738–951):
```csharp
EnscapeResult result = boolParam
    ? EnscapeNativeWindowApi.EnscapeEmbeddedWindow_StartRenderer2(
          ref initParams_Native, out manager._enscape,
          onCloseExt, rendererErrorExt,
          parentWindowHandle, IntPtr.Zero,
          rect.X, rect.Y, rect.Width, rect.Height,
          ref callbacks)
    : EnscapeNativeWindowApi.EnscapeEmbeddedWindow_StartRenderer(
          /* identical 11 parameters */);
```
   `Verify(result)` throws `InvalidOperationException(result.ToString())` on failure. The `bool` choosing `…StartRenderer2` vs `…StartRenderer` is the 8th `StartAsync` parameter; the only observed producer of that argument is the `NewWindowEmbedding` feature flag in `RendererWindow.<StartRendererAsync>d__132` (derived, high confidence).
10. Await `TCS.Task` (the native invokes `OnInitialized(instanceHandle, EnscapeResult)`; `b__1` sets the TCS result, or `SetException(new EnscapeInitializationException(result, "…"))`). Log string on failure: `"Failed to start rendering into embedded window. More information is available in the renderer logs."`
11. If `!manager.IsRunning` → `DisposeAsync()` (destroy path); `ThrowIfCancellationRequested`; if `IsMinimized` → `set_IsMinimized(false)` (which calls `EnscapeEmbeddedWindow_TriggerMinimizeChanged`).
12. `finally`: free pinned GCHandles; `_nativeDelegates = {onClose, errorExt, callbacks}` (keeps delegates alive).

**Phase 3 — attach the managed API wrapper** (`EnscapeRendererManager.AttachApiInstance(EnscapeInstance)`, ev_7c0699c9):
```csharp
void AttachApiInstance(Enscape.RendererApi.EnscapeInstance instance)
    => instance.AttachToExistingInstance(_enscape, _apiStartupParams,
                                        out _apiOnClose /*EnscapeCallbackProc*/,
                                        out _apiOnRendererError /*EnscapeCallbackProcRendererError*/);
```
`EnscapeInstance.AttachToExistingInstance` (ev_afbb9171) calls `Start(startupParams, func)` where the func (`<>c__DisplayClass27_0.<AttachToExistingInstance>b__0`) **does not call any native init** — it just returns `(EnscapeResult, nint)` — then sets `_externalInstance = true` and copies its internal `_onClose`/`_onRendererError` delegates out through the by-ref params. This is how the native-created instance gets a managed façade.

**Phase 4 — embedding into the Avalonia window** (`RendererWindow.<ConnectToCadAndRendererAsync>d__123`, ev_effb5a37):
- shows “AcquiringLicense” loading text → **`PerformLicenseCheckAsync`** (see §7) → “Loading” text → `StartRendererAsync` (Phase 1) → `StartCadRenderingSessionAsync` → `WaitForRendererIdleAsync` (→ `Enscape_WaitForLoopModeIdle`) → `AddContextMenuCallBack` → on Windows: `ToggleChildWindowStyle(manager.WindowHandle)`, `new RendererControl(manager)` added to the panel children, `PrepareForChildWindow()`, `RestorePinnedStateOnActivation()`.

**Lifecycle natives** (ev_7c0699c9, all observed call edges):
```csharp
EnscapeResult EnscapeEmbeddedWindow_GetNativeWindowHandle(nint instance, out nint hwnd); // manager.WindowHandle
EnscapeResult EnscapeEmbeddedWindow_SetWindowRect(nint instance, int x, int y, uint w, uint h); // SetWindowRect(PixelRect)
EnscapeResult EnscapeEmbeddedWindow_TriggerFocusChanged(nint instance, bool focused);    // set_IsFocused
EnscapeResult EnscapeEmbeddedWindow_TriggerMinimizeChanged(nint instance, bool minimized);
EnscapeResult EnscapeEmbeddedWindow_Uninitialize(ref nint instance);                    // DestroyRenderer
EnscapeResult Enscape_WaitForLoopModeIdle(nint instance);                                // WaitForRendererIdle (Run on Task + WaitAsync)
```
`DestroyRenderer`: `Interlocked.Exchange` nulls `_enscape`/current manager, `EnscapeEmbeddedWindow_Uninitialize(ref _enscape)`, `Verify`, `_exitWait.Set()`, `GC.KeepAlive(_nativeDelegates)`. `DisposeAsync` runs `DestroyRenderer` via `Task.Run` on macOS, `Dispatcher.InvokeAsync` elsewhere. `RendererControl` extends **`Avalonia.Controls.NativeControlHost`**; `CreateNativeControlCore` returns a `RendererHandle(manager.WindowHandle)` — that is the entire Avalonia embedding mechanism (native HWND reparented into the Avalonia visual tree).

### 1.2 Path B — classic in-process init (minimal-host blueprint)

Observed in `EnscapeInstance` (ev_afbb9171) + `<>c__DisplayClass26_0.<Start>b__0` (ev_40dc4398):

```csharp
// EnscapeNativeApi (all extern, module "Enscape.Renderer"):
static extern EnscapeResult Enscape_InitializeLogging(string logDir);
static extern EnscapeResult Enscape_Initialize_From_Plugin(
    string rendererLibraryDir,             // observed: Path.GetDirectoryName(StartupParameters.SetWorkingDirectory-target)
    out nint instance,
    EnscapeCallbackProc onDone,            // void(int)
    EnscapeCallbackProcRendererError onRendererError, // bool(EngineErrorState)
    bool standalone,                       // from IStartupParameters.Standalone
    bool enableRpc,                        // from IStartupParameters.EnableRpc
    bool setWorkingDirectory,              // from IStartupParameters.SetWorkingDirectory (param names inferred)
    bool ?,                                // 4th bool: NOT observed being read from IStartupParameters — unknown
    WindowMode windowMode,                 // INVISIBLE | FLOATING | NOACTIVE | FULLSCREEN | WRAPPED
    string[] preInitCommands, int preInitCommandsCount); // from IStartupParameters.PreInitCommands
static extern EnscapeResult Enscape_Initialize_WithRawInitParams(out nint, bool, string, string[], int);
static extern EnscapeResult Enscape_Uninitialize(ref nint);
static extern EnscapeResult Enscape_GetRenderWindowHandle(nint instance, out nint hwnd);
```
`EnscapeInstance.Start(IStartupParameters, WindowMode)` → `Start(params, windowMode)` → `Start(params, Func<ValueTuple<EnscapeResult,nint>>)` → the big `Start` body: lock; double-start guard; `set_StartupParameters(params ?? new StartupParameters())`; create the five managed callbacks (`EnscapeCallbackProc`, `EnscapeCallbackProcHmdUsage`, `EnscapeCallbackRayTracedLightingStatusChange` ×2, `EnscapeCallbackProcRendererError`); `_startedEvent.Reset()`; logger scope “Starting Enscape”; invoke the Func delegate → **`Enscape_Initialize_From_Plugin`** (logger scope string `"Enscape_Initialize_From_Plugin"`); `EnscapeAPI.IsError(result)` → `MarkAsCrashed` + `throw new EnscapeInitializationException(result, "Error on Enscape Initialize")`; on result == 22 (observed `ldc.i4.s 22` + `bne.un`) → “Uninitializing Enscape” / `Enscape_Uninitialize` / “Finished uninitializing Enscape” retry path; then register the three status callbacks:
```
Enscape_RegisterOnHmdUsageChanged(instance, cb)          → logs "EnscapeAPI.Enscape_RegisterOnHmdUsageChanged returned "
Enscape_RegisterOnRayTracedSunShadowsStatusChange(...)   → logs "…returned "
Enscape_RegisterOnRayTracedArtificialLightsStatusChange(...)
```
…then `Instance = handle` under lock; `EnscapeStarted` event. `Stop()` waits `_startedEvent`, `Enscape_Uninitialize`, `UnloadLibrary`, `ResetInstance`, `NotifyEventEnscapeStopped`. `EnscapeInstance` also exposes `BeginApiScope()` → `EnscapeApiScope` (26 `TryGetXxxApi(out …)` methods, ev_a0b8af09) and `RequestApiHandle`/`ReleaseApiHandle` bookkeeping (`EnscapeApiHandle.HandleResult` throws on error results).

`IStartupParameters` (ev_0921b48c) has exactly 6 members: `bool EnableRpc`, `List<string> PreInitCommands`, `bool SetWorkingDirectory`, `bool Standalone`, `bool StartWithHwrtEnabled`, `bool StartWithDlssEnabled`. The concrete `StartupParameters` implements the first four (+ the two `StartWith*` are not observed as fields there — interface-only; **unknown** who implements the other two).

The embedded-window equivalent, `EnscapeEmbeddedWindowInitParams` (ev_c8cdc904): `{bool IsStandalone, bool EnableRpc, bool EnableVideoPathEditor, HostApplication HostApplication, string WorkingDirectory, string[] PreInitCommands}`; `…_Native` adds `string BaseDirectoryWriteable`, `nint PreInitCommands`, `int PreInitCommandsCount`. `HostApplication` enum: UNKNOWN, REVIT, SKETCHUP, RHINO, ARCHICAD, VECTORWORKS, SDK, ENSCAPE_CLIENT, HOSTED_STANDALONE, TOOLING, WEB, CLOUDSTREAMHOST, TEST.

### 1.3 P/Invoke inventory

186 imports across 2 native modules (ev_7464e9e6): `Enscape.Renderer` (179 functions incl. `Enscape_*` engine API) and `Enscape.WindowApi` (7 `EnscapeEmbeddedWindow_*`). CharSet: **Ansi** on `Enscape_GetLastError/GetVersion/GetVersionWithBuild/GetLogDirectory` (which take `System.Text.StringBuilder` + `ulong` buffers), not-specified elsewhere; all `winapi` calling convention. The full 179-entry `Enscape_*` signature list was decoded from ev_4504f345 — key ones are in §3–§6. Marshalling style (observed): delegates are pinned via `MarshalExtensions.MakePinnedDelegate`/`CallbackHandleWrapper`; strings inside native-bound structs are passed as pinned `byte*` from `PinnableCrossPlatformString` (UTF-16 Windows / UTF-32 otherwise); interface methods carry `modreq(InAttribute)` on by-ref struct params (C# `in`).

---

## 2. Settings application — console commands / CVars (Task 4)

**Mechanism (observed, ev_9dda5b4e + ev_0921b48c):** every renderer setting is applied by executing a console-command string through the native engine:
```csharp
// EnscapeSettingsApi
void ExecuteConsoleCommand(string command, bool deferred); // → IEnscapeApiNative.Enscape_ExecuteConsoleCommand(instance, command, deferred)
void SetSettingsValue(string cvar, string value);           // → RendererSettingsCommandHelper.ParseAsCommand(cvar, value) → ExecuteConsoleCommand
// RendererSettingsCommandHelper.ParseAsCommand = string.Concat(cvar, " ", value)  (whole implementation: String.Concat)
static extern EnscapeResult Enscape_ExecuteConsoleCommand(nint instance, string command, bool);
```
`RendererSettingsUpdater` (ev_2cc49f22) maps ~70 observable settings objects to `RendererSettingsSetter.Set(...)` on `EnscapeStarted` and on settings-data change; `UpdateViewSyncState` shows the live pattern: `BeginApiScope() → TryGetSettingsApi() → ExecuteConsoleCommand("sp_viewSync " + value) → Dispose()`.

**Full list of CVar strings** — from `RendererSettings` const fields (122 names, field list from ev_0921b48c; **values read from the raw Constant table** with System.Reflection.Metadata — REA views do not expose const values):

| RendererSettings field | CVar value |
|---|---|
| CaptionString | `w_captionString` |
| LightViewScaleMin / Max | `r_lightViewScaleMin` / `r_lightViewScaleMax` |
| SkyboxRotation | `r_skyboxRotation` |
| SkyboxUseSunSpot | `r_skyBoxUseSunSpot` |
| SkyboxFromFile | `r_skyBoxFromFile` |
| SkyboxFile | `r_skyboxFile` |
| SkyboxBrightnessCorrection | `r_skyboxBrightnessCorrection` |
| MouseSpeedMultiplier | `sp_mouseSpeedMultiplier` |
| MouseInvertX / MouseInvertY | `i_mouseInvertX` / `i_MouseInvertY` |
| GamePadInvertY | `i_gamepadInvertY` |
| MoveSpeedMultiplier | `sp_moveSpeedMultiplier` |
| PostAutoContrast | `r_postAutoContrast` |
| PostColorTemperature | `r_postColorTemperature` |
| ScreenshotLayered | `r_screenshotLayered` |
| ScreenshotLayeredDepthRange | `r_screenshotLayeredDepthRange` |
| ScreenshotApplyAlphaMask | `r_screenshotApplyAlphaMask` |
| ScreenshotLayeredIdJsonExport | `r_screenshotLayeredIdJsonExport` |
| ScreenshotVerasEdgeDetectionInput | `r_screenshotVerasEdgeDetectionInput` |
| RenderSoundSources | `s_renderSoundSources` |
| CustomOverlayAnchor / CustomOverlay / CustomOverlayTexture | `g_customOverlayAnchor` / `g_customOverlay` / `g_customOverlayTexture` |
| ShowIcons | `showIcons` |
| SubsampleAutomatic | `r_subsampleAutomatic` |
| UpscaleAutomatic | `r_upscaleAutomatic` |
| UpscaleAutomaticDuringCapturing | `r_upscaleAllowCapturing` |
| Grass | `r_grass` |
| HardwareRaytracing | `r_hwrt` |
| WhiteBackground | `r_whiteBackground` |
| SpectatorHeight / Diameter | `sp_height` / `sp_Diameter` |
| ReplaceRpc | `em_replaceRpc` |
| CloudOffsetX / CloudOffsetZ | `r_cloudOffsetX` / `r_cloudOffsetZ` |
| CloudContrails | `r_cloudContrails` |
| CaptionIcon / CaptionMode / CaptionIconFile | `w_captionIcon` / `w_captionMode` / `w_captionIconFile` |
| FogHeight / Fog | `r_fogHeight` / `r_fog` |
| PolystyrolTransmission | `r_polystyrolTransmission` |
| ChromaticAberration | `r_chromaticAberration` |
| MotionBlur | `r_motionBlur` |
| Outlines | `r_outlines` |
| LensFlare | `r_lensFlare` |
| Vignette | `r_vignette` |
| AmbientAmount | `r_giAmbientAmount` |
| TonemapHighlights / TonemapShadows | `r_tonemapHighlights` / `r_tonemapShadows` |
| PostSaturation | `r_postSaturation` |
| CloudDensity / CloudCirrusAmount / CloudVariety | `r_cloudDensity` / `r_cloudCirrusAmount` / `r_cloudVariety` |
| SunShadowSharpness | `r_sunShadowSharpness` |
| MoonSize | `r_moonSize` |
| **SunPower** | **`g_SunPower`** |
| MoonPower | `g_moonPower` |
| StereoMode | `r_stereoMode` |
| PanoramaResolution | `pa_resolution` |
| RecordFps / RecordBitrateMode | `rec_fps` / `rec_bitrateMode` |
| VrCapturePath | `vr_capturePath` |
| CaptureOriginalResolution | `r_captureOriginalRes` |
| CaptureResolution | `r_captureResolution` |
| SpectatorFlySmoothing / SpectatorMouseSmoothing | `sp_flysmoothing` / `sp_mouseSmoothing` |
| VrHmd | `vr_hmd` |
| HorizonPlateRotation / HorizonPlate / HorizonPlateTexture | `r_horizonPlateRotation` / `r_horizonPlate` / `r_horizonPlateTexture` |
| Auralization | `s_auralization` |
| StarBrightness | `r_starBrightness` |
| LightBrightness | `r_lightBrightness` |
| SysSpec | `sys_spec` |
| BloomIntensity | `r_bloomIntensity` |
| LightView | `r_lightview` |
| PolystyrolMode / PaperModelMode | `r_polystyrolMode` / `r_paperModelMode` |
| ForceLod | `r_forceLOD` |
| WindowResolution / RenderingResolution | `r_windowResolution` / `r_renderingResolution` |
| LockedRotationAxes / LockedTranslationAxes | `sp_lockedRotationAxes` / `sp_lockedTranslationAxes` |
| **SunVector** | **`g_sunvec`** |
| ViewSync | `sp_viewSync` |
| ShowVideoEditorUi | `rec_showEditorUi` |
| FreezeAnimationTime | `g_freezeAnimationTime` |
| WindStrength / WindDirection | `g_windStrength` / `g_windDirection` |
| RestMode | `r_restmode` |
| FlyMode | `sp_fly` |
| MiniMap | `r_miniMap` |
| Dlss / Fsr | `r_dlss` / `r_fsr` |
| RayTracedSunShadows / RayTracedArtificialLights | `r_rtSunShadows` / `r_restir` |
| NvidiaShadowDenoiser | `r_rtSunDenoise` |
| DisableTeleport / DisableLookaround | `sp_disableTeleport` / `sp_disableLookaround` |
| UseSpaceMouse | `i_spaceMouse` |
| Locale | `g_locale` |
| DoubleClickTimeout | `i_doubleclickTimeout` |
| AmMode | `am_mode` |
| AmGlassTransparent | `am_glassTransparent` |
| AmLineWidth / AmLineJitter / AmExtendedLinesLength | `am_lineWidth` / `am_lineJitter` / `am_extendedLinesLength` |
| AmPalette | `am_palette` |
| AmHatchedShadows / AmColorGradient / AmBleedingEffect / AmSurfaceDetail | `am_hatchedShadows` / `am_colorGradient` / `am_bleedingEffect` / `am_surfaceDetail` |
| DisplayLoadingScreen | `g_displayLoadingScreen` |
| MouseSunSpeed | `g_mouseSunSpeed` |
| ExposureAdaptionSpeed | `r_exposureAdaptionSpeed` |
| OnlyFlyingControls | `i_onlyFlyingControls` |
| CameraHeavyReportInterval | `api_cameraHeavyReportInterval` |
| LoadTexturesAsync | `m_loadTexturesAsync` |
| Headless | `r_headless` |
| **Trial** | **`g_trial`** |
| (EnscapeAPI) | `NO_GROUP = -1` |

Additional CVar-style strings observed in ldstr (ev_9dda5b4e, ev_2cc49f22, ev_bdd93f80):
- Sun/location: **`g_latitude `**, **`g_longitude `**, **`g_sunVec `** (SetProjectLocation/SetExplicitSun).
- Capture: **`r_showCaptureSafeFrame `** + `0`/`1`; **`pa_stereo `** (panorama configure).
- Pre-init commands pushed at startup (`<CollectPreInitCommandsAsync>d__9`): **`g_displayLoadingScreen`**, **`r_hwrt`** (from `IGpuProfile.get_IsHardwareRaytracingSupported`), **`r_dlss`** (from `GetPrimaryGpuDlssSupportAsync`), **`g_trial`** (from **`Enscape.License.ILicenseLogic.get_IsTrial`**), **`g_locale`** (from `ILanguagesProvider.get_ActiveLanguage().Name`). Values formatted with InvariantCulture through `RendererSettingsCommandHelper.ParseAsCommand`; skipped when overridden by `.cfg` files (`RendererConfigurationParser.ParseConfigurationCommands` + `IProjectSettings.get_PrioritizePreConfigDuringStartup`, log `"Ignoring '<cmd>' on startup due to .cfg override."`).
- Veras/AI path: `r_screenshotLayered`, `r_screenshotVerasEdgeDetectionInput` (ev_2cc49f22 strings).

---

## 3. World / scene / camera push API (Tasks 2–3)

All wrappers (ev_9dda5b4e) follow the same pattern: `IEnscapeApiHandle.get_Api()` + `.get_Instance()` → native call → `HandleResult` (throws on error). Managed signatures:

```csharp
class EnscapeWorldApi : IEnscapeWorldApi {
    EnscapeResult BeginUpdateWorld();                       // Enscape_BeginUpdateWorld(instance)
    EnscapeResult EndUpdateWorld();                         // Enscape_EndUpdateWorld(instance)
    EnscapeResult UpdateWorld(EnscapeCallbackProc onDone);  // pinned delegate; Enscape_UpdateWorld(instance, cb)
    EnscapeResult ClearWorld();
    EnscapeResult CreateModelFromGeometry(int modelId, in ModelGeometryParams p);        // Enscape_CreateModelFromGeometry
    EnscapeResult CreateModelFromGeometry(int modelId, DataModel.ModelData data);        // p = ModelGeometryParams.Allocate(data) (pinned)
    EnscapeResult CreateModelFromGeometry2(int modelId, in ModelGeometryParamsExtended); // Enscape_CreateModelFromGeometryExtended
    EnscapeResult CreateModelFromGeometry2(int modelId, ExtendedModelData data);
    EnscapeResult CreateModelFromRpcData(int modelId, in ModelRpcParams);                // ModelRpcParams.FromRpcData
    EnscapeResult CreateModelFromAssetFile(int modelId, string assetFile);
    EnscapeResult AddModelInstance(int modelId, int instanceId, in ModelInstanceParams);
    EnscapeResult AddModelInstances(IEnumerable<(ModelEntityId, ModelInstanceParams)>, out EnscapeResult[] results); // batch
    EnscapeResult RemoveModelInstance(int modelId, int instanceId);
    EnscapeResult RemoveModelInstances(IEnumerable<ModelEntityId>, out EnscapeResult[] results);
    EnscapeResult RemoveModel(int modelId);
    EnscapeResult RemoveAllMaterials();
    EnscapeResult AddMaterial(int materialId, in MaterialParameter material, in TextureParams textures);
        // converts each TextureSlotParams → TextureSlotParams_Ptr via 4× PinnableCrossPlatformString
        // then Enscape_AddMaterial(instance, id, ref MaterialParameter, ref TextureParams_Ptr)
    EnscapeResult RemoveMaterial(int materialId);
    EnscapeResult AddLight(int lightId, in LightParams);       // → LightParams_Ptr (pinned profile-path string) → Enscape_AddLight
    EnscapeResult RemoveLight(int lightId);
    EnscapeResult AddSoundSource(SoundSourceParams);            // → SoundSourceParams_Ptr → Enscape_AddSoundSource
    EnscapeResult UpdateSoundSource(SoundSourceParams);
    EnscapeResult RemoveSoundSource(int id); EnscapeResult RemoveAllSoundSources();
    EnscapeResult WaitForNumFramesRendered(ulong frames);
}
class EnscapeSceneApi : IEnscapeSceneApi {   // backed by Enscape_AddView/RemoveView/RemoveAllViews
    EnscapeResult AddScene(int id, string name, in SceneParams scene);  // SceneParams = {CameraParams, SceneTimeParams}
    EnscapeResult RemoveScene(int id);  EnscapeResult RemoveAllScenes();
}
```
Camera natives: `Enscape_GetCamera(instance, out CameraParams)`, `Enscape_SetCamera(instance, in CameraParams, CameraTransitionFlags, EnscapeCallbackCameraTransitionEnded)`; events: `Enscape_RegisterCameraParamsChanged`, `Enscape_RegisterMovementModeChanged`, `Enscape_WaitForNumFramesRendered(instance, ulong)`.

**Sun/location** (`EnscapeSunAndLocationApi`, ev_9dda5b4e):
```csharp
EnscapeResult GetDate(out TimeData);                                   // Enscape_GetDate
EnscapeResult SetDateAndTime(DateTime);                                // Enscape_SetDate(instance, day, month, year, (float)TotalHours)
EnscapeResult SetFullDate(in TimeData);                                // Enscape_SetFullDate(instance, ref TimeData)
EnscapeResult SetProjectLocation(ProjectLocation loc);                  // ExecuteConsoleCommand("g_latitude "+Invariant(lat)),
                                                                       // ExecuteConsoleCommand("g_longitude "+Invariant(lon)),
                                                                       // Enscape_SetNorthAngleOffset(instance, NorthAngle),
                                                                       // Enscape_SetTimeZone(instance, TimeZone)
EnscapeResult SetExplicitSun(ProjectLocation, ExplicitSunPosition);     // ExecuteConsoleCommand("g_sunVec " + string.Join(",", ToSunVector()))
EnscapeResult SubscribeToDateParamsChanged(EnscapeCallbackDateParamsChange cb, out IDisposable);
// native decls: Enscape_SetDate(nint,int,int,int,float); Enscape_SetTimeZone(nint,float);
//              Enscape_SetNorthAngleOffset(nint,float); Enscape_SetFullDate(nint,ref TimeData)
```
`ProjectLocation {double Latitude, Longitude, NorthAngle, TimeZone}` (auto-props), `ExplicitSunPosition {double Altitude, Azimuth}` + `ToSunVector()`. `SunMode {UNDEFINED, DATE, VECTOR}`.

---

## 4. Struct layouts (Task 6)

All structs below are `sealed`, `[Serializable]`, **sequential layout** (layout mask 0x8 from TypeDef flags; ev_f83b5b48 types collection). Field order = metadata order. Exact ClassLayout pack/size values are **not exposed** by REA views (9 ClassLayout rows exist) — see §8 Unknowns. `GlmSharp.*` types come from `GlmSharpStd.dll` (vec2/vec3/vec4/quart/mat3/mat4/ivec2/uvec2).

```csharp
[Sequential] struct ModelGeometryParams {          // ev_b6d8521e
    nint   VertexBuffer;            ulong VertexBufferSize;
    nint   UvBuffer;                ulong UvBufferSize;
    nint   UvBacksidesBuffer;       ulong UvBacksidesBufferSize;
    nint   NormalBuffer;            ulong NormalBufferSize;
    nint   IndexBuffer;             ulong IndexBufferSize;
    nint   MaterialIndexBuffer;     ulong MaterialIndexBufferSize;   // MaterialIndexGroup[]
    nint   DecalParamsBuffer;       ulong DecalParamsBufferSize;     // DecalDataParams[]
    ulong  FamilyClass;
    BillboardType BillboardFlags;
}
[Sequential] struct ModelGeometryParamsExtended {
    nint   VertexAttributes;         ulong NumberOfVertexAttributes;  // VertexAttributeBufferDescription[]
    nint   IndexBuffer;              ulong IndexBufferSize;
    nint   MaterialIndexBuffer;      ulong MaterialIndexBufferSize;
    float  LodBias;
    VertexAttributeBufferDescription PhysModelVertices;
    nint   PhysModelIndexBuffer;     ulong PhysModelIndexBufferSize;
    PhysModelType PhysModelType;
    nint   DecalParamsBuffer;        ulong DecalParamsBufferSize;
    ulong  FamilyClass;              BillboardType BillboardFlags;
}
[Sequential] struct VertexAttributeBufferDescription { nint VertexBuffer; VertexDataType DataType; VertexAttributeType AttributeType; ulong NumberOfVertices; }
[Sequential] struct MaterialIndexGroup { const uint NO_MATERIAL, MAX_LOD; uint StartIndex, NumIndices, FrontFaceMaterialID, BackFaceMaterialID, VisibleLODStart, VisibleLODEnd; }
[Sequential] struct ModelInstanceParams { float[] RotationScale; float[] Translation; }   // both [HasFieldMarshal] → marshalled fixed arrays (9/3 floats inferred — see §8)
[Sequential] struct ModelEntityId { uint ModelId; uint InstanceId; }
[Sequential] struct MaterialParameter {
    bool doubleSided; float opacity;
    float diffuseColorR,G,B, diffuseColorA_DUMMY; float imageFade;
    float emissiveColorR,G,B; float emissiveStrength;
    float tintColorR,G,B, tintColorA_DUMMY;
    float roughness; float bumpAmount; float metallic; float specular; float refraction;
    ulong materialType;               // cast of MaterialType enum
    BumpMapKind bumpMapKind;
    WaterParams waterParams; GrassParams grassParams;
    bool isSolidGlass; bool isWhiteModeMaterial;
    static MaterialParameter Default;
}
[Sequential] struct WaterParams  { float WaterColorR,G,B,A_DUMMY; bool OverrideWindDirection; float WindDirectionX, WindDirectionY, WaveHeight, WaveScale, CausticsIntensity; static Default, DefaultOcean, DefaultRiver; }
[Sequential] struct GrassParams  { GrassGeometryType geometryType; GrassTextureType textureType; float Density, Height, HeightVariation, WindMultiplier, DrawDistanceMultiplier; static Default, ShortGrass, TallGrass, WildGrass; }
[Sequential] struct TextureParams { TextureSlotParams DiffuseTexture, BumpTexture, RoughnessTexture, MaskTexture; static Default; }
[Sequential] struct TextureParams_Ptr { TextureSlotParams_Ptr DiffuseTexture, BumpTexture, RoughnessTexture, MaskTexture; }
[Sequential] struct TextureSlotParams     { string File; GlmSharp.mat3 Transformation; bool IsInverted; float Brightness; SlotSource Source; WcsType wcsType; static Default; }
[Sequential] struct TextureSlotParams_Ptr { byte* File;     GlmSharp.mat3 Transformation; bool IsInverted; float Brightness; SlotSource Source; WcsType wcsType; }
enum SlotSource { TEXTURE_SLOT_SOURCE_DEFAULT, FROM_RGB, FROM_RGBA, FROM_R,G,B,A, TEXTURE_SLOT_SOURCE_COUNT }
enum WcsType { NONE, PLANAR, BOX }  enum BumpMapKind (native enum, returned by Enscape_DetectBumpMapType)
[Sequential] struct ModelRpcParams { const int MaxFilenameLengthBytes, GuidLengthBytes; float[] BbSize /*marshal*/; float HeightInFeet; RpcPlacementFlags PlacementFlags; ulong FamilyClass; byte[] ReplacedContentFilename /*marshal*/; byte[] RpcGuid /*marshal*/; bool IsWhiteMode; }
[Sequential] struct DecalDataParams { int IndexStart, NumIndices, MaterialId, BacksideMaterialId; GlmSharp.vec2 TopLeftUv, TopRightUv, BottomLeftUv, BottomRightUv; }
[Sequential] struct Annotation { int Id; GlmSharp.vec3 Position; GlmSharp.vec3 TintColor; string TexturePath; float Scale; AnnotationVisibility Visibility; float Alpha, AlphaHighlighted, AlphaSelected; }
// Lights/sound (nested types, no namespace — ev_6de33b6f / ev_c8cdc904):
[Sequential] struct LightParams         { float x,y,z, red,green,blue,alpha, lightIntensity; LightDistribution lightDistribution; LightShape lightShape; float beamAngle; float[] rotation; float shapeParam1, shapeParam2; string lightProfilePath; }
[Sequential] struct LightParams_Ptr     { … same …; float[] rotation /*marshal*/; byte* lightProfilePath; }
enum LightDistribution { SPHERICAL, HEMISPHERICAL, SPOT }  enum LightShape { POINT, LINE, RECT, CIRCLE }
[Sequential] struct SoundSourceParams_Ptr { int id; byte* file; float x,y,z; uint maxVol; float minDist, maxDist; }
// Camera / time / sky:
[Sequential] struct CameraParams {
    CameraFlags ActiveFields;      // POSITION|ORIENTATION|FOV|RESOLUTION|DOF_AUTOFOCUS|DOF_AMOUNT|DOF_DISTANCE|AUTOEXPOSURE|EXPOSURE|TWO_POINT|PROJECTION_TYPE|ORTHO_WINDOW|GLOBAL_BBOX_VIEW
    GlmSharp.vec3 position; GlmSharp.quat orientation; float fov; FovType fovType;
    GlmSharp.uvec2 resolution; float dofAmount; float dofDistance; bool dofAutoFocus /*marshal*/;
    float exposure; bool autoExposure /*marshal*/; bool twoPoint /*marshal*/;
    ProjectionType projectionType; GlmSharp.vec4 orthoWindow; GlobalBBoxView globalBBoxView;
}
enum FovType { HORIZONTAL, VERTICAL }  enum ProjectionType { PERSPECTIVE, ORTHO }  enum GlobalBBoxView { BACK, RIGHT, FRONT, LEFT, TOP }
[Sequential] struct TimeData / SceneTimeParams { SunMode sunMode; int day, month, year; float hour, diffHoursToGMT, longitude, latitude, sunX, sunY, sunZ; }
[Sequential] struct SkyboxParams { float sunDirX, sunDirY, sunDirZ, averageBrightness; CubemapType cubemapType; }  enum CubemapType { LONGLAT, CROSS_VERTICAL, CROSS_HORIZONTAL, UNKNOWN }
[Sequential] struct SceneParams { CameraParams Camera; SceneTimeParams Time; }
[Sequential] struct Keyframe { GlmSharp.vec3 Position, LookAt; float Timestamp, TimeOfDay, FieldOfView, FocalDistance; (+ Effective* read-only props) }
[Sequential] struct VideoPathParams { float TotalDuration; bool ShakyCam /*marshal*/, EasingInOut /*marshal*/; }
[Sequential] struct GpuInformation { string Model, Vendor, DriverVersion, GpuType; ulong VRamInBytes; GpuFeatureFlags SupportedFeatureFlags; QualityLevel DefaultQualityLevel; RendererSettingFlags DefaultRendererSettingFlags; }
enum QualityLevel { DRAFT, MEDIUM, HIGH, ULTRA }  enum GpuFeatureFlags { NONE, HARDWARE_RAYTRACING, VR }
enum RendererSettingFlags { NONE, HARDWARE_RAYTRACING, RT_SUN_SHADOWS, RT_ARTIFICIAL_LIGHTS, GRASS_CARPET }
```
Supporting enums observed: `WindowMode {INVISIBLE, FLOATING, NOACTIVE, FULLSCREEN, WRAPPED}`, `EnscapeResult` (opaque int result type; `EnscapeAPI.IsError(EnscapeResult)`), `EngineErrorState` (payload of renderer-error callbacks), `MovementMode {FLY_MODE, WALK_MODE}`, `EnscapeSingleFrameConfig {EnscapeStreamingMode Mode, EnscapeStreamingOutputFormat OutputFormat, EnscapeStreamingTransport Transport}`.

---

## 5. Capture API (Task 5)

`EnscapeCaptureApi` (ev_0921b48c + ev_9dda5b4e). Screenshot/video/panorama captures are **callback-driven native calls taking a file path**; completion arrives on `EnscapeCallbackProc` (`void(int)`), progress on `EnscapeCallbackProcProgress` (`void(float)`); overloads return an `IDisposable` (a `CallbackHandleWrapper`/`RendererDisposable`) that keeps the pinned delegate alive:

```csharp
EnscapeResult StartScreenshotCapturing(string filePath, EnscapeCallbackProc onFinished);
EnscapeResult StartScreenshotCapturing(string filePath, EnscapeCallbackProc onFinished, EnscapeCallbackProcProgress onProgress, out IDisposable handle);
EnscapeResult StartBatchScreenshotCapturing(…same two forms…);        // batch views
EnscapeResult StartPanoramaCapturing(…same two forms…);               // stereo config first: ExecuteConsoleCommand("pa_stereo " + …)
EnscapeResult StartCreateStandalone(string exePath, string name, StandaloneMode mode, CallbackProcExt onFinished); // void(int,string)
EnscapeResult StartVideoCapturing(string filePath, EnscapeCallbackProc onFinished [, EnscapeCallbackProcProgress, out IDisposable]);
EnscapeResult StartVideoPreview(int w, int h, EnscapeCallbackProc onFinished);
EnscapeResult ShowCaptureSafeFrame(bool show);  // ExecuteConsoleCommand("r_showCaptureSafeFrame " + (show?"1":"0"))
EnscapeResult ConfigurePanorama(bool stereo);   // ExecuteConsoleCommand("pa_stereo " + …)
EnscapeResult StopCapturing();                  // Enscape_CancelCapturing
// native decls: Enscape_StartScreenshotCapturing(nint, string, EnscapeCallbackProc, EnscapeCallbackProcProgress, bool)
//               Enscape_StartBatchScreenshotCapturing(…) / Enscape_StartPanoramaCapturing(…) / Enscape_StartVideoCapturing(…) — same shape
//               Enscape_StartCreateStandalone(nint, string, string, StandaloneMode, CallbackProcExt)
//               Enscape_StartHostMemoryScreenshotCapturing(nint, NativeMemory, EnscapeCallbackProc)   (Veras/AI host-memory path)
//               Enscape_ConvertToPng(byte[] data, int len, out nint png, out int pngLen)
```
Host-side orchestration: `ScreenshotExecutor.ThrowIfNotAvailable` (ev_2cc49f22) → `IEnscapeInstance.get_IsRunning` (`EnscapeNotStartedException`) **and `ILicenseLogic.get_IsFeatureScreenshotAvailable`** → `EnscapeLicenseInvalidException` (see §7); file names via `FileFormat.get_screenshotFormats`, timestamp `yyyy-MM-dd-HH-mm-ss`, PNG extension; temp files under `IApplicationDirectories.get_Temporary()` with a deletion queue.

---

## 6. RendererHost command surface (Task 8) — all 28 `Enscape.RendererHost.Commands.*` types

From ev_d0a9a2e4 (methods/fields/edges/strings):

| Type | Purpose (observed) |
|---|---|
| `EnscapeUiCommand` | Base `ICommand`: Title/ToolTip/Tag/IsEnabled/IsActive, `CanExecute`→`Execute`→`ExecuteAsync`→`OnExecuteAsync`, error handler interface, hides other windows during dialogs. |
| `CommandState` | Shared enablement engine. Ctor takes **`Enscape.License.ILicenseLogic`**, `IEnscapeInstance`, `IApiCaptureState`, feature flags, HMD/SiteContext/uploads state, settings provider. Hooks `EnscapeInstanceExtensions.AddStartedWeakEventHandler/AddStoppedWeakEventHandler`; `RequiresLicense((ILicenseLogic, bool), …)`, `VerifyLicense(bool)` → **throws `Enscape.License.EnscapeLicenseInvalidException`**; `RequiresRenderer`, `RequiresApiCaptureState`, `RequiresFeature`, `IsLockedInGuiModes/VR/SiteContext/ArtisticMode/WorldUpdate/UploadsMigration`; tooltips use `Licensing_FunctionalityNotAvailableWithTrial_HelpText` / `…NotIncludedInLicense_HelpText`. |
| `ICommandState`, `IEnscapeUiCommandErrorHandler`, `GuiModeTextConverter` | interface + error contract + GuiMode→text. |
| `AuthenticatedEnscapeUiCommand` | Account gate: `EnsureAuthenticatedAsync`/`RequestLoginAsync` via `IChaosAccountManager` (login dialog). |
| `AIEnhancerCommand` | Veras/AI enhancement (ExrImageGenerator, `IChaosAccountManager`, cloud-collab dialog, resolution bounds check, keep-alive scope). |
| `BatchRenderingCommand` | Batch renders views/panoramas via `ViewBatchRenderer` + `CadViewsProvider`. |
| `CapturePanoramaCommand` (+`…MonoCommand`, `…StereoCommand`) | Panorama capture via `IPanoramaCreator`; stereo/mono variants via ctor bool; availability & localized titles. |
| `ChangeApplicationModeCommand` | Switches GuiMode (per-mode icons/tabs). |
| `ChangeNavigationModeCommand` | Fly ↔ Walk (`INavigationState`, `ICameraSyncState`). |
| `ChangeProjectionModeCommand` | Perspective/Two-point/Ortho (`VisualSettingsManager`, `ICameraSyncState`, `IHmdState`). |
| `EnableOrDisableHmdCommand` | VR toggle (`IHmdState`, `IGpuProfileProvider.get_IsVrSupported`-adjacent availability). |
| `MiniMapCommand` | `MiniMapState` toggle → `r_miniMap`. |
| `OpenEnscapeWindowSettingsCommand` / `OpenVisualSettingsCommand` | Open the settings/visual-settings windows (single-instance). |
| `OpenVerasCommand` | Veras window lifecycle; image requests → `RendererStreamingProvider` single-frame capture; cloud registration via `IChaosAccountManager`. |
| `SoundSourcePlacementCommand` | CAD-driven sound-source placement (file dialog in CAD window). |
| `StandaloneExportCommand` | `.exe` standalone export (`IStandaloneExporter`); `SetShowRenderSoundSources` → `RendererSettingsSetter.Set("s_renderSoundSources", …)`; missing-icons check; name randomization; `"Enscape-{0}"`. |
| `StandaloneWebExportCommand` | Web-standalone export + upload (`IWebStandaloneProvider`, **ctor takes `Enscape.License.IEnscapeLicense`**, `IChaosAccountManager`, zip + upload + size confirmation). |
| `TakeScreenshotCommand` / `TakeScreenshotIntoDocumentCommand` | `ScreenshotExecutor`; TakeScreenshot also syncs `CaptureState {IsCapturing, CapturingAllowed}` to CAD via `ICadRenderingSession.SetCaptureStateAsync`. |
| `ToggleHelpCommand` | Help panel (`HelpPanelState.IsExpanded`). |
| `ToggleSafeFrameCommand` | `SafeFrameManager` + `SettingsDataProvider`; bound to `IsSafeFrameActive`. |
| `UploadSceneCommand` | Scene export+upload dialog (cloud). |
| `VraySceneExportCommand` | `.vrscene` export per view (`ViewVraySceneExporter`, `VraySceneDialog`). |

Settings/cloud/sky application is spread across `RendererSettingsUpdater` (section 2; e.g., `TrySetSkybox` copies the skybox file into the renderer via `FileToRendererCopier.TryCopyFile` then sets `r_skyboxFile`/`r_skyBoxFromFile`; horizon plate texture likewise; `SetWindDirectionAngle` computes `g_windDirection "x, 0, y"` from cos/sin) and `SunSettingsUpdater`/`RendererSunAndLocationObserver` in `RendererState`.

---

## 7. License-gating map (Task 9)

The requested literal string “No license when opening renderer window” was **not found** in either assembly’s #US heap (1016 strings in RendererHost searched). The observed equivalents are below. Gating happens at exactly these points; everything else (engine init, window creation, world transfer, rendering) has **no license interaction**:

| Gate | Type / method (state machine) | What it does | Evidence |
|---|---|---|---|
| **Startup ULA acceptance** | `EnscapeRendererApp.EnsureLicenseAcceptanceAsync` (`<EnsureLicenseAcceptanceAsync>d__47`) | `Enscape.Views.Licensing.LicenseAcceptanceWindow.EnsureLicenseAcceptanceAsync()` — blocks until EULA accepted; then starts telemetry (`UserActionCollector.Start`). Also `EnscapeRendererApp.<OnCadConnectedAsync>d__44` checks `ILicenseAcceptanceProvider.get_IsLicenseAccepted`. | ev_effb5a37 |
| **Renderer-window license session** (the main “opening the renderer window” gate) | `RendererWindow.PerformLicenseCheckAsync` (`<PerformLicenseCheckAsync>d__124`) | `CancellationTokenSource.CreateLinkedTokenSource` + `CancelAfter` (timeout, “Waited N seconds for a license session. Giving up…”); `Enscape.License.Services.ProductId.get_Enscape`; **`IChaosSessionManager.BeginSessionAsync(token)`** — the license-session acquisition; on failure logs **“Could not obtain an active license session. Error: …”** and throws `Enscape.License.Services.InactiveLicenseException`; then `Enscape.Views.Licensing.ManualWindowLicense.RegisterOrUpdateWindow` (per-window license registration), checks `IProductSession.get_State`/`get_Error`, `ILicenseExpirationChecker.CheckLicenseExpiration`, throws `InactiveLicenseException` on bad state. | ev_effb5a37, ev_70763f6f |
| **Where it gates the start** | `RendererWindow.ConnectToCadAndRendererAsync` (`<ConnectToCadAndRendererAsync>d__123`) | Shows “AcquiringLicense” loading text, then **`await PerformLicenseCheckAsync()` at IL 111 — strictly before `StartRendererAsync` (IL 225)**. | ev_effb5a37 |
| **Retry loop** | `RendererWindow.RetryEngageLicenseAsync` (`<RetryEngageLicenseAsync>d__126`) | Re-runs `PerformLicenseCheckAsync` + `ConnectToCadAndRendererAsync`; on failure feedback dialog (`StartupError_Feedback_Title/Body`) + `Window.Close()`. Strings: “Retried to engage a license, but could not.” / “Failed while retrying to engage a license.” Triggered by `OnAccountStateChanged` (`ManualWindowLicense.get_IsSessionEngaged`). | ev_effb5a37 |
| **Pre-init command injection of license state** | `PreInitCommandsCollector.<CollectPreInitCommandsAsync>d__9` | Reads `Enscape.License.ILicenseLogic.get_IsTrial` → pushes **`g_trial 0/1`** as a pre-init console command to the engine (plus `g_displayLoadingScreen`, `r_hwrt`, `r_dlss`, `g_locale`). | ev_bdd93f80 |
| **Command enablement** | `Commands.CommandState` | Ctor dependency `Enscape.License.ILicenseLogic`; `VerifyLicense` throws `Enscape.License.EnscapeLicenseInvalidException`; tooltips use licensing localization keys; per-feature gates observed in command ctors: `IsFeatureScreenshotAvailable`, `IsFeaturePanoramaAvailable`, `IsFeatureVerasAvailable`, `IsFeatureWebStandaloneAvailable`, `IsFeatureExeExportAvailable`, `IsFeatureAiEnhancerAvailable`. | ev_d0a9a2e4 |
| **Feature gate at execution** | `ScreenshotExecutor.ThrowIfNotAvailable` | `ILicenseLogic.get_IsFeatureScreenshotAvailable` else `EnscapeLicenseInvalidException`; also `EnscapeNotStartedException` when renderer not running. | ev_2cc49f22 |
| **Commercial-only settings** | `RendererSettingsUpdater.LicenseDependentAction` | `IChaosSessionManager.TryGetLicenseSession` → `ILicense.get_IsCommercial` — action invoked only for commercial licenses. | ev_2cc49f22 |
| **Status enum** | `Enscape.RendererHost.RendererLicenseStatus` | `NORMAL, TRIAL, TRIAL_EXPIRED, ACTIVATION_FAILED, LICENSE_EXPIRED, SERVER_UNREACHABLE, NO_FLOATING_SLOT, DEACTIVATED, GENERIC_ERROR` (declared in host; no method in this assembly observed consuming it — consumers are in `Enscape.ViewModels`/`Enscape.Views`, out of scope). | ev_70763f6f |
| Supporting plumbing | `LicenseServerConnectionTester` (`IChaosSessionManager`→`LicenseClient.PingAsync`, `WebProxySettings`), `LicenseVersionProvider`, `XmlProjectSettings` (proxy settings persistence), `WebServicesModule` (`LicenseWebServiceOptions.UseLicenseEndpoint`), `TrialSurveyHandler` (`ILicenseLogic.get_IsTrial`), uploads (`IEnscapeLicense.EnsureInitializedAsync`, `LicenseStatus.get_Error`) | | ev_70763f6f, ev_d41427bf |

**Implication for a minimal host:** the license machinery is entirely above the `Enscape.RendererApi` layer — no `Enscape.License.*` type is referenced by `Enscape.RendererApi.dll` (its 133 TypeRefs were enumerated; none is a License type), and no native function performs a license check in the observed signatures. A minimal host can omit: `PerformLicenseCheckAsync`, `RetryEngageLicenseAsync`, `EnsureLicenseAcceptanceAsync`, `PreInitCommandsCollector`’s `g_trial` push, `CommandState.VerifyLicense`, `ScreenshotExecutor.ThrowIfNotAvailable`’s license branch, and `RendererSettingsUpdater.LicenseDependentAction` — nothing else in the observed bring-up path (`StartRenderer → AttachApiInstance → world transfer`) depends on them. (Whether the *native engine* independently enforces licensing at runtime — e.g. via the `g_trial` CVar or a network check inside `Enscape.Renderer.dll` — is **not determinable from managed evidence** and is flagged as an open question.)

---

## 8. Toolbar / window UI overlay (Task 10)

Observed (ev_70763f6f, ev_d6ebed1e, ev_effb5a37):

- `RendererWindow.axaml` (`avares://Enscape.RendererHost/RendererWindow.axaml`) names these parts: **`Toolbar`, `BottomPanel`, `LeftPanel`, `RightPanel` (Avalonia `Decorator`s), `RendererPlaceholder`, `LoadingPanel`/`LoadingState`/`LoadingProgress`, `RendererContextMenu`, context-menu items, `ExportProgress`, `Converging`, `LargeLicenseNotice`**. The native HWND is embedded by putting a `RendererControl` (a `NativeControlHost`) into the panel children (§1.1 Phase 4); `ShowRendererPanel(Decorator, RendererWindowHostPanelOrientation)` / `CloseRendererPanel` / `GetRendererPanel` manage which edge hosts panels; `RendererWindowHostPanelOrientation {Top, Left, Bottom, Right}`. `ToggleChildWindowStyle(hwnd, bool)` + `PrepareForChildWindow` adjust WS_CHILD styling of the native window; `DockPanel_LayoutUpdated` measures child sizes.
- `ToolbarPanel.axaml` (`avares://Enscape.RendererHost/ToolbarPanel.axaml`) is the toolbar itself — a pure Avalonia overlay, floating above the native child window. It holds command properties for every Commands type: MiniMap, ToggleSafeFrame, TakeScreenshot, AIEnhancer, BatchRendering, UploadScene, VrayRendering, CapturePanoramaMono/Stereo, OpenVeras, Perspective/TwoPoint/Orthographic projection, OpenVisualSettings, OpenEnscapeWindowSettings, EnableFlyMode/EnableWalkMode, EnableOrDisableHmd, ToggleHelp, StandaloneExport, StandaloneWebExport. XAML-named controls: `TopButtonBar`, `CommandToolBar`, `AppModeOverflowButton`, `ModeTabs` (+ per-GuiMode tab items `GuiModeDefault/Collaboration/Bim/ViewManagement/AssetPlacement/SiteContext/VideoEditor/BuildingPerformance`), `ExpanderButton`, `CaptureButton`, split buttons (`CapturePanoramaSplitButton`, `StandaloneExportButton`), dropdowns (`ProjectionModeDropDownButton`, `MovementModeDropDownButton`), `MinimapButton`, `SafeFrameButton`, `VrModeButton`, `GuiVisualSettings`, `GuiEnscapeWindowSettings`, etc. Availability is driven by `CommandState` (`IsEnabled`/`ToolTip`/`CommandStateChanged`), `IApplicationState.GuiModeChanged`, `RendererActiveDocumentObserver`, and `ValidDocumentOpen`/`IsSupported` styled properties; visual states bound to `Command.IsActive` (`class:Selected`, `class:FlyMode`, `:pointerover`, `:checked`).

---

## 9. Unknowns / limitations (explicit)

1. **ClassLayout pack/size** for the 9 explicit/sequential-layout structs is not exposed by any REA view; field order and types are exact, but byte offsets/sizes are not (the `_Ptr` string fields being `byte*` implies pointer-width-dependent sizes).
2. **FieldMarshal native-type codes** (116 rows) exist — the REA boundary view confirms marshalling is declared — but the blob codes were not retrievable (REA views don’t expose them; my raw-metadata extraction hit a PowerShell/SRM interop limitation). Marked fields (`HasFieldMarshal 0x1000`): `ModelInstanceParams.RotationScale/Translation`, `ModelRpcParams.BbSize/ReplacedContentFilename/RpcGuid`, `CameraParams.dofAutoFocus/autoExposure/twoPoint`, `VideoPathParams.ShakyCam/EasingInOut`, `EnscapeStreamingConfig.EmbedFrameCounter`, `PlaceholderInstanceParams.Default*Color` (fixed arrays inferred, exact `ByValArray` sizes unknown), `EnscapeEmbeddedWindowInitParams_Native.WorkingDirectory/BaseDirectoryWriteable`, `GpuInformation` strings.
3. **Parameter names** are not in REA output; all parameter names in this report are inferred from usage (e.g., `parentWindowHandle`), except where a property/field of the same name was observed.
4. **The 4th `bool` of `Enscape_Initialize_From_Plugin`** is not observed being read from `IStartupParameters` in the display-class IL; candidates (`StartWithHwrtEnabled`, `StartWithDlssEnabled`, or an internal flag) are unverified.
5. **`EnscapeEmbeddedWindow_StartRenderer` vs `…2`** differ only in name (identical managed signatures). The observed selector is the `NewWindowEmbedding` feature flag; the *native-side* behavioral difference is not visible from managed evidence.
6. Whether the native engine performs its own license enforcement (e.g., reacting to `g_trial`, `g_trial` being pushed from `ILicenseLogic.IsTrial`) cannot be determined from these two managed assemblies.
7. `RendererLicenseStatus` consumers live outside the two analyzed assemblies (`Enscape.ViewModels`/`Enscape.Views`/`Enscape.License.*`) — not analyzed here.
8. The literal message “No license when opening renderer window” was not found; the closest observed strings are those quoted in §7.
9. `Enscape.RendererHost.exe` / `Enscape.StandaloneHost.bin` and the native `Enscape.Renderer.dll` exports were not part of this task’s scope (only the two managed DLLs), though the P/Invoke inventory (§1.3) enumerates the expected native export names.
</subagent>