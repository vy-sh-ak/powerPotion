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
   which takes the host's top-level HWND + the renderer placeholder's screen rectangle, an init-params
   struct (working dir, pre-init console commands), and callbacks. The engine creates its own window at
   that rectangle; the host re-styles it as a child and reparents it into its visual tree.
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

## Engine behavior verified against the shipped host (4.7.0.57)

Facts re-established from the decompiled `Enscape.RendererHost.dll`/`Enscape.RendererApi.dll`,
the decompiled Revit plugin, and controlled renders on the installed engine:

1. **Units**: pushed geometry is consumed **1:1 in meters**. Enscape's Revit plugin converts
   Revit feet → meters and applies the axis rotation once at the world root
   (`WorldTransformation = Scale(0.3048) · WorldRotation`, `WorldRotation = [X, −Z, Y]`).
   PowerPotion applies the same conversion per vertex/instance (`EngineSpace`). A 1 m cube
   with the camera 10 m away renders at the expected size — no calibration factor is needed.
2. **Camera**: `CameraParams.fov` is in **radians** (the shipped host calls
   `camera.SetFov(fieldOfView.Radians, type)`); `FovType.HORIZONTAL` maps to Revit's
   `ViewOrientation`/camera info. Position/orientation are in meters with the same
   world rotation; the camera basis is `[right, up, −forward]`.
3. **World push**: `UpdateWorld(callback)` is asynchronous; the frame appears when the
   callback returns 0. Push it from the UI thread and do not block waiting for completion.
4. **Window embedding** (this is why a naive HwndHost shows a second window):
   `EnscapeEmbeddedWindow_StartRenderer2` is started with the **top-level** window handle and
   the renderer placeholder's **screen rectangle**; the engine creates its own window there.
   The host then re-styles it (`WS_POPUP` → `WS_CHILD`, Enscape's `ToggleChildWindowStyle`),
   reparents it into its visual tree (`RendererControl : NativeControlHost`) and resizes it.
   PowerPotion mirrors this with `SetParent` + `SetWindowPos` inside `RendererSurface`.
5. **Init parameters**: Chaos' host leaves `WorkingDirectory` and `BaseDirectoryWriteable`
   **null** (the engine derives them from its module location — `Renderer\Bin64` → `Renderer`
   for `systemPre.cfg`, roaming app-data for caches). Setting them to custom paths makes the
   engine miss `systemPre.cfg`; PowerPotion leaves them null as well.
6. **Host application**: the Revit host reports `HostApplication.REVIT`; pre-init commands are
   `g_displayLoadingScreen 0`, `g_trial`, `g_locale`, plus `r_hwrt`/`r_dlss` (these two are only
   accepted **before** initialization).
7. **Scene export**: Revit delivers instance geometry in the symbol's local coordinates; every
   nested instance/link transform must be composed to world space. Facets keep Revit's
   outward CCW winding (the feet→meters/axis conversion is a proper rotation). PowerPotion
   flattens geometry to world space and pushes identity instance matrices.
8. **Callback threads**: engine callbacks (log/initialized) arrive on native threads. Touching
   WPF objects from them raises `InvalidOperationException` inside the engine's SEH handler,
   which aborts the process — every callback must marshal to the dispatcher.
9. **Live updates**: Revit's `DocumentChanged` is raised inside the transaction; the exporter must
   not run there. PowerPotion only sets a dirty flag and exports from `Idling` (debounced 400 ms),
   with `IdlingEventArgs.SetRaiseWithoutDelay()` keeping the loop hot. In the host, socket messages
   are dispatched asynchronously and camera updates are coalesced (latest wins) so a slow scene
   push cannot replay stale camera frames.

## Canvas, environment and free navigation

* **Sky**: the engine's procedural Enscape sky is used (`HorizonSource.None` — no skybox file,
  `r_skyBoxFromFile 0`). Sun/date/location come from the Revit view (`SunParser` → `SetProjectLocation`
  / `SetDateAndTime`), so the sky, clouds and lighting match the Revit model's real time-of-day.
* **Ground / base**: the engine's infinite ground plane is the base the model sits on. Enscape's
  horizon textures (`RendererHost\Data\Horizons\*.ktx2`, including `whiteGround.ktx2`) are deployed
  into the engine's writable data directory at host start (mirroring Enscape's
  `RendererResourcesUpdater`), which removes the "Texture loading - does not exist" warning and gives
  the ground its proper look.
* **Free navigation** (`NavigationController`, 60 Hz):
  * **Fly** — `W/A/S/D` move, `Q/E` down/up, hold **right mouse** to look, wheel dollies, `Shift` fast,
    `Ctrl` slow.
  * **Walk** — `W/A/S/D` + look, wheel walks forward/back; eye height is clamped (1.7 m), `Q/E` disabled.
  * Keyboard is polled (`GetAsyncKeyState`); left- or right-button look and the wheel are captured by
    a low-level mouse hook and swallowed **only while the cursor is over the render surface**, so the
    engine cannot apply a competing navigation step and the settings sidebar still scrolls normally.
    Button-down is passed through (never swallowed) so the first click also activates the window.
  * The first interaction inside the renderer detaches from the Revit camera (Enscape's live-camera
    behaviour); the toolbar's **Follow Revit** button re-attaches at the latest Revit view. The
    **Fly/Walk** button switches modes (`sp_fly` is forwarded to the engine as well).
* `pp_nav` console commands (`mode`, `follow`, `look`, `move`, `teleport`, `fov`, `focus`) exist for
  automated testing of the camera path (used by `tests/PowerPotion.SmokeTest`, including a
  `--real-input` mode that injects real `W`/left-drag/wheel events with `SendInput`).

## Branding & theme

The potion mark and the dark-purple theme are generated from one vector definition
(`tools/BrandGen`) into `assets/branding/`:

* SVG + PNG (512/256/32/16) for the logo, a multi-size `powerpotion.ico`, and
  `ribbon-icons.png` as a visual index.
* Eight 16×16 and 32×32 PNG ribbon glyphs (`start`, `stop`, `pause`, `capture`,
  `panorama`, `material`, `sun`, `about`) embedded in `PowerPotion.Revit.dll`
  (Revit icon rules: 16/32 px, 32-bit PNG, transparent, 96 DPI).
* Inno Setup wizard bitmaps and the setup icon.

The host WPF window uses the same palette (deep violet `#140B22` / `#1E1230`, accent
`#B388FF`, gold `#F5B544`) with a dark immersive title bar and the logo in the toolbar.

## Startup, live sync and navigation hardening

* **No Enscape splash**: the engine window is created far off-screen (`-32000, -32000`) and a
  watchdog hides every native window of class `Enscape` (top-level *and* child windows — some builds
  parent the loading screen to the host). The watchdog stays active with the session, but skips the
  single embedded render window once it is revealed. That reveal happens only when the first world
  commits (never on "renderer ready"), because until it has a scene the engine paints its own
  "Enscape 4.7.0" splash inside that window. The branded splash closes at the same moment.
* **Branded splash + loaders**: a separate top-level `SplashWindow` (logo, "Power Potion", animated
  bar, phase text) is used because the render surface is an HwndHost (airspace). It shows
  "Connecting to Revit…" → "Scene received — starting renderer…" → "Importing scene from Revit…".
  Scene refreshes show a compact "Refreshing from Revit…" spinner in the status bar instead of
  covering the view.
* **Live sync** (validated against Enscape's shipped plugin with REA Evidence
  `ev_a52ad799045d27f12640a9449909340f58a8f45fadd8421c54f61d275096b695`):
  Enscape's `DocumentObserver.OnDocumentChanged` queues a transaction and dispatches it through
  `RevitCadDispatcher`, which implements `IExternalEventHandler` and owns an `ExternalEvent`.
  PowerPotion does the same (`LiveSyncService.ExportEventHandler`): `DocumentChanged` only marks the
  scene dirty and raises an `ExternalEvent`; the export runs in a valid Revit API context, debounced
  and retried if Revit denies the event. The Idling path remains as a fallback.
* **Startup race**: the TCP reader is started only after `MainWindow` has subscribed to the
  connection events, so the first scene Revit sends immediately after connecting cannot be dropped.
* **Navigation**: the native window is disabled (`EnableWindow(false)`) and the host owns input,
  eliminating the competing engine camera movement that caused the shaking sky. Mouse look uses a
  low-level hook with cursor recentering (left or right button), the wheel is captured over the
  render area, and the button-down is passed through so the drag also activates the window. Walk mode
  places the eye 1.7 m above the floor
  computed from the scene under the camera, so switching to walk no longer drops below the model.

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
