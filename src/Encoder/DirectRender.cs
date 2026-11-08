using Botamochi.Pipeline;
using Botamochi.Rendering;
using Microsoft.Extensions.Logging;

namespace Botamochi.Encoder;

internal static class DirectRender
{
    internal static async Task Run(int width, int height, int fps, int frames, TestPattern pattern,
        int[] gpus, int sessions, string output, bool pace, bool namedPipe, int pipeBytes,
        int? node, bool physical, bool lossless, ILogger logger)
    {
        if (frames < 1) throw new ArgumentException("At least one frame is required.");
        sessions = Math.Min(sessions, frames);
        if (sessions > 1 && pace) throw new ArgumentException("Multiple sessions require uncapped rendering.");
        string fullPath = Path.GetFullPath(output);
        string pending = Path.Combine(Path.GetDirectoryName(fullPath)!,
            $".botamochi-{Guid.NewGuid():N}{Path.GetExtension(fullPath)}");
        try
        {
            await RenderToFile(pending);
            File.Move(pending, fullPath, overwrite: true);
        }
        finally { File.Delete(pending); }

        async Task RenderToFile(string path)
        {
            await using var mux = new FfmpegProcessBridge("ffmpeg", width, height, fps, path, logger,
                namedPipe, pipeBytes, encoded: true);
            string? directory = sessions > 1 ? Directory.CreateTempSubdirectory("botamochi-").FullName : null;
            var jobs = new Task[sessions];
            try
            {
                for (int i = 0; i < sessions; i++)
                {
                    int worker = i;
                    jobs[i] = Task.Factory.StartNew(() =>
                    {
                        WorkerPlacement.Apply(worker, node, physical);
                        int first = (int)((long)frames * worker / sessions);
                        int end = (int)((long)frames * (worker + 1) / sessions);
                        using var encoder = new DirectNvenc(width, height, fps, gpus[worker % gpus.Length], pattern, lossless, !pace && end - first >= 4);
                        logger.LogInformation("NVENC worker {Worker}: GPU {Adapter}, {Surfaces} input surfaces",
                            worker, gpus[worker % gpus.Length], encoder.SurfaceCount);
                        var bars = !encoder.Upload.IsEmpty && pattern == TestPattern.ColorBars ? new ColorBarUpload(encoder.Upload, width, height) : null;
                        using var scene = encoder.Upload.IsEmpty || bars != null ? null : new SceneWorkers(width, height, pattern, sessions);
                        using var part = worker == 0 || directory == null ? null : File.Create(Path.Combine(directory, worker + ".h264"));
                        var pacer = pace ? new PipelinePacer(fps) : null;
                        for (int f = first; f < end; f++)
                        {
                            if (pacer != null) pacer.WaitNextFrameAsync().AsTask().GetAwaiter().GetResult();
                            float time = (float)f / fps;
                            bars?.Render(encoder.Upload, time);
                            scene?.Render(encoder.Upload, f, time);
                            WritePacket(encoder.Encode(time));
                        }
                        while (true)
                        {
                            var packet = encoder.Drain();
                            if (packet.IsEmpty) break;
                            WritePacket(packet);
                        }

                        void WritePacket(ReadOnlySpan<byte> packet)
                        {
                            if (packet.IsEmpty) return;
                            if (part == null) mux.WriteFrameSpanSync(packet);
                            else part.Write(packet);
                        }
                    }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                }
                await Task.WhenAll(jobs);
                if (directory != null)
                {
                    byte[] buffer = new byte[1 << 20];
                    for (int i = 1; i < sessions; i++)
                    {
                        using var part = File.OpenRead(Path.Combine(directory, i + ".h264"));
                        int count;
                        while ((count = part.Read(buffer)) != 0) mux.WriteFrameSpanSync(buffer.AsSpan(0, count));
                    }
                }
            }
            finally
            {
                if (directory != null) Directory.Delete(directory, recursive: true);
            }
        }
    }

}
