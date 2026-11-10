# P1 report — Vulkan foundation

**Date:** 2026-11-10
**Machine:** Windows, NVIDIA GeForce RTX 5060, driver 32.0.16.1088
**Runtime:** .NET 8.0.33
**Vulkan:** 1.4.341 device; instance requests 1.3
**Validation:** `VK_LAYER_KHRONOS_validation` (local Khronos layer copy) — **0 errors, 0 warnings**

## Result

P1 is complete. The P0 single-file renderer is now split into a real foundation:

| Component | File | What it does |
|---|---|---|
| Vulkan context | `src/PowerPotion.Rendering/Vulkan/VulkanContext.cs` | instance, debug messenger, Win32 surface, physical/logical device, queues, device/feature/extension queries, memory-type lookup, debug names |
| Swapchain | `src/PowerPotion.Rendering/Vulkan/Swapchain.cs` | format/HDR selection, vsync/present-mode selection, image views, recreation, debug names |
| Allocator | `src/PowerPotion.Rendering/Vulkan/VulkanAllocator.cs` | dedicated image allocations, sub-allocated buffer blocks, persistent staging ring, deferred frees, stats |
| Descriptors | `src/PowerPotion.Rendering/Vulkan/DescriptorManager.cs` | per-frame uniform sets, bindless texture layout when `VK_EXT_descriptor_indexing` is available |
| Pipeline cache | `src/PowerPotion.Rendering/Vulkan/PipelineCache.cs` | persistent `VkPipelineCache`, shader-module cache, pipeline-layout cache, `%LOCALAPPDATA%\PowerPotion\pipeline.cache` |
| Frame graph | `src/PowerPotion.Rendering/Vulkan/FrameGraph.cs` | ordered passes, explicit image transitions, transient-image pool |
| Shader build | `tools/ShaderCompiler/` + `PowerPotion.Rendering.csproj` | GLSL → SPIR-V at build time; the renderer embeds `.spv` and has no runtime shaderc dependency |

Additional P1 work:

- **3 frames in flight** (`MaxFramesInFlight = 3`).
- **Build-time shaders**: the `CompileShaders` MSBuild target runs the shader compiler when a `.vert`/`.frag`/`.comp` changes; generated files live in `Shaders/Compiled/` and are embedded.
- **Validation layers**: enabled with `POWERPOTION_VK_VALIDATION=1` (and automatically in Debug builds); a debug-utils messenger reports errors/warnings and counts them.
- **GPU markers**: `opaque pass` debug labels on the command buffer, plus object names for buffers, images, views, swapchain, pipeline, layouts and descriptors.
- **Vsync toggle**: `r_vsync 0/1` through `RendererBridge`, and `VulkanSurface.SetVsync`; present mode switches FIFO ↔ MAILBOX.
- **HDR selection**: `r_hdr 0/1`; prefers `A2B10G10R10_UNORM_PACK32` + HDR10 ST2084, then `R16G16B16A16_SFLOAT` + extended-sRGB-linear, with a logged SDR fallback.
- **Resize/minimize hardening**: zero-extent guard, swapchain recreation coalescing, explicit `DeviceWaitIdle` before teardown, and a P1 stress mode.

## Verification

### P1 stress run

`pwsh -NoProfile -File tests\p1-stress.ps1` runs the host with validation enabled and performs 36
window resizes, repeated vsync toggles, HDR on/off, minimize/restore, and a final capture.

```text
P1 renderer: NVIDIA GeForce RTX 5060
P1 validation: enabled=True
P1 swapchain: B8G8R8A8Unorm 1264x761 PresentModeFifoKhr hdr=False vsync=True
...
P1 minimize/restore...
P1 leak check: liveAllocations 6->6, allocatedBytes 102670336->102670336
P1 capture: ok C:\Users\Vyshak\AppData\Local\Temp\powerpotion_p1_stress.png
P1 stats: frames=314 (+313), swapchainRecreations=68, validationErrors=0,
          validationWarnings=0, allocatedBytes=106684416, liveAllocations=8
P1 PASSED: stable frame loop, resize stress, no validation errors.
```

Exit code: `0`. The capture is checked in at [`docs/p1-stress-capture.png`](p1-stress-capture.png).

The leak check is exact: after an additional 24-resize cycle, `liveAllocations` and
`allocatedBytes` stay unchanged. The final `liveAllocations=8` after capture is the stable P1 set
(3 uniform buffers, vertex, index, depth, capture color, capture depth).

> After P2/P3, the P1 stress script reports 19 live allocations and ~174 MB because the scene,
> sky, shadow and light resources are persistent. The resize/leak check remains exact.

### P0 regression

The P0 self-check still passes after the refactor:

```text
P0 surface size: 1264x761
P0 resize: 1280x800 -> 960x600
P0 surface size after resize: 944x561
P0 capture: ok C:\Users\Vyshak\AppData\Local\Temp\powerpotion_p0.png
P0 image: 944x561, non-background pixels=19.9%
P0 clean shutdown.
P0 PASSED
```

### Shader build

```text
cube.frag -> cube.frag.spv (432 bytes)
cube.vert -> cube.vert.spv (984 bytes)
```

The renderer embeds `PowerPotion.Rendering.Shaders.Compiled.cube.vert.spv` and
`...cube.frag.spv`; `Silk.NET.Shaderc` is no longer referenced by the runtime project.

### Pipeline cache

`%LOCALAPPDATA%\PowerPotion\pipeline.cache` is written on shutdown (14,718 bytes after the P1
stress run), so driver pipeline compilation can be reused across launches.

## Notes and limits

- This desktop exposes no HDR10/linear surface format, so `r_hdr 1` logs the fallback and keeps
  SDR. The selection path is implemented and will pick HDR on a capable display.
- The local validation run used the Khronos layer DLL shipped with Steam CEF because the Vulkan
  SDK is not installed on this machine. CI should install the Vulkan SDK (or register a layer
  manifest) for a reproducible validation gate.
- `FrameGraph` currently schedules the opaque and capture passes; P2 will add depth prepass,
  shadow and alpha passes to the same graph.
- The transient pool keeps capture images until renderer disposal. P2 should add a
  frame-stamped trim for transient resources after resize storms.
- Bindless support is detected and the device feature is enabled; a 1024-entry bindless texture
  layout is created. The P2 material/texture pipeline will use it.
- P1 does not change the P0 cube scene; world geometry/materials/lights remain P2/P3.

## P1 exit checklist

- [x] Swapchain recreation for resize/minimize, HDR format selection, vsync toggle.
- [x] Frame graph with transient resources and explicit barriers; per-frame command buffers.
- [x] 3 frames in flight.
- [x] Allocator with dedicated + sub-allocated memory, staging ring, deferred frees.
- [x] Descriptor manager with per-frame sets and bindless layout when available.
- [x] Pipeline cache and shader-module cache.
- [x] Build-time `.glsl` → `.spv`; embedded shaders.
- [x] Validation layers + debug messenger; GPU markers; device/driver logging.
- [x] Stable frame loop and resize stress with **0 validation errors** and stable live allocations.

## Next (P2)

1. Upload `WorldSnapshot` geometry (positions/normals/UV/backside UV/indices/material groups) through the allocator + staging ring.
2. Material buffer and texture slots; WIC/STB loading; sRGB/mips/samplers.
3. Forward+ PBR (GGX, split-sum IBL, normal mapping, alpha/cutout, double-sided).
4. Revision-gated async uploads and `MaterialUpdate` hot-swap.
5. Golden-image test harness (sphere grid/material chart) on the P1 frame graph.
