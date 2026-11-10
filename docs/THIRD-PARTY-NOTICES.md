# Third-party notices

PowerPotion ships its own Vulkan renderer and does **not** include, link to, or redistribute any
Chaos/Enscape binary, shader, texture, or other asset. The historical reverse-engineering notes in
`docs/` are documentation only.

The product uses the following public components:

| Component | Version | License | Use |
|---|---|---|---|
| .NET 8 / WPF | 8.0 | MIT | Host UI and runtime |
| Silk.NET.Vulkan | 2.22.0 | MIT | Vulkan bindings |
| Silk.NET.Vulkan.Extensions.KHR | 2.22.0 | MIT | Swapchain/win32 surface |
| Silk.NET.Vulkan.Extensions.EXT | 2.22.0 | MIT | Debug utils / extension support |
| Silk.NET.Core | (transitive) | MIT | Native interop helpers |
| StbImageSharp | 2.30.15 | Public domain (stb_image) | Image decoding |
| Inno Setup | 6.x (build-time) | Inno Setup license | Installer packaging (not linked into the product) |

The Vulkan renderer, shaders, scene exporter, material/light/sun parsers, protocol and UI are
original PowerPotion code. Techniques implemented from public literature include GGX/Cook–Torrance,
split-sum IBL, cascaded shadow maps, ACES filmic tonemapping, PCF/SSAO/SSGI screen-space techniques,
and a minimal EXR reader/writer.
