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

- The scene and sky render **linear HDR** into an `R16G16B16A16_SFLOAT` offscreen target; a
  fullscreen post pass applies exposure, **ACES filmic tonemapping**, gamma and grading, then
  writes the swapchain.
- Post parameters (std140, 64 bytes): exposure, bloom, saturation, contrast, temperature,
  vignette, chromatic aberration, motion blur, safe frame, lens flare, time, auto-contrast,
  sun screen position/strength and TAA weight.
- Effects: 8-tap HDR bloom, radial motion blur, radial chromatic aberration, colour-temperature
  tint, content-adaptive 3×3 luminance auto-contrast, vignette, sun lens flare, and a 90%
  capture safe-frame border.
- **TAA**: Halton subpixel jitter on the camera projection, a history image, and a temporal
  blend with an LDR neighbourhood clamp. TAA is disabled for captures and when the safe frame
  is enabled (so the overlay does not ghost).
- Exposure: manual EV (`exp2(Exposure)`) or auto exposure driven by sun/ambient with
  `r_exposureAdaptionSpeed` smoothing (captures use the target immediately).

### Capture

`Capture/CaptureWriter.cs`, `Capture/ExrWriter.cs`, `Capture/PngWriter.cs`

- `.png` (existing writer), `.jpg`/`.jpeg` via WPF/WIC, `.exr` via a minimal uncompressed
  scanline float writer. **EXR captures read back the HDR scene target directly**, so the file
  is true linear HDR; if the device cannot provide a float scene format the LDR frame is
  linearised as a fallback.
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
| JPG + EXR | both written; EXR is true HDR float (11.5 MB at 1264×761) |
| Panorama | captured at 1024×512, non-background 100% |
| ACES tonemap | HDR scene target + ACES reported in the log |
| TAA | history/jitter active reported in the log |
| CVar coverage | all host commands recognised (`unknown=0`) |
| Validation | **0 errors / 0 warnings** |
| Exit | `P5 PASSED: post, formats, resolution, panorama and CVar coverage verified.` |

Captures: `docs/p5-base.png`, `docs/p5-exposure.png`, `docs/p5-post.png`,
`docs/p5-800x450.png`, `docs/p5-panorama.png`.

## Limitations / follow-ups

- Auto exposure is a sun/ambient heuristic with smoothing; a luminance histogram readback would
  make it fully scene-adaptive.
- Motion blur is a radial screen-space approximation (no per-object velocity buffer).
- TAA uses a clamped history blend without motion-vector reprojection; camera motion can show
  minor ghosting on thin geometry.
