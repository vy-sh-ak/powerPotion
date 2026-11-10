# PowerPotion — What is not implemented

This file is the honest, current register of features that are **planned but not implemented**,
**implemented approximately**, or **accepted as settings but have no effect**. It complements
`renderer-plan.md` (§7 acceptance criteria) and the per-phase reports in `docs/`.

Last reviewed: 2026-11-10 (after the P7 source cutover).

---

## 1. Phase status at a glance

| Phase | Scope | Status |
|---|---|---|
| P0 | Standalone Vulkan window, resize, PNG capture | complete |
| P1 | Context/swapchain, allocator, descriptors, frame graph, validation | complete |
| P2 | Scene upload, materials, textures, GGX PBR | complete |
| P3 | Sun/sky/shadows/lights/IES | complete (see §4 for approximations) |
| P4 | Camera modes, section planes, transparency/glass | complete |
| P5 | Post, ACES/TAA, capture formats, panorama, CVar coverage | complete (see §4) |
| P6 | Quality: SSAO/SSGI, fog, clouds, culling, render scale | **partial** — see §2 |
| P7 | Enscape cutover, installer, docs, CI guard | source complete; clean-machine test manual — see §3 |

---

## 2. P6 quality items still deferred

These are the P6 items that are **not implemented**. The renderer uses the listed stand-in
instead.

| Planned item | Current state | Stand-in |
|---|---|---|
| **DDGI / probe-grid global illumination** | not implemented | screen-space GI (`r_ssgi`), single-bounce, no probes, no multi-bounce |
| **OIDN or temporal GI denoiser** | not implemented | fixed 8-tap depth-weighted average in `post.frag` |
| **Full FSR 2 / FSR 3 temporal upscaling** | not implemented | `r_renderScale` (0.5–1.0) spatial upscale + RCAS-style sharpen |
| **Ray-traced sun shadows** | not implemented | raster 4-cascade PCF shadow map |
| **Ray-traced reflections** | not implemented | environment tint + screen-space refraction for glass only |
| **Ray-traced / RTXDI ambient occlusion** | not implemented | SSAO from the linear-depth G-buffer |
| **Art modes (hatching / watercolor)** | not implemented | only "white mode" exists |
| **GPU-driven culling / indirect draws** | not implemented | CPU frustum culling per draw, one `vkCmdDrawIndexed` per material group |
| **Occlusion culling** | not implemented | — |

`r_hwrt`, `r_restir` and `r_dlss` are accepted for settings-panel compatibility but do not enable
any of the above; see §5.

---

## 3. MVP acceptance gaps (`renderer-plan.md` §7)

| Acceptance item | Status | Notes |
|---|---|---|
| Revit view renders with correct meters/axes/winding/normals | done | verified by P2/P3/P4 suites |
| Materials incl. 4 texture slots, cutout, double-sided, glass | done | — |
| Sun/date/location drives sky and shadows | done | — |
| **Explicit sun vector (`g_sunVec`)** | **not implemented** | `CvarAdapter` has no `g_sunVec` case; sun comes from date/location + `g_SunPower` |
| **Point/spot/rect/line lights** | **partial** | point/spot/hemispherical work; rect and line shapes are shaded as point/spot lights — `scene.frag` never reads the shape channel |
| Live camera at ~30–60 Hz, debounced updates | done | `verify-p4-live.ps1` |
| Two-point / ortho cameras, section planes | done | automated signals verified; side-by-side visual parity with Revit is still a manual check |
| Screenshot + panorama at requested path/resolution | done | PNG/JPG/EXR; mono equirect panorama |
| Every settings-panel control changes the render | **mostly** | four controls are inert (§5) |
| **1080p ≥ 60 fps on a mid-range GPU** | **not measured** | no frame-time capture or regression budgets exist |
| No Enscape DLL/file referenced, loaded or shipped | done | `tests/no-enscape.ps1`, runtime module check |
| **Installer + uninstaller on a clean machine** | **manual** | requires a Windows 10/11 VM with Revit 2026 and no Enscape |

### Testing-strategy items from §8 that do not exist

- **Golden reference images** — there is no `tests/reference/` directory and no image-diff
  tolerance checks. Tests assert structural properties (changed/unchanged, coverage percentages,
  draw counts) rather than pixel goldens.
- **Performance budgets** — no per-scene frame-time capture and no per-quality-preset regression
  budgets.
- **`sys_spec` presets** — `sys_spec 0..3` only changes the shadow-map resolution
  (1024 / 2048 / 4096). It does not switch SSAO/SSGI, cloud steps, post quality or texture
  resolution as a real quality preset would.

---

## 4. Implemented, but as approximations

These features exist and are wired up, but are simplified relative to a production renderer.

### Lighting & shading

- **No split-sum IBL / prefiltered environment probe.** Opaque ambient is an analytic
  sky-coloured hemisphere term (`scene.frag`, "Sky-driven hemisphere ambient"). There is no
  specular environment reflection for opaque materials and no roughness-aware prefilter.
- **No bent/occlusion-aware ambient.** Ambient is not modulated by the AO term per light.
- **Rect/line area lights** are shaded as a single point/spot sample with an effective radius;
  there is no area-light integration (LTC) and no soft area shadows.
- **IES profiles** are sampled as a 1D angle lookup in a 2D texture; full photometric webs are
  not supported.

### Shadows

- Cascaded shadow maps with 3×3 PCF and `r_sunShadowSharpness`. No contact shadows, no
  ray-traced shadows, no coloured/translucent shadow casting.

### Global illumination / AO

- **SSAO** is an 8-sample view-space hemisphere gather from the linear-depth attachment; no
  bilateral blur pass, no temporal accumulation, no multi-scale AO.
- **SSGI** is a single-bounce 8-tap screen-space gather with a depth-weighted average; it cannot
  see off-screen geometry and has no multi-bounce or temporal accumulation.

### Reflections / glass / water / foliage

- **Glass** uses Fresnel + a screen-space refraction sample when the frame provides a
  scene-colour copy, otherwise an environment tint. No true refraction, no dispersion, no
  ray-traced reflection.
- **Water** is a small animated ripple normal hook; no planar reflections, no depth-based
  absorption, no caustics.
- **Grass/foliage** is a blade-scale shading variation hook; no alpha-tested cards, no wind
  animation on geometry, no translucency.

### Atmosphere

- **Clouds** are a 5-step raymarch through 3-octave value noise between 500 m and 1220 m. No
  volumetric self-shadowing, no multiple scattering, no temporal reprojection; `r_cloudContrails`
  has no effect.
- **Fog** is height fog only. No volumetric fog, god rays, or distance-based aerial perspective
  tied to the sky.
- **Sky** is procedural (sun/moon/stars/clouds) or an equirectangular `.hdr`/`.exr` skybox.

### Post-processing

- **TAA** uses Halton subpixel jitter plus a clamped history blend. There is **no motion-vector
  reprojection**, so fast camera motion or thin geometry can ghost. TAA is disabled for captures
  and when the safe frame is shown.
- **Motion blur** is a radial screen-space approximation; there is no per-object velocity buffer.
- **Bloom** is an 8-tap screen-space blur, not a mip-chain/physically based bloom.
- **Lens flare** is a single radial falloff around the sun's screen position.
- **Auto exposure** is a sun/ambient heuristic with `r_exposureAdaptionSpeed` smoothing; it does
  not read a luminance histogram.
- **Auto contrast** stretches a 3×3 luminance grid min/max; it is not a histogram equalisation.
- **Chromatic aberration** is a radial channel split; no spectral model.

### Capture

- **EXR** is an uncompressed scanline float writer. No ZIP/PIZ compression, no deep/multi-part
  EXR, no arbitrary channel sets.
- **Panorama** is mono equirectangular only. No stereo/cubemap/stereographic output.
- **No video capture** (`rec_*` CVars are not implemented).

### Upscaling

- Spatial render-scale + RCAS-style sharpening only. No FSR 1/2/3, no DLSS, no XeSS, no
  temporal upscaler with jittered history.

---

## 5. Settings that are accepted but have no effect

These commands are parsed by `CvarAdapter` and stored in `RendererSettings` so the existing
settings UI and CVar surface keep working, but they currently change nothing:

| Command | Field | Status |
|---|---|---|
| `r_hwrt` | `HardwareRayTracing` | inert — no ray tracing implemented |
| `r_restir` | `RtArtificialLights` | inert — no ReSTIR / ray-traced artificial lights |
| `r_cloudContrails` | `CloudContrails` | inert — contrails are not rendered |
| `r_dlss` | `Dlss` | not DLSS; it only selects the 0.75 render-scale path |

Note: `r_rtSunShadows` **is** honoured, but it toggles the **raster** cascade shadow map despite
the name.

---

## 6. Explicit non-goals (by design)

From `renderer-plan.md` §7 — these are deliberately out of scope, not backlog:

- Bit-identical visual parity with any commercial renderer.
- VR / HMD support.
- Accounts, login, licensing, telemetry, cloud/asset upload.
- Proprietary upscalers/denoisers (DLSS, NRD/SIGMA), CUDA, NoesisGUI, SpaceMouse SDK.

---

## 7. How to check the current state

```powershell
pwsh -File tests/no-enscape.ps1     # no Enscape dependency in product sources
pwsh -File tests/p0-spike.ps1       # window, resize, capture
pwsh -File tests/p1-stress.ps1      # frame loop, resize stress, leaks, validation
pwsh -File tests/p2-scene.ps1       # geometry, materials, textures
pwsh -File tests/p3-scene.ps1       # sun, sky, shadows, lights, IES
pwsh -File tests/p4-scene.ps1       # camera modes, sections, glass
pwsh -File tests/p5-scene.ps1       # post, ACES/TAA, formats, panorama, CVars
pwsh -File tests/p6-scene.ps1       # SSAO, SSGI, fog, clouds, culling, render scale
pwsh -File tests/verify-p4-live.ps1 # live Revit session (requires Revit 2026)
```

The suites assert structural behaviour and validation cleanliness; they are **not** golden-image
or performance tests (see §3).

---

## 8. Suggested order if this list is worked down

1. `g_sunVec` explicit sun vector (small, MVP checkbox).
2. Rect/line area lights — read the shape channel and use an LTC/representative-point model.
3. Real `sys_spec` presets (SSAO/SSGI/cloud steps/post quality/shadow size).
4. Frame-time capture + per-preset budgets; then the 1080p ≥ 60 fps claim.
5. Golden reference images under `tests/reference/` with a diff threshold.
6. Motion-vector TAA and a temporal denoiser for SSGI.
7. FSR 2/3 temporal upscaling.
8. DDGI probes, then optional ray-traced shadows/reflections.
9. Clean-machine installer acceptance on a Windows VM without Enscape.
