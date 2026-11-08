using ComputeSharp;
using ComputeSharp.Descriptors;
using ComputeSharp.Interop;
using System.Runtime.InteropServices;

namespace Botamochi.Gpu;

// ComputeSharp 3.1 exposes the shader descriptor ABI for offline-compiled DXIL.
[ThreadGroupSize(32, 1, 1)]
internal readonly struct PackWave(ReadOnlyBuffer<uint> source, ReadWriteBuffer<uint> target)
    : IComputeShader, IComputeShaderDescriptor<PackWave>
{
    private readonly ReadOnlyBuffer<uint> _source = source;
    private readonly ReadWriteBuffer<uint> _target = target;
    private static readonly byte[] Bytes = ReadBytecode();
    private static readonly ResourceDescriptorRange[] Ranges =
    [
        new(ResourceDescriptorRangeType.ShaderResourceView, 0),
        new(ResourceDescriptorRangeType.UnorderedAccessView, 0)
    ];

    private static byte[] ReadBytecode()
    {
        using var file = typeof(PackWave).Assembly.GetManifestResourceStream("Botamochi.PackWave.dxil");
        if (file == null) return [];
        var bytes = new byte[file.Length];
        file.ReadExactly(bytes);
        return bytes;
    }

    public static bool Available => Bytes.Length > 0;
    public static int ThreadsX => 32;
    public static int ThreadsY => 1;
    public static int ThreadsZ => 1;
    public static int ConstantBufferSize => 8;
    public static bool IsStaticSamplerRequired => false;
    public static ReadOnlyMemory<ResourceDescriptorRange> ResourceDescriptorRanges => Ranges;
    public static string HlslSource => throw new NotSupportedException("This shader is precompiled.");
    public static ReadOnlyMemory<byte> HlslBytecode => Bytes;

    public static void LoadConstantBuffer<TLoader>(in PackWave shader, ref TLoader loader, int x, int y, int z)
        where TLoader : struct, IConstantBufferLoader
    {
        ReadOnlySpan<int> dimensions = [x, y];
        loader.LoadConstantBuffer(MemoryMarshal.AsBytes(dimensions));
    }

    public static void LoadGraphicsResources<TLoader>(in PackWave shader, ref TLoader loader)
        where TLoader : struct, IGraphicsResourceLoader
    {
        loader.LoadGraphicsResource(shader._source, 0);
        loader.LoadGraphicsResource(shader._target, 1);
    }

    public void Execute() => throw new NotSupportedException("GPU entry point.");
}
