# PowerPotion — Architecture & Reverse-Engineering Notes

> **Naming note:** the third-party rendering engine PowerPotion loads at runtime is referred
> to as **"Upstream"** throughout the docs, and its vendor as **"the vendor"**. Identifiers
> prefixed `Upstream` (`Upstream.RendererApi.dll`, `UpstreamInstance`, `Upstream_*`, …) are the
> anonymized names of the corresponding managed types / native exports of the engine product
> installed under `C:\Program Files\Enscape\RendererHost`.

PowerPotion is a clean-room, personalized re-implementation of the **Upstream 4.7.0.57 Revit plugin**,
built from static evidence gathered with the REA reverse-engineering toolkit on the installed product
(`C:\Program Files\Enscape`). It keeps the rendering functionality (real-time render, materials, lights,
sun/cloud/sky), removes VR and authentication/licensing, and adds its own installer/uninstaller.

## What was reverse engineered (evidence-backed)

| Area | Evidence |
|---|---|
| Revit plugin structure (`Upstream.Revit.Plugin.2026.dll`) | REA managed-members evidence `ev_5d78d938…` (463 types / 2102 methods / 587 strings) — see `rea-report-revit-plugin.md` |
| IPC layer (`Upstream.Cad.Remote.dll`) | REA evidence `ev_89e328cd…` — custom binary RPC over a localhost TCP socket, `--port` argument |
| Renderer host (`Upstream.RendererHost.dll`) | REA evidence `ev_d41427bf…` (614 types) — see `rea-report-rendererhost.md` |
| Native renderer API (`Upstream.RendererApi.dll`, P/Invoke over `Upstream.Renderer.dll`) | REA native-boundary evidence `ev_7464e9e6…` (186 imports) + full reflection surface dump (`rendererapi-surface*.txt`) |
| Native engine exports | 213 `Upstream_*` C exports in `Renderer\Bin64\Upstream.Renderer.dll` + 8 `UpstreamEmbeddedWindow_*` in `Upstream.WindowApi.dll` |

### the vendor's architecture (observed)

```
Revit (Revit.exe, .NET 8)
 ├─ Upstream.Revit.Plugin.2026.dll          managed add-in (ribbon UI, scene export, live sync)
 │   ├─ RenderingSync: CustomExporter/IExportContext → ExportedWorld → FlatWorld deltas
 │   ├─ Materials: 19 Revit appearance schemas parsed (generic_diffuse, …) + ExtensibleStorage overrides
 │   ├─ Lights: "Generic Photometric Light" asset (IES, temperature, intensity units)
 │   └─ Sun: SunAndShadowSettings → PlaceAndTime / Explicit sun vector
 ├─ Upstream.Cad.Remote.dll                 RemoteCadServer: TCP loopback listener, custom binary protocol
 └─ launches → RendererHost\Upstream.RendererHost.exe --port <n>  (Avalonia UI host)
      ├─ Upstream.License.* / the vendor account  ← license + auth gates (REMOVED in PowerPotion)
      ├─ en2vr / vr_* CVars, HmdType        ← VR support (REMOVED in PowerPotion)
      └─ Upstream.RendererApi.dll → Upstream.Renderer.dll (native Vulkan engine, DLSS/FSR)
```

Key facts that shaped PowerPotion's design:

1. **The renderer engine has no license API.** All licensing in the shipped product lives in the *managed*
   host (`RendererWindow.PerformLicenseCheckAsync`, `CommandState.VerifyLicense`,
   `ScreenshotExecutor.ThrowIfNotAvailable`, `PreInitCommandsCollector` pushing `g_trial`). The native
   `Upstream.Renderer.dll` exports 213 C functions, none license-related. A host that simply never calls the
   license layer gets full engine functionality. (The engine remains the user's licensed Upstream
   installation — PowerPotion does not patch or circumvent anything inside it.)
2. **The engine is embedded via `UpstreamEmbeddedWindow_StartRenderer2`** (from `Upstream.WindowApi.dll`),
   which takes the host's top-level HWND + the renderer placeholder's screen rectangle, an init-params
   struct (working dir, pre-init console commands), and callbacks. The engine creates its own window at
   that rectangle; the host re-styles it as a child and reparents it into its visual tree.
3. **Everything (geometry, materials, lights, sun, camera) is pushed through 24 API scopes** obtained via
   `UpstreamInstance.BeginApiScope()` (`IUpstreamApiScope.TryGetWorldApi/CaptureApi/SettingsApi/…`).
4. **Settings are console-command (CVar) strings** (`r_skyboxFile`, `r_cloudDensity`, `r_fog`,
   `g_SunPower`, `r_hwrt`, `sys_spec`, `r_dlss`, …). The full CVar list is in
   `rea-report-rendererhost.md` §2.
5. **Geometry format**: `ModelGeometryParams` = raw vertex/normal/UV/index buffers + `MaterialIndexGroup`
   ranges; helpers `ModelGeometryParams.Allocate(ModelData, out Action cleanup)` and
   `ModelInstanceParams.FromMatrix(mat4)` exist in the shipped managed wrapper and are used by PowerPotion.
6. **the vendor's coordinate convention** (derived from the plugin's `RevitGeometryParser.WorldTransformation`,
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
           ├─ Upstream/EngineBridge — loads installed Upstream.RendererApi.dll + native engine
           │   (no license calls, no VR, no accounts, no telemetry)
           ├─ MainWindow: HwndHost embedding + WPF toolbar (visual settings, sky/cloud/sun, quality, capture)
           └─ Panels: MaterialEditor, VisualSettings, SkySettings, SunSettings, CaptureSettings
```

Both processes exchange `PowerPotion.Protocol` messages (length-prefixed little-endian binary frames).

## Engine behavior verified against the shipped host (4.7.0.57)

Facts re-established from the decompiled `Upstream.RendererHost.dll`/`Upstream.RendererApi.dll`,
the decompiled Revit plugin, and controlled renders on the installed engine:

1. **Units**: pushed geometry is consumed **1:1 in meters**. the vendor's Revit plugin converts
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
   `UpstreamEmbeddedWindow_StartRenderer2` is started with the **top-level** window handle and
   the renderer placeholder's **screen rectangle**; the engine creates its own window there.
   The host then re-styles it (`WS_POPUP` → `WS_CHILD`, the vendor's `ToggleChildWindowStyle`),
   reparents it into its visual tree (`RendererControl : NativeControlHost`) and resizes it.
   PowerPotion mirrors this with `SetParent` + `SetWindowPos` inside `RendererSurface`.
5. **Init parameters**: the vendor's host leaves `WorkingDirectory` and `BaseDirectoryWriteable`
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

* **Sky**: the engine's procedural Upstream sky is used (`HorizonSource.None` — no skybox file,
  `r_skyBoxFromFile 0`). Sun/date/location come from the Revit view (`SunParser` → `SetProjectLocation`
  / `SetDateAndTime`), so the sky, clouds and lighting match the Revit model's real time-of-day.
* **Ground / base**: the engine's infinite ground plane is the base the model sits on. the vendor's
  horizon textures (`RendererHost\Data\Horizons\*.ktx2`, including `whiteGround.ktx2`) are deployed
  into the engine's writable data directory at host start (mirroring the vendor's
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
  * The first interaction inside the renderer detaches from the Revit camera (the vendor's live-camera
    behaviour); the toolbar's **Follow Revit** button re-attaches at the latest Revit view. The
    **Fly/Walk** button switches modes (`sp_fly` is forwarded to the engine as well).
* `pp_nav` console commands (`mode`, `follow`, `look`, `move`, `teleport`, `fov`, `focus`) exist for
  automated testing of the camera path (used by `tests/PowerPotion.SmokeTest`, including a
  `--real-input` mode that injects real `W`/left-drag/wheel events with `SendInput`).

## Materials

Revit appearance assets use one schema per material family; the diffuse/albedo bitmap is connected
to the **schema's albedo property**, not always to `generic_diffuse`. `RevitMaterialParser` maps each
observed schema to its albedo property using the literals from the vendor's `MaterialParser`
(REA Evidence `ev_21cf950919b02dbddc9127eb00780815fce66e449b13fc0ebab82738322e1de4`; parent
`ev_d6e7aea05573553a7661e2433e87523a3cf608160e6a25fd0e2b40e6f65bf1e9`):

| Schema | Albedo property |
|---|---|
| `GenericSchema` | `generic_diffuse` |
| `MasonryCMUSchema` | `masonrycmu_color` |
| `CeramicSchema` / `ConcreteSchema` / `HardwoodSchema` | `ceramic_color` / `concrete_color` / `hardwood_color` |
| `MetallicPaintSchema` / `MetalSchema` / `MirrorSchema` | `metallicpaint_base_color` / `metal_color` / `mirror_tintcolor` |
| `PlasticVinylSchema` / `StoneSchema` / `WallPaintSchema` / `GlazingSchema` | `plasticvinyl_color` / `stone_color` / `wallpaint_color` / `glazing_transmittance_color` |
| `PrismOpaque/Transparent/Layered/Metal/Glazing` | `opaque_albedo` / `transparent_color` / `layered_diffuse` / `metal_f0` / `glazing_transmission_color` |

Textures are read exactly like the vendor's `ParseTexture`: `unifiedbitmap_Bitmap` (connected asset),
`unifiedbitmap_RGBAmount`, `unifiedbitmap_Invert`, and the `texture_*` / `unifiedbitmap_*` scale,
offset and angle aliases; the angle is degrees → radians. Bitmap paths are resolved with a
Revit-FileLocator-style search (Revit.ini `MaterialLibraryFiles` / `AdditionalRenderAppearancePaths`,
`%ProgramData%\Autodesk\RVT *\Materials{1,2,3}`, `Program Files\Common Files\Autodesk Shared\Materials`,
the document folder) plus a filename index fallback for library-relative paths. Paint/view overrides
are resolved **inside `OnMaterial`** (`MaterialNode.HasOverriddenAppearance` / `GetAppearanceOverride()`):
`MaterialNode` is a transient wrapper and using it later in `OnPolymesh` throws and drops the
geometry, so the resulting `MaterialDto` is cached for the face. The 16/32 texture UV transform
defaults to identity (not a zero matrix). Every parsed material is logged to `revit-sync.log`
(`... schema=... rgb=... tex='...'`), and unresolved bitmaps log
`texture unresolved on '<property>': raw='...'`.

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

* **No Upstream splash**: the engine window is created far off-screen (`-32000, -32000`) and a
  watchdog hides every native window of class `Upstream` (top-level *and* child windows — some builds
  parent the loading screen to the host). The watchdog stays active with the session, but skips the
  single embedded render window once it is revealed. That reveal happens after the first world commits
  **and a one-second settle delay** (never on "renderer ready"), because until it has a scene the
  engine paints its own branded "Loading" screen inside that window. The branded splash stays
  on top during the delay and closes when the renderer is revealed.
* **Responsive splash / no busy cursor**: `SplashPresenter` runs the branded `SplashWindow` on its own
  STA UI thread, and the main window starts off-screen with no taskbar entry. `StartRendererNative`
  blocks the main UI thread for several seconds by design (the engine requires the windowing thread);
  previously that made Windows mark the visible window as unresponsive and show the spinning/loading
  cursor. The separate splash stays responsive, and the main window is moved on-screen only when the
  renderer is ready (Revit) or the first scene commits.
* **Branded splash + loaders**: a separate top-level `SplashWindow` (logo, "Power Potion", animated
  bar, phase text) is used because the render surface is an HwndHost (airspace). It shows
  "Connecting to Revit…" → "Scene received — starting renderer…" → "Importing scene from Revit…".
  Scene refreshes show a compact "Refreshing from Revit…" spinner in the status bar instead of
  covering the view.
* **Live sync** (validated against the vendor's shipped plugin with REA Evidence
  `ev_a52ad799045d27f12640a9449909340f58a8f45fadd8421c54f61d275096b695`):
  the vendor's `DocumentObserver.OnDocumentChanged` queues a transaction and dispatches it through
  `RevitCadDispatcher`, which implements `IExternalEventHandler` and owns an `ExternalEvent`.
  PowerPotion does the same (`LiveSyncService.ExportEventHandler`): `DocumentChanged` only marks the
  scene dirty and raises an `ExternalEvent`; the export runs in a valid Revit API context, debounced
  and retried if Revit denies the event. The Idling path remains as a fallback.
* **Startup race**: the TCP reader is started only after `MainWindow` has subscribed to the
  connection events, so the first scene Revit sends immediately after connecting cannot be dropped.
* **Export target is resolved per export** (not cached at Start): the active `UIDocument`/`View3D`
  are read again every time (Revit replaces `Document` objects when a model is reopened); if the
  active document is a family, the open project is used; if the active 3D view is hidden/empty, the
  document's other 3D views are tried. This mirrors the vendor's
  `GetActiveOrDefault3DViewFromActiveDocument` / `SetActiveViewIfNot3D` / `ResetActiveView`
  (REA Evidence `ev_3ea1e4e9c5caddf615e749c3fa3e1760cf359baf611d92dfc029ed1dd5702c67`).
  Empty worlds are never sent once a scene exists, and the host ignores empty worlds entirely
  (keeping the current scene or the branded splash instead of clearing the renderer and flashing its
  loading screen). Exports are strictly event-driven (initial connection, `EngineReady`, debounced
  changes, view/document activation, Refresh); the Idling loop never starts a full export. Every sent
  world carries a monotonic revision; the host applies the newest queued revision once and ignores
  duplicates/older revisions, so a burst cannot clear and rebuild the scene repeatedly.
* **Debounced live updates + Refresh**: `DocumentChanged` only marks the scene dirty and records the
  time; the Idling loop re-exports after 500 ms of quiet (so dragging a slider or editing several
  elements renders once), with the host showing the compact refresh spinner. The host queues snapshots
  and applies one at a time after the previous engine commit. The ribbon **Refresh** button forces an
  immediate re-export (`LiveSyncService.Refresh`), and resuming from **Pause Updates** also refreshes.
  The smoke test covers the real Revit order (`--late-world`), an empty world before a scene
  (`--empty-first`), duplicate revisions (`--duplicate`), and the live-update path.
* **Navigation**: the native window is disabled (`EnableWindow(false)`) and the host owns input,
  eliminating the competing engine camera movement that caused the shaking sky. Mouse look uses a
  low-level hook with cursor recentering (left or right button), the wheel is captured over the
  render area, and the button-down is passed through so the drag also activates the window. Walk mode
  places the eye 1.7 m above the floor
  computed from the scene under the camera, so switching to walk no longer drops below the model.

## Legal / ethical scope

* PowerPotion **does not modify, patch, or redistribute** any the vendor/Upstream binary. It *uses* the
  locally-installed, licensed Upstream engine at runtime through its shipped managed API assembly
  (`Upstream.RendererApi.dll`), the same way the shipped RendererHost does.
* License/DRM code is **not ported** — the personalized host simply never calls it. Your Upstream
  installation remains licensed and untouched; the engine's own runtime behavior (including any internal
  entitlement enforcement, if present) is preserved as-is.
* VR, account, telemetry and cloud-upload features are **not implemented**.
* `docs/rendererapi-surface*.txt` are reference notes of the observed public API surface, not the vendor code.

## References

* `docs/rea-report-revit-plugin.md` — full REA evidence report on the Revit-side plugin.
* `docs/rea-report-rendererhost.md` — full REA evidence report on the renderer host + native API + license-gate map.
* `docs/rendererapi-surface*.txt` — reflection dumps of the shipped managed API assembly.
