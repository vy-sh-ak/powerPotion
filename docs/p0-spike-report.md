# P0 spike report — standalone Vulkan renderer

**Date:** 2026-11-10
**Machine:** Windows, NVIDIA GeForce RTX 5060, driver 32.0.16.1088
**Runtime:** .NET 8.0.33, .NET SDK 10.0.403
**Vulkan:** 1.4.341 reported by the device; renderer requests Vulkan 1.3

## Result

P0 is complete: a WPF `HwndHost` hosts a standalone Vulkan 1.3 swapchain, renders a rotating
colored cube with a depth-tested forward pass, recreates the swapchain on resize, writes a PNG
capture, and shuts down cleanly. The P0 path does not touch Enscape.

The P0 smoke run:

```text
[vk] Swapchain: 1264x761, 3 images, B8G8R8A8Unorm, PresentModeFifoKhr
[vk] Vulkan renderer ready: NVIDIA GeForce RTX 5060 (Vulkan 1.4.341, driver 0x98960000), format=B8G8R8A8Unorm, extent=1264x761
P0 renderer ready: NVIDIA GeForce RTX 5060
P0 child HWND: 0x7055C
P0 surface size: 1264x761
P0 resize: 1280x800 -> 960x600
[vk] surface resize: 944x761
[vk] surface resize: 944x561
[vk] Swapchain: 944x561, 3 images, B8G8R8A8Unorm, PresentModeFifoKhr
P0 surface size after resize: 944x561
P0 capture: ok C:\Users\Vyshak\AppData\Local\Temp\powerpotion_p0_verified.png
P0 image: 944x561, non-background pixels=20.0%
[vk] Captured 944x561 to 'C:\Users\Vyshak\AppData\Local\Temp\powerpotion_p0_verified.png'.
P0 clean shutdown.
```

Exit code: `0`.

The captured frame is checked in at [`docs/p0-capture.png`](p0-capture.png).

## What was added

| Path | Purpose |
|---|---|
| `src/PowerPotion.Rendering/PowerPotion.Rendering.csproj` | `net8.0-windows`, x64, unsafe; Silk.NET.Vulkan 2.22.0, Silk.NET.Vulkan.Extensions.KHR/EXT, Silk.NET.Shaderc 2.23.0 + native, GlmSharpStd 0.9.8 |
| `src/PowerPotion.Rendering/Vulkan/VulkanRenderer.cs` | instance/device/surface/swapchain, render pass, pipeline, depth buffer, vertex/index/uniform buffers, 2 frames in flight, resize, offscreen PNG capture, disposal |
| `src/PowerPotion.Rendering/Shaders/cube.vert`, `cube.frag` | our own GLSL; compiled to SPIR-V at runtime with shaderc |
| `src/PowerPotion.Rendering/Shaders/ShaderCompiler.cs` | GLSL → SPIR-V wrapper over Silk.NET.Shaderc |
| `src/PowerPotion.Rendering/Capture/PngWriter.cs` | dependency-free PNG writer (RGBA, zlib scanlines, CRC32) |
| `src/PowerPotion.Rendering/IRendererBridge.cs` | renderer seam shared by `EngineBridge` and `RendererBridge` |
| `src/PowerPotion.Rendering/RendererBridge.cs` | P0 adapter: same public shape as the old Enscape bridge; accepts world/camera/settings and logs them |
| `src/PowerPotion.Host/Rendering/VulkanSurface.cs` | `HwndHost` child HWND + render thread + `IRenderSurface` |
| `src/PowerPotion.Host/Rendering/P0Window.cs` | P0 test window and self-check (resize, capture, pixel assertion) |
| `src/PowerPotion.Host/Program.cs` | `--p0`, `--seconds`, `--resize`; `POWERPOTION_RENDERER=pp` selects `RendererBridge` |
| `src/PowerPotion.Host/MainWindow.xaml.cs` | existing layout uses `VulkanSurface` when the bridge is `RendererBridge`; Enscape path unchanged |

## How to run

Build:

```powershell
dotnet build src\PowerPotion.Host\PowerPotion.Host.csproj -c Release
```

P0 spike (self-checking; exits non-zero on failure):

```powershell
src\PowerPotion.Host\bin\Release\net8.0-windows\PowerPotion.Host.exe `
  --p0 --shot "$env:TEMP\powerpotion_p0.png" --seconds 8 --resize
```

Existing host layout with the standalone renderer:

```powershell
$env:POWERPOTION_RENDERER = "pp"
src\PowerPotion.Host\bin\Release\net8.0-windows\PowerPotion.Host.exe --port <port>
```

The Enscape path remains the default (`POWERPOTION_RENDERER` unset) for A/B.

## Verification

### P0 self-check

- Renderer reaches ready state on the RTX 5060.
- Surface size changes `1264x761 → 944x561`; the swapchain is recreated.
- Capture is `944x561` and contains **20.0%** non-background pixels (the cube).
- Process exits `0` with `P0 clean shutdown`.

### Enscape regression

The existing smoke test still passes **16/16** assertions with the default Enscape bridge after
the seam refactor:

```text
windows visible          : 1 (expected 1)
engine window embedded   : True
renderer shown after scene : True
camera stream log lines  : 10 (expected >= 3, 30 updates each)
world applies            : 2 (expected 2)
world commits            : 2 (expected >= 2)
dispatcher errors        : False
navigation changed view  : True
sky visible (top RGB)    : True (88,111,156)
canvas sky (top RGB)     : True (73,90,121)
canvas ground (bottom)   : True (186,175,167)
walk eye height          : 1.7 (expected 1.7)
fly vertical move        : 2.97 m (expected ~3)
SMOKE TEST PASSED
```

### Standalone path through the existing host

Running the same smoke test with `POWERPOTION_RENDERER=pp` exercises the existing `MainWindow`
layout, protocol, navigation and capture path against `RendererBridge`. **16 of the 19 required
assertions in the smoke test's `ok` expression pass (84%)**; all six captures are produced, world
applies/commits and camera-stream diagnostics are present. The two conditional checks
(`empty world ignored`, `duplicate revision ignored`) were not requested and are not counted.
The capture from the existing layout is checked in at
[`docs/p0-mainwindow-capture.png`](p0-mainwindow-capture.png).

The three failures are expected P0 gaps, not renderer crashes:

- `engine window embedded` / `renderer shown after scene` — the smoke test looks for
  Enscape-specific log strings; P0 logs `VulkanSurface.Ready` instead.
- `sky visible` — P0 renders a dark clear color and a cube, not a sky/ground scene.

### Independence

`--p0` runs with `POWERPOTION_ENSCAPE_DIR=C:\definitely-not-enscape` and still exits `0`.
The P0 host log contains `VulkanSurface`/`P0 bridge` messages and no Enscape load. The
`PowerPotion.Rendering` project has no Enscape package or assembly reference.

## P0 limitations (intentional)

- Hard-coded cube; `WorldSnapshot` geometry/materials/lights are accepted but not uploaded.
- Hard-coded camera; `CameraUpdate` is stored but not applied.
- No sky, shadows, PBR, textures, sections, glass, post-processing or panorama.
- Capture uses the current swapchain extent; the requested `CaptureRequest` resolution is not
  honored yet. `panorama` currently writes a normal frame.
- Shaders are compiled at runtime with shaderc in this P0 snapshot. P1 replaced this with the
  build-time `.spv` pipeline (see `docs/p1-report.md`).
- Vulkan validation layers are not registered on this machine; the renderer supports
  `POWERPOTION_VK_VALIDATION=1` but P1 should install/verify the layer.
- The smoke test is still Enscape-coupled; P7 should make it renderer-agnostic.

## Next (P1)

P1 is complete — see `docs/p1-report.md` for the context/swapchain split, allocator, descriptor
manager, pipeline cache, frame graph, build-time SPIR-V, validation/debug markers and the
resize/leak stress results.

1. ~~Split `VulkanRenderer` into `VulkanContext` / `Swapchain` / `PipelineCache` / `FrameGraph`.~~
2. ~~Build-time shader compilation to `.spv` and embed the artifacts.~~
3. ~~Enable and verify Vulkan validation layers; add GPU markers and device/driver logging.~~
4. ~~Harden swapchain recreation for minimize/restore, DPI changes and multi-monitor.~~
5. Add a headless golden-image test (sphere grid / material chart) as the first P2 seam.
