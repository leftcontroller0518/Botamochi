using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Botamochi.Core;
using Botamochi.Encoder;
using Botamochi.Gpu;
using Botamochi.Simd;

namespace Botamochi;

public static class Program
{
    private const int Width = 1920;
    private const int Height = 1080;
    private const int Fps = 60;
    private const int TotalFrames = 300; // 5秒分 (300フレーム)
    private const string OutputFile = "render_output.mp4";

    public static async Task Main(string[] args)
    {
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole().SetMinimumLevel(LogLevel.Information);
        });
        var logger = loggerFactory.CreateLogger("Botamochi");

        logger.LogInformation("=============================================================");
        logger.LogInformation(" Botamochi .NET 10 Zero-Allocation GPU Render & Encode Pipeline");
        logger.LogInformation(" Resolution: {Width}x{Height} @ {Fps}fps, Target: {Total} frames", Width, Height, Fps, TotalFrames);
        logger.LogInformation("=============================================================");

        // 1. ネイティブフレームバッファプールの初期化 (NV12用, Zero-Allocation)
        using var memoryPool = new MemoryPoolManager(Width, Height, maxCapacity: 16);

        // 2. FFmpeg プロセスおよびエンコードパイプラインの初期化
        string ffmpegPath = "ffmpeg"; // PATH環境変数上のffmpeg

        FfmpegProcessBridge? ffmpegBridge = null;
        EncoderPipeline? pipeline = null;

        try
        {
            ffmpegBridge = new FfmpegProcessBridge(ffmpegPath, Width, Height, Fps, OutputFile, logger);
            pipeline = new EncoderPipeline(ffmpegBridge, boundedCapacity: 8, logger);
            logger.LogInformation("FFmpeg Bridge successfully initialized. Output -> {OutputFile}", OutputFile);
        }
        catch (Exception ex)
        {
            logger.LogWarning("FFmpegが見つからないか起動できませんでした: {Message}。オフラインベンチマークモードに切り替えます。", ex.Message);
        }

        // 3. GPUコンピュートパイプラインの初期化
        GpuComputePipeline? gpuCompute = null;
        try
        {
            gpuCompute = new GpuComputePipeline(Width, Height);
            logger.LogInformation("ComputeSharp (DirectX 12 Compute Shader) Pipeline Ready.");
        }
        catch (Exception ex)
        {
            logger.LogWarning("GPUコンピュートの初期化をスキップします: {Message}", ex.Message);
        }

        // テスト用RGBAフレームソースの確保
        byte[] rawRgbaSource = new byte[Width * Height * 4];
        byte[] processedRgba = new byte[Width * Height * 4];
        Array.Fill<byte>(rawRgbaSource, 128); // ニュートラルグレー

        var stopwatch = Stopwatch.StartNew();
        long ticksPerFrame = Stopwatch.Frequency / Fps;

        logger.LogInformation("--- Rendering & Encoding Loop Started ---");

        for (int frameIdx = 0; frameIdx < TotalFrames; frameIdx++)
        {
            long currentPts = frameIdx * ticksPerFrame;

            // ネイティブメモリプールからNV12バッファを借用 (GC Allocation: 0)
            NativeFrameBuffer rentedBuffer = memoryPool.Rent();

            if (gpuCompute != null)
            {
                // GPU コンピュートカーネルでリアルタイムカラーグレーディング処理を超並列実行
                gpuCompute.ExecuteColorGrading(rawRgbaSource, processedRgba, brightness: 0.05f, contrast: 1.1f);
            }
            else
            {
                rawRgbaSource.CopyTo(processedRgba, 0);
            }

            // SIMD (AVX2/AVX-512) によるRGBAからNV12への超並列変換
            unsafe
            {
                byte* pSrc = (byte*)Unsafe.AsPointer(ref processedRgba[0]);
                byte* pDst = (byte*)rentedBuffer.NativePointer;
                byte* pDstY = pDst;
                byte* pDstUv = pDst + (Width * Height);

                SimdConverter.ConvertRgbaToNv12(pSrc, pDstY, pDstUv, Width, Height);
            }

            // エンコードパイプラインへキューイング（バックプレッシャー制御）
            if (pipeline != null)
            {
                await pipeline.EnqueueFrameAsync(rentedBuffer, frameIdx, currentPts);
            }
            else
            {
                // FFmpeg無しの場合は即時プールへ返却
                rentedBuffer.Return();
            }

            if ((frameIdx + 1) % 60 == 0 || frameIdx == TotalFrames - 1)
            {
                double elapsedSec = stopwatch.Elapsed.TotalSeconds;
                double currentFps = (frameIdx + 1) / (elapsedSec > 0 ? elapsedSec : 1.0);
                int queueCount = pipeline?.QueueCount ?? 0;
                logger.LogInformation("Processed: {Frame}/{Total} frames ({Fps:F1} FPS) | Queue Depth: {Queue}",
                    frameIdx + 1, TotalFrames, currentFps, queueCount);
            }
        }

        stopwatch.Stop();
        logger.LogInformation("--- Pipeline Finished in {Elapsed:F2}s ({AverageFps:F1} avg FPS) ---",
            stopwatch.Elapsed.TotalSeconds, TotalFrames / stopwatch.Elapsed.TotalSeconds);

        // リソースの安全な破棄
        if (pipeline != null)
        {
            await pipeline.DisposeAsync();
        }
        gpuCompute?.Dispose();

        logger.LogInformation("All pipeline resources safely disposed. Completed successfully.");
    }
}
