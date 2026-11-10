using System.Runtime.InteropServices;
using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;

namespace PowerPotion.Rendering.Vulkan;

/// <summary>
/// Owns the Vulkan instance, debug messenger, Win32 surface, physical/logical device and queues.
/// This is the P1 split of the P0 walking skeleton: the renderer no longer talks to the loader
/// or device-creation APIs directly.
/// </summary>
public sealed unsafe class VulkanContext : IDisposable
{
    private const uint Vulkan13 = (1u << 22) | (3u << 12);

    private Vk _vk = null!;
    private Instance _instance;
    private PhysicalDevice _physicalDevice;
    private Device _device;
    private Queue _graphicsQueue;
    private Queue _presentQueue;
    private uint _graphicsFamily;
    private uint _presentFamily;
    private SurfaceKHR _surface;
    private KhrSurface _khrSurface = null!;
    private KhrWin32Surface _khrWin32Surface = null!;
    private KhrSwapchain _khrSwapchain = null!;
    private ExtDebugUtils? _debugUtils;
    private DebugUtilsMessengerEXT _debugMessenger;
    private DebugUtilsMessengerCallbackFunctionEXT? _debugCallback;
    private bool _disposed;
    private bool _debugUtilsEnabled;
    private bool _validationLayerEnabled;
    private int _validationErrors;
    private int _validationWarnings;

    public Vk Vk => _vk;
    public Instance Instance => _instance;
    public PhysicalDevice PhysicalDevice => _physicalDevice;
    public Device Device => _device;
    public Queue GraphicsQueue => _graphicsQueue;
    public Queue PresentQueue => _presentQueue;
    public uint GraphicsFamily => _graphicsFamily;
    public uint PresentFamily => _presentFamily;
    public SurfaceKHR Surface => _surface;
    public KhrSurface KhrSurface => _khrSurface;
    public KhrSwapchain KhrSwapchain => _khrSwapchain;
    public ExtDebugUtils? DebugUtils => _debugUtils;
    public bool ValidationEnabled => _validationLayerEnabled;
    public bool DebugUtilsEnabled => _debugMessenger.Handle != 0;
    public int ValidationErrorCount => _validationErrors;
    public int ValidationWarningCount => _validationWarnings;
    public string DeviceName { get; private set; } = "";
    public string DriverInfo { get; private set; } = "";
    public uint ApiVersion { get; private set; }
    public PhysicalDeviceType DeviceType { get; private set; }
    public PhysicalDeviceProperties Properties { get; private set; }
    public PhysicalDeviceMemoryProperties MemoryProperties { get; private set; }
    public bool SupportsBindless { get; private set; }
    public bool SupportsDynamicRendering { get; private set; }
    public bool SupportsSynchronization2 { get; private set; }
    public bool SamplerAnisotropyEnabled { get; private set; }

    public event Action<string>? Log;

    private void Write(string message) => Log?.Invoke(message);

    public void Initialize(IntPtr hwnd, bool requestValidation)
    {
        if (hwnd == IntPtr.Zero) throw new ArgumentException("HWND is null.", nameof(hwnd));

        _vk = Vk.GetApi();
        CreateInstance(requestValidation);
        CreateSurface(hwnd);
        PickPhysicalDevice();
        QueryFeatures();
        CreateDevice();
        CreateDebugMessenger();

        MemoryProperties = _vk.GetPhysicalDeviceMemoryProperties(_physicalDevice);

        Write($"Vulkan context: {DeviceName} | API {VersionString(ApiVersion)} | type {DeviceType}");
        Write($"Queues: graphics={_graphicsFamily}, present={_presentFamily}; " +
              $"bindless={SupportsBindless}, dynamicRendering={SupportsDynamicRendering}, sync2={SupportsSynchronization2}");
        Write($"Validation layer: {(ValidationEnabled ? "enabled" : "disabled")}; " +
              $"debug utils: {(DebugUtilsEnabled ? "enabled" : "disabled")}");
    }

    private void CreateInstance(bool requestValidation)
    {
        var appName = SilkMarshal.StringToPtr("PowerPotion");
        var engineName = SilkMarshal.StringToPtr("PowerPotion.Rendering");
        var extensions = new List<string> { "VK_KHR_surface", "VK_KHR_win32_surface" };
        var layers = new List<string>();

        // Debug utils is useful for validation and for GPU markers even without validation.
        if (HasInstanceExtension("VK_EXT_debug_utils"))
        {
            extensions.Add("VK_EXT_debug_utils");
            _debugUtilsEnabled = true;
        }

        if (requestValidation)
        {
            if (HasInstanceLayer("VK_LAYER_KHRONOS_validation"))
            {
                layers.Add("VK_LAYER_KHRONOS_validation");
                _validationLayerEnabled = true;
                Write("Validation layer requested and available.");
            }
            else
            {
                Write("Validation layer requested but VK_LAYER_KHRONOS_validation is not available.");
            }
        }

        var extPtr = SilkMarshal.StringArrayToPtr(extensions);
        var layerPtr = layers.Count > 0 ? SilkMarshal.StringArrayToPtr(layers) : IntPtr.Zero;
        try
        {
            var appInfo = new ApplicationInfo
            {
                SType = StructureType.ApplicationInfo,
                PApplicationName = (byte*)appName,
                ApplicationVersion = 1,
                PEngineName = (byte*)engineName,
                EngineVersion = 1,
                ApiVersion = Vulkan13,
            };

            var createInfo = new InstanceCreateInfo
            {
                SType = StructureType.InstanceCreateInfo,
                PApplicationInfo = &appInfo,
                EnabledExtensionCount = (uint)extensions.Count,
                PpEnabledExtensionNames = (byte**)extPtr,
                EnabledLayerCount = (uint)layers.Count,
                PpEnabledLayerNames = layerPtr == IntPtr.Zero ? null : (byte**)layerPtr,
            };

            var result = _vk.CreateInstance(&createInfo, null, out _instance);
            if (result != Result.Success && layers.Count > 0)
            {
                Write($"vkCreateInstance with validation failed ({result}); retrying without layers.");
                layers.Clear();
                _validationLayerEnabled = false;
                createInfo.EnabledLayerCount = 0;
                createInfo.PpEnabledLayerNames = null;
                result = _vk.CreateInstance(&createInfo, null, out _instance);
            }
            if (result != Result.Success)
                throw new InvalidOperationException($"vkCreateInstance failed: {result}");
        }
        finally
        {
            SilkMarshal.FreeString(appName);
            SilkMarshal.FreeString(engineName);
            SilkMarshal.Free(extPtr);
            if (layerPtr != IntPtr.Zero) SilkMarshal.Free(layerPtr);
        }
    }

    private static bool HasInstanceLayer(string name)
    {
        var vk = Vk.GetApi();
        uint count = 0;
        vk.EnumerateInstanceLayerProperties(ref count, null);
        var props = new LayerProperties[count];
        fixed (LayerProperties* p = props)
            vk.EnumerateInstanceLayerProperties(ref count, p);
        for (int i = 0; i < props.Length; i++)
        {
            var p = props[i];
            if (Marshal.PtrToStringAnsi((IntPtr)p.LayerName) == name) return true;
        }
        return false;
    }

    private static bool HasInstanceExtension(string name)
    {
        var vk = Vk.GetApi();
        uint count = 0;
        vk.EnumerateInstanceExtensionProperties((byte*)IntPtr.Zero, ref count, null);
        var props = new ExtensionProperties[count];
        fixed (ExtensionProperties* p = props)
            vk.EnumerateInstanceExtensionProperties((byte*)IntPtr.Zero, ref count, p);
        for (int i = 0; i < props.Length; i++)
        {
            var p = props[i];
            if (Marshal.PtrToStringAnsi((IntPtr)p.ExtensionName) == name) return true;
        }
        return false;
    }

    private void CreateSurface(IntPtr hwnd)
    {
        if (!_vk.TryGetInstanceExtension(_instance, out _khrSurface))
            throw new InvalidOperationException("VK_KHR_surface is not available.");
        if (!_vk.TryGetInstanceExtension(_instance, out _khrWin32Surface))
            throw new InvalidOperationException("VK_KHR_win32_surface is not available.");

        var createInfo = new Win32SurfaceCreateInfoKHR
        {
            SType = StructureType.Win32SurfaceCreateInfoKhr,
            Hinstance = GetModuleHandle(null),
            Hwnd = hwnd,
        };

        var result = _khrWin32Surface.CreateWin32Surface(_instance, &createInfo, null, out _surface);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateWin32SurfaceKHR failed: {result}");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    private void PickPhysicalDevice()
    {
        uint count = 0;
        _vk.EnumeratePhysicalDevices(_instance, ref count, null);
        if (count == 0) throw new InvalidOperationException("No Vulkan physical devices found.");

        var devices = new PhysicalDevice[count];
        fixed (PhysicalDevice* p = devices)
            _vk.EnumeratePhysicalDevices(_instance, ref count, p);

        foreach (var device in devices)
        {
            var props = _vk.GetPhysicalDeviceProperties(device);
            var name = Marshal.PtrToStringAnsi((IntPtr)props.DeviceName) ?? "unknown";
            if (!FindQueueFamilies(device, out var graphics, out var present)) continue;

            _physicalDevice = device;
            _graphicsFamily = graphics;
            _presentFamily = present;
            DeviceName = name;
            ApiVersion = props.ApiVersion;
            DeviceType = props.DeviceType;
            Properties = props;
            DriverInfo = $"Vulkan {VersionString(props.ApiVersion)}, driver 0x{props.DriverVersion:X}";
            return;
        }

        throw new InvalidOperationException("No Vulkan device supports both graphics and presentation.");
    }

    private bool FindQueueFamilies(PhysicalDevice device, out uint graphics, out uint present)
    {
        graphics = uint.MaxValue;
        present = uint.MaxValue;

        uint count = 0;
        _vk.GetPhysicalDeviceQueueFamilyProperties(device, ref count, null);
        var families = new QueueFamilyProperties[count];
        fixed (QueueFamilyProperties* p = families)
            _vk.GetPhysicalDeviceQueueFamilyProperties(device, ref count, p);

        for (uint i = 0; i < count; i++)
        {
            if ((families[i].QueueFlags & QueueFlags.GraphicsBit) != 0)
                graphics = i;

            _khrSurface.GetPhysicalDeviceSurfaceSupport(device, i, _surface, out Bool32 supported);
            if (supported)
                present = i;

            if (graphics != uint.MaxValue && present != uint.MaxValue && graphics == present)
                return true;
        }

        return graphics != uint.MaxValue && present != uint.MaxValue;
    }

    private void CreateDevice()
    {
        var unique = new HashSet<uint> { _graphicsFamily, _presentFamily };
        var queueInfos = new DeviceQueueCreateInfo[unique.Count];
        float priority = 1.0f;
        int index = 0;
        foreach (var family in unique)
        {
            queueInfos[index++] = new DeviceQueueCreateInfo
            {
                SType = StructureType.DeviceQueueCreateInfo,
                QueueFamilyIndex = family,
                QueueCount = 1,
                PQueuePriorities = &priority,
            };
        }

        var extensions = new List<string> { "VK_KHR_swapchain" };
        if (SupportsBindless) extensions.Add("VK_EXT_descriptor_indexing");
        var extPtr = SilkMarshal.StringArrayToPtr(extensions);

        var supportedFeatures = _vk.GetPhysicalDeviceFeatures(_physicalDevice);
        SamplerAnisotropyEnabled = supportedFeatures.SamplerAnisotropy == true;
        var enabledFeatures = new PhysicalDeviceFeatures
        {
            SamplerAnisotropy = supportedFeatures.SamplerAnisotropy,
        };

        try
        {
            fixed (DeviceQueueCreateInfo* q = queueInfos)
            {
                var indexing = new PhysicalDeviceDescriptorIndexingFeatures
                {
                    SType = StructureType.PhysicalDeviceDescriptorIndexingFeatures,
                    DescriptorBindingPartiallyBound = SupportsBindless,
                    RuntimeDescriptorArray = SupportsBindless,
                };
                var features2 = new PhysicalDeviceFeatures2
                {
                    SType = StructureType.PhysicalDeviceFeatures2,
                    Features = enabledFeatures,
                    PNext = SupportsBindless ? &indexing : null,
                };

                var createInfo = new DeviceCreateInfo
                {
                    SType = StructureType.DeviceCreateInfo,
                    PNext = SupportsBindless ? &features2 : null,
                    QueueCreateInfoCount = (uint)queueInfos.Length,
                    PQueueCreateInfos = q,
                    EnabledExtensionCount = (uint)extensions.Count,
                    PpEnabledExtensionNames = (byte**)extPtr,
                };

                var result = _vk.CreateDevice(_physicalDevice, &createInfo, null, out _device);
                if (result != Result.Success)
                    throw new InvalidOperationException($"vkCreateDevice failed: {result}");
            }
        }
        finally
        {
            SilkMarshal.Free(extPtr);
        }

        _vk.GetDeviceQueue(_device, _graphicsFamily, 0, out _graphicsQueue);
        _vk.GetDeviceQueue(_device, _presentFamily, 0, out _presentQueue);

        if (!_vk.TryGetDeviceExtension(_instance, _device, out _khrSwapchain))
            throw new InvalidOperationException("VK_KHR_swapchain is not available.");
    }

    private void QueryFeatures()
    {
        uint extensionCount = 0;
        _vk.EnumerateDeviceExtensionProperties(_physicalDevice, (byte*)IntPtr.Zero, ref extensionCount, null);
        var extensions = new ExtensionProperties[extensionCount];
        fixed (ExtensionProperties* p = extensions)
            _vk.EnumerateDeviceExtensionProperties(_physicalDevice, (byte*)IntPtr.Zero, ref extensionCount, p);

        var names = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < extensions.Length; i++)
        {
            var p = extensions[i];
            names.Add(Marshal.PtrToStringAnsi((IntPtr)p.ExtensionName) ?? "");
        }

        SupportsBindless = names.Contains("VK_EXT_descriptor_indexing");
        SupportsDynamicRendering = names.Contains("VK_KHR_dynamic_rendering");
        SupportsSynchronization2 = names.Contains("VK_KHR_synchronization2");

        if (SupportsBindless)
        {
            var features = new PhysicalDeviceDescriptorIndexingFeatures
            {
                SType = StructureType.PhysicalDeviceDescriptorIndexingFeatures,
            };
            var features2 = new PhysicalDeviceFeatures2
            {
                SType = StructureType.PhysicalDeviceFeatures2,
                PNext = &features,
            };
            _vk.GetPhysicalDeviceFeatures2(_physicalDevice, &features2);
            SupportsBindless = features.DescriptorBindingPartiallyBound == true &&
                               features.RuntimeDescriptorArray == true;
        }
    }

    private void CreateDebugMessenger()
    {
        if (!_debugUtilsEnabled || !_vk.TryGetInstanceExtension(_instance, out ExtDebugUtils debugUtils))
        {
            Write("VK_EXT_debug_utils is not available; debug markers are disabled.");
            return;
        }

        _debugUtils = debugUtils;
        _debugCallback = DebugCallback;
        var createInfo = new DebugUtilsMessengerCreateInfoEXT
        {
            SType = StructureType.DebugUtilsMessengerCreateInfoExt,
            MessageSeverity = DebugUtilsMessageSeverityFlagsEXT.WarningBitExt |
                              DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt,
            MessageType = DebugUtilsMessageTypeFlagsEXT.GeneralBitExt |
                          DebugUtilsMessageTypeFlagsEXT.ValidationBitExt |
                          DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt,
            PfnUserCallback = _debugCallback,
        };

        var result = debugUtils.CreateDebugUtilsMessenger(_instance, &createInfo, null, out _debugMessenger);
        if (result != Result.Success)
        {
            Write($"vkCreateDebugUtilsMessengerEXT failed: {result}");
            _debugMessenger = default;
        }
    }

    private uint DebugCallback(
        DebugUtilsMessageSeverityFlagsEXT severity,
        DebugUtilsMessageTypeFlagsEXT types,
        DebugUtilsMessengerCallbackDataEXT* data,
        void* userData)
    {
        var message = data != null
            ? Marshal.PtrToStringAnsi((IntPtr)data->PMessage) ?? ""
            : "";

        if ((severity & DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt) != 0)
        {
            Interlocked.Increment(ref _validationErrors);
            Write("VK ERROR: " + message);
        }
        else if ((severity & DebugUtilsMessageSeverityFlagsEXT.WarningBitExt) != 0)
        {
            Interlocked.Increment(ref _validationWarnings);
            Write("VK WARN: " + message);
        }

        return 0;
    }

    public uint FindMemoryType(uint typeFilter, MemoryPropertyFlags properties)
    {
        for (uint i = 0; i < MemoryProperties.MemoryTypeCount; i++)
        {
            if ((typeFilter & (1u << (int)i)) != 0 &&
                (MemoryProperties.MemoryTypes[(int)i].PropertyFlags & properties) == properties)
                return i;
        }
        throw new InvalidOperationException($"No memory type for flags {properties}.");
    }

    public void BeginLabel(CommandBuffer commandBuffer, string name)
    {
        if (_debugUtils == null) return;
        var ptr = SilkMarshal.StringToPtr(name);
        try
        {
            var label = new DebugUtilsLabelEXT
            {
                SType = StructureType.DebugUtilsLabelExt,
                PLabelName = (byte*)ptr,
            };
            _debugUtils.CmdBeginDebugUtilsLabel(commandBuffer, &label);
        }
        finally
        {
            SilkMarshal.FreeString(ptr);
        }
    }

    public void EndLabel(CommandBuffer commandBuffer)
    {
        _debugUtils?.CmdEndDebugUtilsLabel(commandBuffer);
    }

    public void SetObjectName(ulong handle, ObjectType type, string name)
    {
        if (_debugUtils == null) return;
        var ptr = SilkMarshal.StringToPtr(name);
        try
        {
            var info = new DebugUtilsObjectNameInfoEXT
            {
                SType = StructureType.DebugUtilsObjectNameInfoExt,
                ObjectType = type,
                ObjectHandle = handle,
                PObjectName = (byte*)ptr,
            };
            _debugUtils.SetDebugUtilsObjectName(_device, &info);
        }
        finally
        {
            SilkMarshal.FreeString(ptr);
        }
    }

    public static string VersionString(uint version) =>
        $"{version >> 22}.{(version >> 12) & 0x3FF}.{version & 0xFFF}";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_device.Handle != 0)
            {
                _vk.DeviceWaitIdle(_device);
                if (_debugMessenger.Handle != 0 && _debugUtils != null)
                    _debugUtils.DestroyDebugUtilsMessenger(_instance, _debugMessenger, null);
                _vk.DestroyDevice(_device, null);
            }
            if (_surface.Handle != 0) _khrSurface.DestroySurface(_instance, _surface, null);
            if (_instance.Handle != 0) _vk.DestroyInstance(_instance, null);
            _vk.Dispose();
        }
        catch
        {
            // Best-effort teardown.
        }
    }
}
