# PowerPotion ⚡

<p align="center">
  <img src="assets/branding/logo-badge.png" width="150" alt="PowerPotion logo"/>
</p>

**A personalized, clean-room re-implementation of the upstream Revit real-time renderer
plugin** — real-time rendering, material editing, sun/location, cloud & sky, and capture —
**without VR and without authentication**, packaged as a single installer with a clean
uninstaller.

PowerPotion was built from static reverse-engineering evidence gathered with the REA toolkit
on the installed 4.7.0.57 renderer product. It does **not** contain or modify any third-party
code: it loads the rendering engine of your **separately licensed renderer installation** at
runtime, through the same managed API (`Enscape.RendererApi.dll`) the vendor's own host uses.
Anything the engine enforces internally (including entitlement) stays fully in effect.

## What's inside

```
power-potion/
├── assets/branding/              # logo (SVG/PNG/ICO), ribbon icons, installer art
├── docs/                          # REA evidence reports + architecture notes
│   ├── ARCHITECTURE.md            # how the upstream renderer works + how PowerPotion maps onto it
│   ├── rea-report-revit-plugin.md # full REA analysis of the upstream Revit add-in
│   ├── rea-report-rendererhost.md # full REA analysis of the renderer host + native API
│   └── rendererapi-surface*.txt   # reflection dumps of the shipped managed API
├── src/
│   ├── PowerPotion.Protocol/      # wire protocol between Revit add-in and renderer host
│   ├── PowerPotion.Host/          # WPF renderer host (embeds the engine window)
│   └── PowerPotion.Revit/         # Revit 2026 add-in (ribbon, exporter, live sync)
├── installer/PowerPotion.iss      # Inno Setup script → PowerPotionSetup.exe
├── build.cmd                     # builds everything + packages the installer
└── LICENSE.txt                   # personal/educational use, trademark notes
```

## Feature mapping (upstream → PowerPotion)

| Upstream feature (observed) | PowerPotion | Notes |
|---|---|---|
| Real-time render window (RendererHost.exe) | `PowerPotion.Host.exe` | same embedded-window native API |
| Ribbon tab + panels | PowerPotion tab | Start/Stop/Pause, capture, material editor, sun |
| Live geometry sync (CustomExporter pipeline) | `SceneExportContext` | same `IExportContext` callbacks |
| Materials (19 Revit schemas + keywords) | `RevitMaterialParser` | generic/glass/metal/water/prism + keyword typing |
| Photometric lights + IES | `LightParser` | intensity units cd/lm/lux/W + color temperature |
| Sun, location, north angle, time-of-day | `SunParser` | place-and-time and explicit-vector modes |
| Sky, clouds, fog, quality, exposure (CVars) | Host settings panels | `r_skybox*`, `r_cloud*`, `r_fog`, `sys_spec`, … |
| Screenshot + panorama capture | toolbar + ribbon | native capture API |
| Material editor + write-back | `MaterialEditorWindow` | AppearanceAssetEditScope write-back |
| **VR / HMD support** | ✗ removed | not ported by design |
| **Accounts / login / licensing** | ✗ removed | engine-side enforcement untouched |

## Prerequisites

* **Autodesk Revit 2026** (the add-in targets the .NET 8 Revit API)
* **The rendering engine, 4.7+** installed and licensed — PowerPotion locates it at
  `C:\Program Files\Enscape\RendererHost` (or via `POWERPOTION_ENGINE_DIR`) and uses its engine
* Windows 10/11 x64 with a Vulkan-capable GPU (same requirements as the engine itself)

## Build

```cmd
:: from the repository root (adjust paths if needed)
build.cmd
```

`build.cmd` compiles the three projects (Release) and compiles the Inno Setup installer if
`ISCC.exe` is on the PATH. Without Inno Setup you still get the full binaries under each
`src/*/bin/Release`.

## Install / Uninstall

* **Install**: run `installer\Output\PowerPotionSetup.exe`. It
  * copies the renderer host to `%LocalAppData%\PowerPotion`
  * installs the Revit add-in manifest into `%AppData%\Autodesk\Revit\Addins\2026`
  * registers an uninstaller in Add/Remove Programs (per-user, no admin needed)
  * After installation, (re)start Revit and open any 3D view → **PowerPotion tab → Start Live Render**
* **Uninstall**: `Add/Remove Programs → PowerPotion` (or the Start-menu entry). The uninstaller
  * kills a running renderer host,
  * removes the add-in manifest, add-in DLLs, host app, `%LocalAppData%\PowerPotion` and
    `%AppData%\PowerPotion` (settings), leaving Revit and the engine installation untouched.

## How it works (short version)

1. The Revit add-in exports the active 3D view with Revit's own `CustomExporter`
   (`IExportContext`) — the same pipeline the upstream plugin uses — into a compact binary world
   snapshot (geometry, materials, lights, sun, camera).
2. It starts `PowerPotion.Host.exe --port <n>`, which loads the installed
   `Enscape.RendererApi.dll` + native engine, embeds the engine's render window into a WPF
   surface, and pulls the world over a localhost TCP socket.
3. While you model, `DocumentChanged` events trigger debounced re-exports; the active view's
   camera is streamed at ~30 Hz; sun/location and material edits push on demand.
4. The host's settings panels map directly onto the engine's console-variable (CVar) interface
   (`r_skyboxFile`, `r_cloudDensity`, `r_fog`, `g_SunPower`, `sys_spec`, `r_hwrt`, `r_dlss`, …)
   — the same mechanism the vendor's host uses for its Visual Settings.

Full evidence trail, API surface maps and the empirical calibration notes:
**docs/ARCHITECTURE.md**.

## Branding

The dark-purple theme and potion mark live in `assets/branding/`:

* `logo.svg` / `logo-badge.svg` — vector source, plus PNGs at 512/256/32/16 px.
* `powerpotion.ico` — multi-size application/installer icon.
* `icons/*.svg` and `ribbon-icons.png` — the eight Revit ribbon glyphs.
* `installer/wizard*.bmp` — Inno Setup wizard artwork.

The Revit add-in embeds 16×16 and 32×32 PNGs (Revit's icon rules) in
`PowerPotion.Revit.dll`; the host window, taskbar icon, installer and Start-menu
shortcut use the same mark. Everything is generated from one vector definition:

```cmd
dotnet run --project tools\BrandGen
```

## Legal

Personal/educational use. PowerPotion does not bypass or remove any license enforcement;
it simply never calls the managed license layer, and any engine-internal checks in your
licensed renderer installation remain in effect. See `LICENSE.txt`.
