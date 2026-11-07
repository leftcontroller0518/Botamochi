using System.Runtime.InteropServices;
using ComputeSharp;

namespace Botamochi.Gpu;

/// <summary>
/// GPU上でRGBAフレームに対するリアルタイム・ポストプロセッシング（色調補正・ガンマ・輝度）を超並列実行するCompute Shader
/// </summary>
[ThreadGroupSize(DefaultThreadGroupSizes.X)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct ColorGradingComputeKernel : IComputeShader
{
    private readonly ReadOnlyBuffer<Float4> _sourceBuffer;
    private readonly ReadWriteBuffer<Float4> _destinationBuffer;
    private readonly float _brightness;
    private readonly float _contrast;
    private readonly int _totalPixels;

    public ColorGradingComputeKernel(
        ReadOnlyBuffer<Float4> sourceBuffer,
        ReadWriteBuffer<Float4> destinationBuffer,
        float brightness,
        float contrast,
        int totalPixels)
    {
        _sourceBuffer = sourceBuffer;
        _destinationBuffer = destinationBuffer;
        _brightness = brightness;
        _contrast = contrast;
        _totalPixels = totalPixels;
    }

    public void Execute()
    {
        int idx = ThreadIds.X;
        if (idx >= _totalPixels) return;

        Float4 pixel = _sourceBuffer[idx];
        Float3 rgb = (pixel.RGB - 0.5f) * _contrast + 0.5f + _brightness;
        rgb = Hlsl.Saturate(rgb);

        _destinationBuffer[idx] = new Float4(rgb, pixel.A);
    }
}

/// <summary>
/// GPUコンピュート処理のディスパッチャ (ComputeSharp / DirectX 12)
/// </summary>
public sealed class GpuComputePipeline : IDisposable
{
    private readonly GraphicsDevice _device;
    private readonly int _width;
    private readonly int _height;
    private readonly int _totalPixels;
    private readonly ReadOnlyBuffer<Float4> _sourceBuffer;
    private readonly ReadWriteBuffer<Float4> _destinationBuffer;
    private readonly Float4[] _cpuUploadArray;
    private readonly Float4[] _cpuDownloadArray;

    public GpuComputePipeline(int width, int height)
    {
        _width = width;
        _height = height;
        _totalPixels = width * height;
        _device = GraphicsDevice.GetDefault();

        _cpuUploadArray = new Float4[_totalPixels];
        _cpuDownloadArray = new Float4[_totalPixels];

        _sourceBuffer = _device.AllocateReadOnlyBuffer<Float4>(_totalPixels);
        _destinationBuffer = _device.AllocateReadWriteBuffer<Float4>(_totalPixels);
    }

    public void ExecuteColorGrading(ReadOnlySpan<byte> sourceRgba, Span<byte> destinationRgba, float brightness = 0.05f, float contrast = 1.1f)
    {
        // 1. RGBA8 -> Float4 高速正規化
        for (int i = 0; i < _totalPixels; i++)
        {
            int baseIdx = i * 4;
            _cpuUploadArray[i] = new Float4(
                sourceRgba[baseIdx] / 255.0f,
                sourceRgba[baseIdx + 1] / 255.0f,
                sourceRgba[baseIdx + 2] / 255.0f,
                sourceRgba[baseIdx + 3] / 255.0f
            );
        }

        // 2. GPU VRAMへアップロード
        _sourceBuffer.CopyFrom(_cpuUploadArray);

        // 3. Compute Shaderディスパッチ
        _device.For(_totalPixels, new ColorGradingComputeKernel(
            _sourceBuffer,
            _destinationBuffer,
            brightness,
            contrast,
            _totalPixels
        ));

        // 4. GPUからリードバック
        _destinationBuffer.CopyTo(_cpuDownloadArray);

        // 5. Float4 -> RGBA8 量子化
        for (int i = 0; i < _totalPixels; i++)
        {
            int baseIdx = i * 4;
            Float4 p = _cpuDownloadArray[i];
            destinationRgba[baseIdx] = (byte)(p.X * 255.0f + 0.5f);
            destinationRgba[baseIdx + 1] = (byte)(p.Y * 255.0f + 0.5f);
            destinationRgba[baseIdx + 2] = (byte)(p.Z * 255.0f + 0.5f);
            destinationRgba[baseIdx + 3] = (byte)(p.W * 255.0f + 0.5f);
        }
    }

    public void Dispose()
    {
        _sourceBuffer.Dispose();
        _destinationBuffer.Dispose();
    }
}
