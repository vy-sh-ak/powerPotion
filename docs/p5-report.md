# P5 — Post, capture, settings

**Status:** complete (2026-11-10). Verified on an NVIDIA GeForce RTX 5060 with the Khronos
validation layer enabled: **0 validation errors, 0 warnings**.

P5 closes the post/capture/settings slice of `renderer-plan.md`:

- exposure (auto/manual, adaptation speed), bloom, saturation, colour temperature, contrast,
  vignette, chromatic aberration, motion blur, lens flare and the capture safe frame;
- PNG, JPG and EXR capture, at the window size or an explicit resolution;
- six-face equirectangular panorama;
- every command emitted by `HostSettings` mapped by `CvarAdapter`.

## What changed

### Post pipeline

`src/PowerPotion.Rendering/Scene/PostProcessor.cs` + `Shaders/post.vert` / `post.frag`

- The scene and sky now render into an offscreen `_sceneColorImage`; a fullscreen post pass
  writes the final image to the swapchain.
- Post parameters (std140, 64 bytes): exposure, bloom, saturation, contrast, temperature,
  vignette, chromatic aberration, motion blur, safe frame, lens flare, time, aspect, sun screen
  position/strength, auto-exposure flag.
- Effects: 8-tap bloom, radial motion blur, radial chromatic aberration, colour-temperature
  tint, contrast/auto-contrast, vignette, sun lens flare, and a 90% capture safe-frame border.
- Exposure: manual EV (`exp2(Exposure)`) or auto exposure driven by sun/ambient with
  `r_exposureAdaptionSpeed` smoothing (captures use the target immediately).

### Capture

`Capture/CaptureWriter.cs`, `Capture/ExrWriter.cs`, `Capture/PngWriter.cs`

- `.png` (existing writer), `.jpg`/`.jpeg` via WPF/WIC, `.exr` via a minimal uncompressed
  scanline float writer (sRGB→linear 2.2).
- `Capture(path, width, height)` renders at an explicit resolution; `r_captureResolution`
  scales the default window-size capture.
- Captures use the environment-refraction fallback when the requested size differs from the
  swapchain (the screen-colour copy is swapchain-sized).

### Panorama

`VulkanRenderer.CapturePanorama`

- Renders six 90° cube faces through the normal capture path, then stitches an equirectangular
  image on the CPU (direction → cube face → bilinear sample).
- Defaults to 2048×1024 when no resolution is requested.

### Settings / CVars

`RendererSettings` + `CvarAdapter` now cover every command in
`HostSettings.ToCommands()` / `ToPreInitCommands()`: post effects, cloud placeholders,
capture scale and safe frame. Unknown keys still log and ignore.

## Evidence

`tests/p5-scene.ps1` runs the synthetic scene:

| Check | Result |
|---|---|
| Baseline capture | captured, non-background 100% |
| Manual exposure | captured, image changed |
| Post stack (bloom/saturation/temperature/vignette/CA/motion blur/flare/contrast) | captured, image changed |
| Safe-frame border | detected at the frame edge |
| Explicit resolution | captured at 800×450 |
| JPG + EXR | both written |
| Panorama | captured at 1024×512, non-background 100% |
| CVar coverage | all host commands recognised (`unknown=0`) |
| Validation | **0 errors / 0 warnings** |
| Exit | `P5 PASSED: post, formats, resolution, panorama and CVar coverage verified.` |

Captures: `docs/p5-base.png`, `docs/p5-exposure.png`, `docs/p5-post.png`,
`docs/p5-800x450.png`, `docs/p5-panorama.png`.

## Limitations / follow-ups

- TAA is not implemented yet; the post pass is single-frame. A history buffer + jitter is the
  natural next step.
- Auto-contrast is a fixed contrast lift rather than a histogram-driven stretch.
- Auto exposure is a sun/ambient heuristic with smoothing; a luminance histogram readback would
  make it fully scene-adaptive.
- Motion blur is a radial screen-space approximation (no per-object velocity buffer).
- EXR capture writes the tone-mapped LDR frame linearised to float; a true HDR capture path
  belongs with the P6 HDR pipeline.
