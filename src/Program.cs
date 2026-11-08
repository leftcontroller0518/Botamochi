using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Botamochi.Core;
using Botamochi.Encoder;
using Botamochi.Gpu;
using Botamochi.Pipeline;
using Botamochi.Rendering;
using Botamochi.Simd;

namespace Botamochi;

public static class Program
{
    public static async Task Main(string[] args)
    {
        RenderOptions options;
        try { options = RenderOptions.Parse(args); }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"Botamochi: {ex.Message}");
            Environment.ExitCode = 2;
            return;
        }
        if (options.ShowHelp)
        {
            Console.WriteLine(RenderOptions.Help);
            return;
        }
        int width = options.Width, height = options.Height, fps = options.Fps;
        int totalFrames = options.FrameCount;
        float durationSec = options.Duration;
        string outputFile = options.Output;
        TestPattern pattern = options.Pattern;
        bool enablePacing = options.Pace;
        int[] gpus = options.Gpus;

        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole().SetMinimumLevel(LogLevel.Information);
        });
        var logger = loggerFactory.CreateLogger("Botamochi");

        logger.LogInformation("=============================================================");
        logger.LogInformation(" Botamochi .NET 10 Zero-Allocation Ultra-Performance Engine");
        logger.LogInformation(" Pattern   : {Pattern} | Pacing: {Pacing}", pattern, enablePacing ? "Precise Realtime" : "Uncapped Max Throughput");
        logger.LogInformation(" Resolution: {Width}x{Height} @ {Fps}fps ({Duration:F1}s, {Total} frames)",
            width, height, fps, durationSec, totalFrames);
        logger.LogInformation(" Output    : {OutputFile}", outputFile);
        logger.LogInformation("=============================================================");

        if (options.RealtimePriority)
        {
            using var process = Process.GetCurrentProcess();
            process.PriorityClass = ProcessPriorityClass.RealTime;
        }

        using var gcScope = new NoGcScope(options.GcBudgetMb);
        if (options.Direct || options.Lossless || options.Sessions > 1 || gpus.Length > 1)
        {
            await DirectRender.Run(width, height, fps, totalFrames, pattern, gpus, Math.Max(gpus.Length, options.Sessions),
                outputFile, enablePacing, options.NamedPipe, options.PipeBytes, options.NumaNode,
                options.PhysicalCores, options.Lossless, logger);
            logger.LogInformation("Direct NVENC completed -> {Path}", Path.GetFullPath(outputFile));
            return;
        }

        int frameBytes = checked(width * height * 3 / 2);
        int queueCapacity = Math.Clamp((96 << 20) / frameBytes - 2, 1, 8);
        // GPUコンピュートパイプラインの初期化 (DirectX 12 GPU NV12 Pack Kernel)
        GpuComputePipeline? gpuCompute = null;
        var colorBars = pattern == TestPattern.ColorBars ? new ColorBarsNv12Renderer(width, height) : null;
        if (colorBars == null && width % 4 == 0)
        {
            try
            {
                gpuCompute = new GpuComputePipeline(width, height, queueCapacity + 2, pattern != TestPattern.Plasma,
                    batchPlasma: !enablePacing && pattern == TestPattern.Plasma);
                logger.LogInformation("ComputeSharp (DirectX 12 GPU NV12 Pack Kernel) Ready.");
            }
            catch (Exception ex)
            {
                logger.LogWarning("GPUコンピュートをスキップし、CPU AVX-512/AVX2 SIMD を使用します: {Message}", ex.Message);
            }
        }

        using var gpu = gpuCompute;
        // The encoder must drain its queue before either ring is disposed.
        using var memoryPool = gpuCompute == null ? new NativeRingBufferPool(width, height, queueCapacity + 2) : null;

        // FFmpeg プロセスおよびエンコードパイプラインの初期化 (NVENC)
        string ffmpegPath = "ffmpeg";
        FfmpegProcessBridge? ffmpegBridge = null;
        EncoderPipeline? pipeline = null;

        try
        {
            ffmpegBridge = new FfmpegProcessBridge(ffmpegPath, width, height, fps, outputFile, logger,
                options.NamedPipe, options.PipeBytes, gpu: gpus[0]);
            pipeline = new EncoderPipeline(ffmpegBridge, queueCapacity, options.NumaNode, options.PhysicalCores);
            logger.LogInformation("FFmpeg Video Pipe initialized successfully.");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 2)
        {
            logger.LogWarning("FFmpegを起動できませんでした: {Message}。オフラインベンチマークモードで実行します。", ex.Message);
        }

        await using var encoding = pipeline;

        var pacer = new PipelinePacer(fps, enablePacing);
        // RGBA中間バッファ
        bool needsScene = colorBars == null && (gpuCompute == null || pattern != TestPattern.Plasma);
        byte[] rawRgbaSource = needsScene && gpuCompute == null ? new byte[width * height * 4] : [];

        using var workers = needsScene ? new SceneWorkers(width, height, pattern) : null;
        var stopwatch = Stopwatch.StartNew();

#if RENDER_STATS
        var swStep = new Stopwatch();
        double msGen = 0, msTransform = 0;
#endif

        logger.LogInformation("--- Rendering & Encoding Loop Started ---");

        for (int frameIdx = 0; frameIdx < totalFrames; frameIdx++)
        {
            // 精密フレームペーシング制御（スロットリング）
            await pacer.WaitNextFrameAsync();

            float timeSeconds = (float)frameIdx / fps;
            // CPUアリーナまたはGPU読み戻しリングの完成フレーム
            NativeFrameBuffer rentedBuffer;

            // ② GPU完結またはCPU+GPU/SIMDハイブリッドによる超高速レンダリング
#if RENDER_STATS
            swStep.Restart();
#endif
            if (colorBars != null)
            {
                rentedBuffer = memoryPool!.NextFrame();
                colorBars.Render(timeSeconds, rentedBuffer.Span);
#if RENDER_STATS
                msGen = swStep.Elapsed.TotalMilliseconds;
                msTransform = 0;
#endif
            }
            else if (gpuCompute != null && pattern == TestPattern.Plasma)
            {
                // 【GPU完全直結】GPU数千コアで直接数式描画＆NV12パックを完結
                rentedBuffer = gpuCompute.RenderPlasmaFrame(frameIdx, fps, totalFrames - frameIdx);
#if RENDER_STATS
                msGen = 0.0;
                msTransform = swStep.Elapsed.TotalMilliseconds;
#endif
            }
            else
            {
                // CPU側でのマルチスレッド並列プロシージャル描画
                workers!.Render(gpuCompute == null ? rawRgbaSource.AsSpan() : gpuCompute.Upload, frameIdx, timeSeconds);
#if RENDER_STATS
                msGen = swStep.Elapsed.TotalMilliseconds;
                swStep.Restart();
#endif
                if (gpuCompute != null)
                {
                    // GPUによるRGBA -> NV12超高速変換
                    rentedBuffer = gpuCompute.ConvertUploadedFrame();
                }
                else
                {
                    rentedBuffer = memoryPool!.NextFrame();
                    // CPU AVX-512 / AVX2 最新SIMDフォールバック
                    unsafe
                    {
                        fixed (byte* pSrc = rawRgbaSource)
                        {
                            byte* pDst = (byte*)rentedBuffer.NativePointer;
                            Avx512Converter.ConvertRgbaToNv12Vectorized(pSrc, pDst, pDst + width * height, width, height);
                        }
                    }
                }
#if RENDER_STATS
                msTransform = swStep.Elapsed.TotalMilliseconds;
#endif
            }

            // ③ 非同期エンコードキューへ送信（バックプレッシャー同期 & Zero-Allocation）
            if (pipeline != null)
            {
                pipeline.EnqueueFrame(rentedBuffer);
            }

#if RENDER_STATS
            // 進捗ログの出力
            if ((frameIdx + 1) % 60 == 0 || frameIdx == totalFrames - 1)
            {
                double elapsedSec = stopwatch.Elapsed.TotalSeconds;
                double currentFps = (frameIdx + 1) / (elapsedSec > 0 ? elapsedSec : 1.0);
                int queueCount = pipeline?.QueueCount ?? 0;
                logger.LogInformation("Frame: {Frame,4}/{Total} ({Progress,5:F1}%) | {Fps,5:F1} FPS | Queue: {Queue} | Gen: {Gen:F1}ms | Trans: {Trans:F1}ms",
                    frameIdx + 1, totalFrames, ((float)(frameIdx + 1) / totalFrames) * 100.0f, currentFps, queueCount,
                    msGen, msTransform);
            }
#endif
        }

        stopwatch.Stop();
        double totalSeconds = stopwatch.Elapsed.TotalSeconds;
        logger.LogInformation("--- Finished: {Total} frames in {Elapsed:F2}s ({AverageFps:F1} avg FPS) ---",
            totalFrames, totalSeconds, totalFrames / (totalSeconds > 0 ? totalSeconds : 1.0));

        if (pipeline != null) logger.LogInformation("Finalizing {OutputFile}", Path.GetFullPath(outputFile));
    }
}
