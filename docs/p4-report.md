# P4 — Camera modes, section planes, transparency

**Status:** complete (2026-11-10). Verified on an NVIDIA GeForce RTX 5060 with the Khronos
validation layer enabled: **0 validation errors, 0 warnings**.

P4 closes the camera/section/transparency slice of `renderer-plan.md`:

- perspective and orthographic projection, horizontal/vertical FOV, scene-bounds near/far;
- two-point perspective with verticals kept vertical;
- `SectionPlanesUpdate` protocol message + Revit section-box extraction + shader clipping;
- solid glass, water/grass material hooks and per-frame transparent sorting.

## What changed

### Camera

`src/PowerPotion.Rendering/Scene/CameraState.cs`

- `Ortho` now builds a real `Mat4.Ortho` projection from `OrthoLeft/Right/Bottom/Top` (with a
  FOV-derived fallback when the extents are unset).
- `TwoPointPerspective` keeps the projection verticals vertical: the view matrix is rebuilt from
  the horizontal (yaw-only) forward direction and the camera pitch is applied as a vertical
  projection shift (`proj[9] = -tan(pitch) / tan(fovY/2)`), so pitched views do not converge
  vertical edges.
- `SetSceneBounds(min, max)` derives `Near = max(0.05, nearest * 0.5)` and
  `Far = max(Near + 1, farthest * 1.15 + 5)` from the uploaded scene AABB.
- `FrustumCorners` has an orthographic path for the shadow cascades.
- `ViewRotation` records the effective (yaw-only) rotation used by the view and frustum.

`src/PowerPotion.Rendering/Vulkan/VulkanRenderer.cs` calls `SetSceneBounds` after each scene
upload, so near/far track the actual model extents.

### Section planes

Protocol (`src/PowerPotion.Protocol`):

- new `MessageType.SectionPlanesUpdate = 15`;
- `SectionPlanesUpdate { float[] Planes }` — packed `xyzd`, up to 8 planes;
- `MessageCodec` decode + `WriteSectionPlanes` encode.

Revit (`src/PowerPotion.Revit`):

- `SceneExportContext.GetSectionPlanes()` reads the active `View3D` section box
  (`IsSectionBoxActive` / `GetSectionBox()`), transforms its eight corners through the box
  transform and converts them to engine space, then emits six inward planes
  (`dot(n, p) + d >= 0` is kept).
- `LiveSyncService.PushSectionPlanes()` sends the planes after each export and polls at 2 Hz
  (section-box edits do not always raise `DocumentChanged`); the world send also carries the
  initial plane set.

Host (`src/PowerPotion.Host`):

- `HostConnection.SectionPlanesReceived` event + decode switch case;
- `MainWindow` forwards the message to `IRendererBridge.SetSectionPlanes`;
- `VulkanSurface` enqueues the call on the render thread;
- `RendererBridge` stores and forwards the planes.

Renderer:

- `VulkanRenderer.SetSectionPlanes` clamps to 8 planes;
- the scene UBO grows from 448 to **592 bytes** (`vec4 sectionPlanes[8]`, `vec4 sectionParams`);
- `scene.frag` discards fragments with `dot(n, vWorldPos) + d < 0`.

### Glass / transparency

`scene.frag`:

- `isSolidGlass` (`FLAG_SOLID_GLASS`) and water (`MaterialType == 2`) get a Fresnel-weighted
  environment reflection plus a refracted sky tint; solid glass also raises alpha towards the
  silhouettes.
- Water applies an animated ripple normal using the scene-UBO time.
- Grass (`MaterialType == 1`) gets blade-scale albedo variation.
- Alpha/cutout behaviour is unchanged.

`GpuScene`:

- per-model bounds/centres are captured at upload;
- solid glass and water are classified as transparent even when opacity is 1;
- `VulkanRenderer` re-sorts transparent draws far-to-near **per frame** using the current camera
  position and model centres.

## Evidence

`tests/p4-scene.ps1` runs the same synthetic Revit-shaped scene in each mode:

| Check | Result |
|---|---|
| Perspective baseline | captured, non-background 100% |
| Solid glass pane | captured, image changed |
| Orthographic camera | captured, image changed, non-background 100% |
| Two-point perspective | captured, image changed, non-background 100% |
| Section plane `y >= 1 m` | captured, image changed, bottom-band RGB delta **16.4** |
| Validation | **0 errors / 0 warnings** |
| Exit | `P4 PASSED: ortho, two-point, section planes and glass verified.` |

Captures:

- `docs/p4-scene.png` — perspective baseline
- `docs/p4-glass.png` — solid glass pane
- `docs/p4-ortho.png` — orthographic projection
- `docs/p4-twopoint.png` — two-point perspective
- `docs/p4-section.png` — section plane clipping the ground/lower geometry

## Limitations / follow-ups

- Section clipping is applied to scene geometry; the sun shadow pass does not yet consume the
  same planes, so clipped geometry can still contribute to the shadow map. A shadow-pass UBO
  binding is a small follow-up.
- Refraction currently samples the procedural environment rather than a screen-space colour
  copy; the P5 post pipeline is the natural place to add the colour copy and true screen-space
  refraction.
- Revit two-point perspective is detected from a vertical camera up axis
  (`|up.x|` and `|up.z|` below 1e-3); this matches the Revit two-point view convention.
