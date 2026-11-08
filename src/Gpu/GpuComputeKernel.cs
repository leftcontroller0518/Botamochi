using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ComputeSharp;
using Botamochi.Core;

namespace Botamochi.Gpu;

/// <summary>
/// GPU上で直接数式アニメーション（プラズマ波）を計算し、同時にNV12へ直接パック出力する超高速カーネル
/// CPU負荷を完全にゼロにし、GPU上で描画とエンコード前処理を完結
/// </summary>
[ThreadGroupSize(256, 1, 1)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct ProceduralPlasmaNv12Kernel : IComputeShader
{
    private readonly ReadWriteBuffer<uint> _destinationNv12;
    private readonly int _width;
    private readonly int _height;
    private readonly float4 _times;
    private readonly int _stride;
    private readonly int _yPackCount;

    public ProceduralPlasmaNv12Kernel(
        ReadWriteBuffer<uint> destinationNv12,
        int width,
        int height,
        float time) : this(destinationNv12, width, height, new float4(time, time, time, time), 0) { }

    internal ProceduralPlasmaNv12Kernel(ReadWriteBuffer<uint> destinationNv12, int width, int height,
        float4 times, int stride)
    {
        _destinationNv12 = destinationNv12;
        _width = width;
        _height = height;
        _times = times;
        _stride = stride;
        _yPackCount = (width * height) / 4;
    }

    public void Execute()
    {
        int id = ThreadIds.X;

        if (id >= _yPackCount) return;

        int frame = ThreadIds.Y;
        int offset = frame * _stride;
        float time = frame == 0 ? _times.X : frame == 1 ? _times.Y : frame == 2 ? _times.Z : _times.W;
        int packsPerRow = _width / 4;
        int x = (id % packsPerRow) * 4;
        int y = id / packsPerRow;
        float3 a = EvalRgb(x, y, time);
        float3 b = EvalRgb(x + 1, y, time);
        float3 c = EvalRgb(x + 2, y, time);
        float3 d = EvalRgb(x + 3, y, time);
        _destinationNv12[offset + id] = EvalY(a) | (EvalY(b) << 8) | (EvalY(c) << 16) | (EvalY(d) << 24);

        if ((y & 1) == 0)
        {
            int uvIndex = _yPackCount + (y / 2) * packsPerRow + id % packsPerRow;
            _destinationNv12[offset + uvIndex] = EvalU(a) | (EvalV(a) << 8) | (EvalU(c) << 16) | (EvalV(c) << 24);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float3 EvalRgb(int x, int y, float time)
    {
        float nx = (float)x / _width;
        float ny = (float)y / _height;

        float v1 = Hlsl.Sin(nx * 10.0f + time * 2.0f);
        float v2 = Hlsl.Sin(10.0f * (nx * Hlsl.Sin(time * 0.5f) + ny * Hlsl.Cos(time * 0.333f)) + time);
        float cx = nx + 0.5f * Hlsl.Sin(time * 0.2f) - 0.5f;
        float cy = ny + 0.5f * Hlsl.Cos(time * 0.333f) - 0.5f;
        float v3 = Hlsl.Sin(Hlsl.Sqrt(100.0f * (cx * cx + cy * cy) + 1.0f) + time * 3.0f);

        float v = (v1 + v2 + v3) * 0.33333f;

        float r = Hlsl.Sin(v * 3.14159f) * 0.5f + 0.5f;
        float g = Hlsl.Cos(v * 3.14159f) * 0.5f + 0.5f;
        float b = Hlsl.Sin(v * 3.14159f + 2.09439f) * 0.5f + 0.5f;

        return new float3(r, g, b);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint EvalY(float3 rgb)
    {
        float yVal = 16.0f + 219.0f * (0.2126f * rgb.X + 0.7152f * rgb.Y + 0.0722f * rgb.Z);
        return (uint)Hlsl.Clamp(yVal + 0.5f, 16.0f, 235.0f);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint EvalU(float3 rgb)
    {
        float uVal = 128.0f + 224.0f * (-0.11457f * rgb.X - 0.38543f * rgb.Y + 0.5f * rgb.Z);
        return (uint)Hlsl.Clamp(uVal + 0.5f, 16.0f, 240.0f);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint EvalV(float3 rgb)
    {
        float vVal = 128.0f + 224.0f * (0.5f * rgb.X - 0.45415f * rgb.Y - 0.04585f * rgb.Z);
        return (uint)Hlsl.Clamp(vVal + 0.5f, 16.0f, 240.0f);
    }
}

/// <summary>
/// GPU上でRGBA8888からNV12 (Y + UV interleaved) へ超並列パック変換を行うCompute Shader
/// </summary>
[ThreadGroupSize(256, 1, 1)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct RgbaToNv12PackKernel : IComputeShader
{
    private readonly ReadOnlyBuffer<uint> _sourceRgba;
    private readonly ReadWriteBuffer<uint> _destinationNv12;
    private readonly int _width;
    private readonly int _height;
    private readonly int _yPackCount;
    private readonly int _uvPackCount;

    public RgbaToNv12PackKernel(
        ReadOnlyBuffer<uint> sourceRgba,
        ReadWriteBuffer<uint> destinationNv12,
        int width,
        int height)
    {
        _sourceRgba = sourceRgba;
        _destinationNv12 = destinationNv12;
        _width = width;
        _height = height;
        _yPackCount = (width * height) / 4;
        _uvPackCount = (width * (height / 2)) / 4;
    }

    public void Execute()
    {
        int id = ThreadIds.X;

        // 1. Y平面の計算 (4ピクセル -> 1 uint パック)
        if (id < _yPackCount)
        {
            int basePixelIdx = id * 4;
            uint p0 = _sourceRgba[basePixelIdx];
            uint p1 = _sourceRgba[basePixelIdx + 1];
            uint p2 = _sourceRgba[basePixelIdx + 2];
            uint p3 = _sourceRgba[basePixelIdx + 3];

            uint y0 = CalculateY(p0);
            uint y1 = CalculateY(p1);
            uint y2 = CalculateY(p2);
            uint y3 = CalculateY(p3);

            _destinationNv12[id] = y0 | (y1 << 8) | (y2 << 16) | (y3 << 24);
        }

        // 2. UV平面の計算 (2つの 2x2 ブロック = 4バイト -> 1 uint パック)
        if (id < _uvPackCount)
        {
            int blocksPerRow = _width / 4;
            int blockX = id % blocksPerRow;
            int blockY = id / blocksPerRow;

            int px0 = blockX * 4;
            int py0 = blockY * 2;
            int pixelIdx0 = py0 * _width + px0;
            int pixelIdx1 = pixelIdx0 + 2;

            uint pBlock0 = _sourceRgba[pixelIdx0];
            uint pBlock1 = _sourceRgba[pixelIdx1];

            uint u0 = CalculateU(pBlock0);
            uint v0 = CalculateV(pBlock0);
            uint u1 = CalculateU(pBlock1);
            uint v1 = CalculateV(pBlock1);

            int uvBaseOffset = _yPackCount;
            _destinationNv12[uvBaseOffset + id] = u0 | (v0 << 8) | (u1 << 16) | (v1 << 24);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint CalculateY(uint rgba)
    {
        uint r = rgba & 0xFF;
        uint g = (rgba >> 8) & 0xFF;
        uint b = (rgba >> 16) & 0xFF;

        int yVal = (int)(r * 3483 + g * 11718 + b * 1183 + 262144) >> 14;
        return (uint)Hlsl.Clamp(yVal, 16, 235);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint CalculateU(uint rgba)
    {
        int r = (int)(rgba & 0xFF);
        int g = (int)((rgba >> 8) & 0xFF);
        int b = (int)((rgba >> 16) & 0xFF);

        int uVal = (-1877 * r - 6315 * g + 8192 * b + 2097152) >> 14;
        return (uint)Hlsl.Clamp(uVal, 16, 240);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint CalculateV(uint rgba)
    {
        int r = (int)(rgba & 0xFF);
        int g = (int)((rgba >> 8) & 0xFF);
        int b = (int)((rgba >> 16) & 0xFF);

        int vVal = (8192 * r - 7441 * g - 751 * b + 2097152) >> 14;
        return (uint)Hlsl.Clamp(vVal, 16, 240);
    }
}

/// <summary>
/// ダブル/トリプルバッファリング対応 高速GPUコンピュートパイプライン
/// </summary>
public sealed class GpuComputePipeline : IDisposable
{
    private readonly GraphicsDevice _device;
    private readonly int _width;
    private readonly int _height;
    private readonly int _totalPixels;
    private readonly int _nv12UintCount;
    private readonly ReadOnlyBuffer<uint>? _sourceRgbaBuffer;
    private readonly UploadBuffer<uint>? _upload;
    private readonly ReadBackBuffer<uint>? _readback;
    private readonly NativeFrameBuffer[] _frames;
    private readonly ReadWriteBuffer<uint> _gpuNv12Buffer;
    private readonly int _slotStride;
    private readonly int _batch;
    private int _next, _ready;

    internal Span<byte> Upload => MemoryMarshal.AsBytes(_upload!.Span);

    public GpuComputePipeline(int width, int height) : this(width, height, 0, true) { }

    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.NonPublicFields, typeof(uint))]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Both GPU buffers use UInt32 and its fields are preserved above.")]
    internal unsafe GpuComputePipeline(int width, int height, int slots, bool needsRgba, bool batchPlasma = false)
    {
        if (width % 4 != 0 || height % 2 != 0)
            throw new ArgumentException("Packed GPU output requires a multiple-of-four width and even height.");

        _width = width;
        _height = height;
        _totalPixels = width * height;
        _device = GraphicsDevice.GetDefault();

        _nv12UintCount = (width * height * 3) / (2 * 4);

        // Keep every mapped frame on a 128-byte boundary, including odd chroma row counts.
        _slotStride = checked(_nv12UintCount + 31) & ~31;
        _batch = batchPlasma ? Math.Clamp((8 << 20) / _slotStride, 1, 4) : 1;
        // The whole batch is written before the first frame can enter the bounded queue.
        slots += _batch - 1;
        _frames = new NativeFrameBuffer[slots];
        int outputSize = checked((_batch - 1) * _slotStride + _nv12UintCount);
        _gpuNv12Buffer = _device.AllocateReadWriteBuffer<uint>(outputSize);
        try
        {
            if (needsRgba) _sourceRgbaBuffer = _device.AllocateReadOnlyBuffer<uint>(_totalPixels);
            if (slots > 0)
            {
                if (needsRgba) _upload = _device.AllocateUploadBuffer<uint>(_totalPixels);
                _readback = _device.AllocateReadBackBuffer<uint>(checked(slots * _slotStride));
                // TransferBuffer.Span points into a persistently mapped D3D12 resource.
                fixed (uint* data = _readback.Span)
                {
                    for (int i = 0; i < slots; i++)
                        _frames[i] = new NativeFrameBuffer(data + i * _slotStride, _nv12UintCount * sizeof(uint));
                }
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>
    /// GPU完結: 数式アニメーションをGPU数千コアで直接計算し、NV12として即座にリードバック
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RenderProceduralGpu(float time, Span<byte> destinationNv12Bytes)
    {
        DispatchPlasma(time);
        _gpuNv12Buffer.CopyTo(MemoryMarshal.Cast<byte, uint>(destinationNv12Bytes));
    }

    private void DispatchPlasma(float time) =>
        _device.For(_totalPixels / 4, new ProceduralPlasmaNv12Kernel(_gpuNv12Buffer, _width, _height, time));

    internal NativeFrameBuffer RenderPlasmaFrame(int frame, int fps, int remaining)
    {
        if (_ready == 0)
        {
            int count = Math.Min(Math.Min(_batch, remaining), _frames.Length - _next);
            var times = new float4((float)frame / fps, (float)(frame + 1) / fps,
                (float)(frame + 2) / fps, (float)(frame + 3) / fps);
            _device.For(_totalPixels / 4, count,
                new ProceduralPlasmaNv12Kernel(_gpuNv12Buffer, _width, _height, times, _slotStride));
            _gpuNv12Buffer.CopyTo(_readback!, 0, _next * _slotStride, (count - 1) * _slotStride + _nv12UintCount);
            _ready = count;
        }
        _ready--;
        return AdvanceSlot();
    }

    /// <summary>
    /// CPUで生成されたRGBAバイト列をGPUへアップロードし、NV12へ超並列変換してリードバック
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ExecuteRgbaToNv12Gpu(ReadOnlySpan<byte> rgbaBytes, Span<byte> destinationNv12Bytes)
    {
        ReadOnlySpan<uint> rgbaUints = MemoryMarshal.Cast<byte, uint>(rgbaBytes);

        _sourceRgbaBuffer!.CopyFrom(rgbaUints);
        DispatchConversion();
        _gpuNv12Buffer.CopyTo(MemoryMarshal.Cast<byte, uint>(destinationNv12Bytes));
    }

    internal NativeFrameBuffer ConvertUploadedFrame()
    {
        _sourceRgbaBuffer!.CopyFrom(_upload!);
        DispatchConversion();
        return DownloadNextFrame();
    }

    private void DispatchConversion()
    {
        if (PackWave.Available && _device.WavefrontSize >= 4)
            _device.For(_width, _height, new PackWave(_sourceRgbaBuffer!, _gpuNv12Buffer));
        else
            _device.For(_totalPixels / 4, new RgbaToNv12PackKernel(_sourceRgbaBuffer!, _gpuNv12Buffer, _width, _height));
    }

    private NativeFrameBuffer DownloadNextFrame()
    {
        int slot = _next;
        _gpuNv12Buffer.CopyTo(_readback!, 0, slot * _slotStride, _nv12UintCount);
        return AdvanceSlot();
    }

    private NativeFrameBuffer AdvanceSlot()
    {
        var frame = _frames[_next];
        if (++_next == _frames.Length) _next = 0;
        return frame;
    }

    public void Dispose()
    {
        _readback?.Dispose();
        _upload?.Dispose();
        _sourceRgbaBuffer?.Dispose();
        _gpuNv12Buffer.Dispose();
    }
}
