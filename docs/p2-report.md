# P2 report — scene, materials, textures, PBR

**Date:** 2026-11-10
**Machine:** Windows, NVIDIA GeForce RTX 5060, Vulkan 1.4.341
**Validation:** `VK_LAYER_KHRONOS_validation` — **0 errors, 0 warnings** on the P2 scene run

## Result

P2 is complete. The standalone renderer now consumes a real `WorldSnapshot` instead of the P0
cube:

- **Scene upload** — per-model interleaved vertex buffers (`pos/normal/uv/backside-uv`), 32-bit
  index buffers, material groups, and per-instance matrices.
- **Material buffer** — host-visible storage buffer with 288-byte `MaterialGpu` records:
  diffuse, tint, opacity, emissive, roughness, metallic, specular, bump amount, refraction,
  material type, bump kind, double-sided, solid-glass and cutout flags (white mode is a global
  scene option).
- **Textures** — 4 slots per material (diffuse/bump/roughness/mask), UV transforms, inversion,
  brightness, basic planar/box WCS, sRGB/linear formats, CPU mip chains and anisotropic sampling.
- **PBR** — GGX Cook–Torrance direct lighting, normal mapping (normal and height maps),
  alpha-test/cutout, double-sided pipeline, transparent pipeline with back-to-front sorting,
  simple Reinhard tonemap + gamma.
- **Camera** — engine-space position/quaternion, horizontal/vertical FOV, perspective
  view-projection, aspect updates on resize.
- **Scene updates** — revision-gated, CPU build on a background task, latest-ready scene uploaded
  on the render thread; camera-only frames do not rebuild the scene.
- **Live material edits** — `OverwriteMaterial` updates only the material SSBO entry and draw
  flags; no geometry re-upload.
- **White mode** — `r_whiteBackground` now reaches the scene UBO and overrides albedo.

## Verification

`pwsh -NoProfile -File tests\p2-scene.ps1` builds the host, enables validation when a local
Khronos layer is available, runs `--p2`, and checks for `VK ERROR` in the output.

```text
P2 renderer: NVIDIA GeForce RTX 5060
P2 swapchain: B8G8R8A8Unorm 1264x761 PresentModeFifoKhr hdr=False vsync=True
[vk] scene: building rev 100 asynchronously...
[vk] texture 1: powerpotion_checker.png 64x64 srgb=True
[vk] scene: uploaded rev 100, 5 models, 5 instances, 5 draws, 5 materials, 2 textures
[vk] scene: draw materials=1,2,2,3,4
P2 scene stats: 5 models, 5 instances, 5 draws, 5 materials, 2 textures, 4600 geometry bytes
P2 draw calls: 5, materials=5, textures=2
P2 pixels: nonBg=65.2%, red=2.09%, checkerBlue=3.67%, bright=0.00%
P2 camera-only: captured=True, changed=True, sceneUnchanged=True, frames+=40
[vk] scene: material 3 updated at index 3 in 0.14 ms
P2 pixels after material edit: nonBg=60.4%, green=1.15%, changed=True, updateMs=0.14
P2 white mode: captured=True, light=60.4%
P2 PASSED: geometry, materials, texture and live material update verified.
```

Exit code: `0`.

Captures:

- [`docs/p2-scene.png`](p2-scene.png) — ground, walls, red accent block, blue/white checker texture.
- [`docs/p2-scene-after.png`](p2-scene-after.png) — accent block changed red → green.
- [`docs/p2-scene-camera.png`](p2-scene-camera.png) — camera-only move; no scene rebuild.
- [`docs/p2-scene-white.png`](p2-scene-white.png) — white mode (`r_whiteBackground 1`).
- [`docs/p2-smoke-capture.png`](p2-smoke-capture.png) — the existing smoke-test world rendered by
  the standalone renderer.

The camera-only check reports `sceneUnchanged=True` and `frames+=40`, proving a camera move does
not rebuild or re-upload the scene. The material hot-swap took **0.14 ms** for a color edit and
changed the image without re-uploading geometry or stalling the device. Texture-set changes still
idle the device before descriptor updates; P2.5 can double-buffer materials for that path.

### Existing smoke test

The `POWERPOTION_RENDERER=pp` path still runs the existing `MainWindow` layout and protocol. The
smoke test passes 16/19 required assertions (84%); the three failures remain the expected
Enscape-specific log strings and the missing sky (P3). All six captures are produced, and the
canvas ground is now the rendered ground plane (e.g. `153,152,151`) rather than the clear color.

## Notes and limits

- **Split-sum IBL** is not implemented yet; P2 uses a directional sun plus ambient. P3 adds the
  sky and a sky-driven hemisphere ambient; full prefiltered IBL remains P6.
- **Sun/date/location** is still a simple directional light. `SunUpdate` mode 1 feeds the
  direction; mode 0 date/location will be implemented in P3.
- **Texture formats** — PNG/JPG via StbImageSharp. DDS/KTX2 are deferred; WIC is available for
  additional formats if needed.
- **Mips** are generated on the CPU with a 2×2 box filter; sRGB mip filtering is approximate.
- **WCS** — basic planar/box projection using world position; full Revit WCS semantics and
  per-slot transforms are P2.5/P3 polish.
- **Material updates** write the coherent material SSBO directly (0.14 ms measured for a color
  edit). Texture-set changes still use `DeviceWaitIdle` before descriptor updates; P2.5 can
  double-buffer the material SSBO for fully tear-free non-blocking edits.
- **Transparency** uses per-draw instance distance sorting; P4 adds proper glass/refraction and
  per-triangle sorting.
- No shadows, sky, GI, clouds or post-processing yet — those are P3/P5/P6.

## P2 exit checklist

- [x] Upload `WorldSnapshot` geometry, indices, material groups and instances.
- [x] Material buffer with the PBR parameters and material flags.
- [x] Four texture slots, UV transforms, inversion, brightness, sRGB, mips, aniso.
- [x] GGX Cook–Torrance, normal mapping, alpha-test/cutout, double-sided.
- [x] Engine-space camera and aspect updates.
- [x] Revision-gated async CPU build; no rebuild on camera-only frames.
- [x] `MaterialUpdate` hot-swap without geometry re-upload.
- [x] A synthetic Revit-shaped scene and the existing smoke world both render correctly.

## Next (P3)

1. `SunUpdate` date/location/north-angle and explicit sun vector.
2. CSM sun shadows with PCF/PCSS.
3. Atmosphere (Hosek–Wilkie/Preetham), sun disk, moon, stars.
4. Skybox `.hdr`/`.exr` loading and split-sum IBL.
5. Photometric artificial lights and IES profiles.
