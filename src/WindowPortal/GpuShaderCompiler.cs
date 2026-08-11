using System.Runtime.InteropServices;
using System.Text;
using Vortice.Direct3D;

namespace WindowPortal;

internal static class GpuShaderCompiler
{
    private const uint EnableStrictness = 1u << 11;

    internal static byte[] Compile(
        string source,
        string entryPoint,
        string targetProfile)
    {
        var sourceBytes = Encoding.UTF8.GetBytes(source);
        var result = D3DCompile(
            sourceBytes,
            checked((nuint)sourceBytes.Length),
            "PierceView.GpuPortal.hlsl",
            nint.Zero,
            nint.Zero,
            entryPoint,
            targetProfile,
            EnableStrictness,
            0,
            out var shaderPointer,
            out var errorPointer);

        string? diagnostics = null;
        if (errorPointer != nint.Zero)
        {
            using var errors = new Blob(errorPointer);
            diagnostics = ReadBlobText(errors);
        }

        if (result < 0 || shaderPointer == nint.Zero)
        {
            Marshal.ThrowExceptionForHR(result);
            throw new InvalidOperationException(
                $"HLSL 编译未返回字节码：{diagnostics}");
        }

        using var shader = new Blob(shaderPointer);
        var bytecode = new byte[checked((int)(ulong)shader.BufferSize)];
        Marshal.Copy(shader.BufferPointer, bytecode, 0, bytecode.Length);
        return bytecode;
    }

    private static string ReadBlobText(Blob blob)
    {
        var bytes = new byte[checked((int)(ulong)blob.BufferSize)];
        Marshal.Copy(blob.BufferPointer, bytes, 0, bytes.Length);
        return Encoding.UTF8.GetString(bytes).TrimEnd('\0', '\r', '\n');
    }

    [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi)]
    private static extern int D3DCompile(
        byte[] sourceData,
        nuint sourceDataSize,
        string sourceName,
        nint defines,
        nint include,
        string entryPoint,
        string target,
        uint flags1,
        uint flags2,
        out nint code,
        out nint errorMessages);
}
