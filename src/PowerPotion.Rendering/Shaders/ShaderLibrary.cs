using System.Reflection;

namespace PowerPotion.Rendering.Shaders;

/// <summary>Loads the build-time compiled SPIR-V resources.</summary>
internal static class ShaderLibrary
{
    public static byte[] Load(string name)
    {
        var resourceName = $"PowerPotion.Rendering.Shaders.Compiled.{name}.spv";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded shader '{resourceName}' not found.");
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }
}
