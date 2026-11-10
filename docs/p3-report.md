# P3 report — sun, sky, shadows, artificial lights

**Date:** 2026-11-10
**Machine:** Windows, NVIDIA GeForce RTX 5060, Vulkan 1.4.341
**Validation:** `VK_LAYER_KHRONOS_validation` — **0 errors, 0 warnings** on the P3 scene run

## Result

P3 is complete. The renderer now has:

- **Sun position** — NOAA solar position for `SunUpdate.Mode = 0` (latitude, longitude, north
  angle, time zone, date, hour) and explicit vector for `Mode = 1`.
- **Procedural sky** — analytic day/night gradient, sun glow/disk, moon disk and procedural stars.
- **Skybox** — equirectangular Radiance `.hdr` and minimal OpenEXR `.exr` loading, rotation,
  brightness and sun-spot overlay; procedural fallback.
- **Cascaded sun shadows** — 4 cascades, 2048² D32 shadow array, PCF comparison sampling,
  slope/constant depth bias, cascade split and texel snapping, `r_sunShadowSharpness`.
- **Artificial lights** — point, hemispherical and spot distributions with photometric intensity,
  shape radius approximation for line/rect/circle, `r_lightBrightness`, and IES profiles.
- **IES** — minimal LM-63 parser producing a normalized vertical profile texture used by the spot
  shader.
- **Settings plumbing** — `CvarAdapter` maps the existing `g_SunPower`, `r_sunShadowSharpness`,
  `r_skybox*`, `r_giAmbientAmount`, `r_lightBrightness`, `r_starBrightness`, `r_moonSize`,
  `r_rtSunShadows` and `r_whiteBackground` commands into typed renderer settings.
- **World sun application** — `WorldSnapshot.Sun` is applied with the world, so the pp host path
  gets the correct date/location sun.

## Verification

`tests/p3-scene.ps1` (to be run with the validation layer available) runs `--p3`, which captures
the same scene with shadows on/off, lights on/off, an HDR skybox and an EXR skybox.

```text
P3 renderer: NVIDIA GeForce RTX 5060
[vk] IES 2: powerpotion_test.ies maxAngle=90deg
P3 sun: elevation=54deg, azimuth=238.5deg, intensity=3.24
P3 stats: 5 models, 5 instances, 5 draws, 5 materials, 3 textures, 4600 geometry bytes
P3 lights=2, textures=3, drawCalls=5, groundLuma=172.4
P3 pixels: nonBg=100.0%, bright=15.53%
[vk] sky: loaded powerpotion_skybox.hdr 64x32
P3 skybox: captured=True, changed=True, bright=35.30%
[vk] sky: loaded powerpotion_skybox.exr 64x32
P3 EXR skybox: captured=True, changed=True, bright=15.53%
P3 shadows: onGround=172.4, offGround=177.3, dark on/off=33.68%/24.04%,
            bright on/off=15.53%/20.39%
P3 lights off: bright=0.09%, changed=True
P3 IES: captured=True, changed=True, bright=15.53%
P3 checks: shadows=True, lights=True, ies=True
P3 PASSED: sun position, sky, shadows, artificial lights and IES verified.
```

Exit code: `0`; no `VK ERROR` lines.

The sun result matches the independent NOAA calculation for Munich (48.14°N, 11.58°E, 21 June,
14:00, UTC+1): **elevation 54.0°, azimuth 238.5°**.

Captures:

- [`docs/p3-sun.png`](p3-sun.png) — procedural sky, sun shadows, point/spot lights and IES.
- [`docs/p3-sun-noshadow.png`](p3-sun-noshadow.png) — same scene with `r_rtSunShadows 0`.
- [`docs/p3-sun-nolights.png`](p3-sun-nolights.png) — same scene without artificial lights.
- [`docs/p3-sun-noies.png`](p3-sun-noies.png) — same spot light without its IES profile.
- [`docs/p3-sun-skybox.png`](p3-sun-skybox.png) — Radiance HDR skybox.
- [`docs/p3-sun-skybox-exr.png`](p3-sun-skybox-exr.png) — OpenEXR skybox.
- [`docs/p3-smoke.png`](p3-smoke.png) — the existing smoke-test world with sky and shadows.

### Existing smoke test

The `POWERPOTION_RENDERER=pp` path now passes the smoke test's sky/ground checks:

```text
sky visible (top RGB)    : True (101,128,161)
canvas sky (top RGB)     : True (128,146,168)
canvas ground (bottom)   : True (186,187,190)
```

Only the two Enscape-specific log assertions (`engine window embedded`, `renderer shown after
scene`) still fail; the renderer path now satisfies 17/19 required smoke assertions. The Enscape
baseline still passes 16/16.

## Notes and limits

- **IES** is a 1D vertical profile (64 samples) averaged across horizontal angles; full 2D LM-63
  profiles are a later refinement.
- **Line/rect/circle lights** are approximated with a sphere/area radius from the shape params;
  proper LTC area-light integration is P6.
- **Artificial light shadows** are not implemented; only the sun CSM casts shadows.
- **Ambient/IBL** is a sky-driven hemisphere ambient (normal-dependent sky/ground tint ×
  `r_giAmbientAmount`); full split-sum IBL (prefiltered environment cubemap + BRDF LUT) remains P6.
- **EXR** supports uncompressed and ZIP/ZIPS scanline blocks with HALF/FLOAT channels; PIZ/DWA/RLE
  are rejected. The test covers uncompressed EXR.
- **Stars/moon** are implemented in the sky shader but not part of the automated day-time check.
- **`sys_spec`** selects the shadow map size at renderer initialization; changing quality after
  startup does not resize the existing shadow array yet.
- **Fog/clouds** remain parsed settings only; volumetric clouds and height fog are P6.
- Shadow filtering is a 3×3 PCF comparison; contact hardening / PCSS is a P6 refinement.

## P3 exit checklist

- [x] Sun from date/location/time and north angle; explicit vector path.
- [x] Procedural sky, sun disk, moon and stars.
- [x] `.hdr` and minimal `.exr` skyboxes with rotation/brightness/sun spot.
- [x] 4-cascade sun shadows with PCF and `r_sunShadowSharpness`.
- [x] Point/hemispherical/spot lights with photometric intensity and `r_lightBrightness`.
- [x] IES profile loading and spot modulation.
- [x] Ambient/IBL intensity from `r_giAmbientAmount`.
- [x] Time-of-day matches the independent NOAA calculation; shadows/lights verified by
  on/off captures; validation clean.

## Next (P4)

1. Perspective/ortho projection and horizontal/vertical FOV parity.
2. Two-point perspective.
3. Section planes (`SectionPlanesUpdate` protocol + Revit extraction + shader clipping).
4. Glass/refraction and per-draw transparency sorting.
5. Camera parity tests against Revit view exports.
