# P6 — Quality (SSAO, height fog, clouds/wind, culling)

**Status:** implemented (2026-11-10), uncommitted by request. Verified on an NVIDIA GeForce
RTX 5060 with the Khronos validation layer: **0 validation errors, 0 warnings**.

## What changed

### SSAO

- The scene render pass now writes a second color attachment: **linear view depth** (`R32Sfloat`).
  Scene, sky and cube shaders emit `location = 1` depth.
- `post.frag` reconstructs view-space positions from the linear depth and inverse projection,
  derives normals from screen-space derivatives, and samples an 8-point view-space hemisphere.
- Result is applied to the HDR colour before ACES.
- `r_ssao` / `r_ssaoRadius` control strength and world-space radius.

### SSGI + denoise

- A screen-space diffuse bounce gather samples the HDR scene at eight points around the pixel,
  weighted by linear-depth similarity (`r_ssgiFalloff`). The weighted average acts as the denoise.
- `r_ssgi` / `r_ssgiRadius` control the contribution and screen-space radius. The result is added
  to the HDR colour before exposure/ACES.

### Dynamic resolution / upscale path

- `r_renderScale` (0.5–1.0) makes the scene passes render into a smaller `_renderExtent`; the post
  pass samples that target and writes the full swapchain (spatial upscale).
- When upscaling, `post.frag` applies an RCAS-style sharpening kernel to recover edge detail.
- Scene color, linear depth, depth, refraction and scene framebuffers follow the render extent;
  the post output and TAA history remain at swapchain resolution.
- `r_dlss` is the compatibility toggle and enables the 0.75 render-scale path; full FSR 2/3
  temporal integration is still a follow-up.

### Height fog

- Fog parameters (`r_fog`, `r_fogHeight`) are carried in the scene UBO and evaluated in
  `scene.frag`: exponential falloff with height, applied before the HDR output.

### Clouds + wind

- `sky.frag` now raymarches a 5-step cloud layer between 500 m and 1220 m using 3-octave value
  noise, lit by the sun colour.
- `r_cloudDensity`, `r_cloudCirrusAmount`, `r_cloudVariety`, `r_cloudOffsetX/Z` control the layer.
- `g_windStrength` and `g_windDirection` advect the cloud noise over time.

### Frustum culling

- Every draw carries a model bounding sphere (centre + radius).
- `VulkanRenderer` extracts six normalized frustum planes from the camera view-projection and
  skips draws outside the frustum for both opaque and transparent passes.
- `SceneDrawCalls` reflects the culled count.

## Evidence

`tests/p6-scene.ps1`:

| Check | Result |
|---|---|
| Baseline | captured, 5 draw calls |
| SSAO | captured, image changed, mean luma delta **0.52** |
| SSGI + denoise | captured, image changed, mean luma delta **5.82** |
| Height fog | captured, image changed |
| Clouds + wind | captured, image changed |
| Frustum culling (camera looking away) | **5 → 0 draw calls** |
| Dynamic resolution | render **632×380**, capture **1264×761** |
| P6 CVars | `r_ssao`, `r_ssaoRadius`, `r_ssgi`, `r_ssgiRadius`, `r_renderScale`, `g_windStrength`, `g_windDirection` apply |
| Validation | **0 errors / 0 warnings** |
| Exit | `P6 PASSED: SSAO, SSGI, height fog, clouds/wind, culling and render scale verified.` |

Captures: `docs/p6-base.png`, `docs/p6-ssao.png`, `docs/p6-ssgi.png`, `docs/p6-fog.png`,
`docs/p6-clouds.png`, `docs/p6-renderScale.png`.

Regressions (all with validation enabled):

- **P0 PASSED**
- **P1 PASSED** (0 validation errors, no leaks)
- **P2 PASSED**
- **P3 PASSED**
- **P4 PASSED**
- **P5 PASSED** (0 validation errors)

## Not yet implemented (P6 follow-ups)

Tracked with the rest of the deferred work in `docs/NOT-IMPLEMENTED.md`.

- DDGI probe grid and OIDN/custom temporal denoise (SSGI + a depth-weighted gather are in place).
- FSR 2/3 temporal upscaling (the render-scale + spatial upscale path is in place; `r_dlss` is
  the compatibility toggle).
- Ray-traced sun shadows/reflections/AO (`r_hwrt`, `r_rtSunShadows`, `r_restir` remain settings
  placeholders).
- Art modes (hatching/watercolor).
- GPU-driven culling/indirect draws (current culling is CPU-side per draw).
