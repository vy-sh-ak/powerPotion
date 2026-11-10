using System.Runtime.InteropServices;
using Silk.NET.Shaderc;

namespace PowerPotion.ShaderCompiler;

// Build-time GLSL -> SPIR-V compiler. The renderer only ever sees the generated .spv
// resources, so shaderc is a build tool, not a runtime dependency.
//
// usage: ShaderCompiler <source-dir> <output-dir>
internal static unsafe class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: ShaderCompiler <source-dir> <output-dir>");
            return 2;
        }

        var sourceDir = Path.GetFullPath(args[0]);
        var outputDir = Path.GetFullPath(args[1]);
        if (!Directory.Exists(sourceDir))
        {
            Console.Error.WriteLine($"source directory not found: {sourceDir}");
            return 2;
        }
        Directory.CreateDirectory(outputDir);

        var shaderc = Shaderc.GetApi();
        var compiler = shaderc.CompilerInitialize();
        var options = shaderc.CompileOptionsInitialize();
        if (compiler == null || options == null)
        {
            Console.Error.WriteLine("shaderc initialization failed.");
            return 3;
        }

        var failures = 0;
        try
        {
            shaderc.CompileOptionsSetSourceLanguage(options, SourceLanguage.Glsl);
            shaderc.CompileOptionsSetTargetEnv(options, TargetEnv.Vulkan, (uint)SpirvVersion.Shaderc13);
            shaderc.CompileOptionsSetOptimizationLevel(options, OptimizationLevel.Performance);

            foreach (var file in Directory.EnumerateFiles(sourceDir, "*.*", SearchOption.AllDirectories))
            {
                var kind = Path.GetExtension(file).ToLowerInvariant() switch
                {
                    ".vert" => ShaderKind.GlslVertexShader,
                    ".frag" => ShaderKind.GlslFragmentShader,
                    ".comp" => ShaderKind.GlslComputeShader,
                    _ => (ShaderKind?)null,
                };
                if (kind == null) continue;

                var source = File.ReadAllText(file);
                var result = shaderc.CompileIntoSpv(
                    compiler,
                    source,
                    (nuint)System.Text.Encoding.UTF8.GetByteCount(source),
                    kind.Value,
                    file,
                    "main",
                    options);

                if (result == null)
                {
                    Console.Error.WriteLine($"FAILED {file}: shaderc returned null");
                    failures++;
                    continue;
                }

                try
                {
                    var status = shaderc.ResultGetCompilationStatus(result);
                    if (status != CompilationStatus.Success)
                    {
                        Console.Error.WriteLine($"FAILED {file}: {status} - {shaderc.ResultGetErrorMessageS(result)}");
                        failures++;
                        continue;
                    }

                    var length = (int)shaderc.ResultGetLength(result);
                    var bytes = new byte[length];
                    Marshal.Copy((IntPtr)shaderc.ResultGetBytes(result), bytes, 0, length);

                    var relative = Path.GetRelativePath(sourceDir, file);
                    var destination = Path.Combine(outputDir, relative + ".spv");
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.WriteAllBytes(destination, bytes);
                    Console.WriteLine($"{relative} -> {Path.GetFileName(destination)} ({length} bytes)");
                }
                finally
                {
                    shaderc.ResultRelease(result);
                }
            }
        }
        finally
        {
            shaderc.CompileOptionsRelease(options);
            shaderc.CompilerRelease(compiler);
        }

        return failures == 0 ? 0 : 1;
    }
}
