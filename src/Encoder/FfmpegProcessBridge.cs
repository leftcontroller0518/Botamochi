using System.Diagnostics;
using System.IO.Pipes;
using Microsoft.Extensions.Logging;

namespace Botamochi.Encoder;

public sealed class FfmpegProcessBridge : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Stream _input;
    private readonly Task<string> _errors;

    public FfmpegProcessBridge(string ffmpegPath, int width, int height, int fps, string outputPath,
        ILogger logger, bool namedPipe = false, int pipeBytes = 0, bool encoded = false, int gpu = 0)
    {
        var info = new ProcessStartInfo(ffmpegPath)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
            RedirectStandardInput = !namedPipe
        };
        void Add(params string[] args)
        {
            foreach (string arg in args) info.ArgumentList.Add(arg);
        }
        string name = $"botamochi-{Guid.NewGuid():N}";
        Add("-nostdin", "-loglevel", "error", "-y", "-f", encoded ? "h264" : "rawvideo");
        if (!encoded) Add("-pix_fmt", "nv12", "-s", $"{width}x{height}");
        Add("-r", fps.ToString(), "-i", namedPipe ? $@"\\.\pipe\{name}" : "pipe:0");
        if (encoded) Add("-c:v", "copy");
        else
        {
            var (type, codec, extra) = HardwareEncoderDetector.DetectBestEncoder(logger, gpu);
            Add("-c:v", codec);
            Add(extra.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            Add("-pix_fmt", type == HardwareAccelerationType.NvidiaNvenc ? "nv12" : "yuv420p");
        }
        Add(outputPath);
        int bufferSize = pipeBytes > 0 ? pipeBytes : Math.Clamp(width * height * 3 / 2, 65536, 4 << 20);
        var pipe = namedPipe ? new NamedPipeServerStream(name, PipeDirection.Out, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 0, bufferSize) : null;
        _process = new Process { StartInfo = info };
        bool started = false;
        try
        {
            _process.Start();
            started = true;
            _errors = _process.StandardError.ReadToEndAsync();
            if (pipe != null)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var connect = pipe.WaitForConnectionAsync(timeout.Token);
                var exit = _process.WaitForExitAsync(timeout.Token);
                if (Task.WhenAny(connect, exit).GetAwaiter().GetResult() == exit && _process.HasExited)
                    throw new IOException(_errors.GetAwaiter().GetResult());
                connect.GetAwaiter().GetResult();
                _input = pipe;
            }
            else _input = pipeBytes > 0 ? new BufferedStream(_process.StandardInput.BaseStream, pipeBytes)
                                        : _process.StandardInput.BaseStream;
        }
        catch
        {
            pipe?.Dispose();
            if (started && !_process.HasExited) _process.Kill();
            _process.Dispose();
            throw;
        }
    }

    public void WriteFrameSpanSync(ReadOnlySpan<byte> frame) => _input.Write(frame);

    public async ValueTask DisposeAsync()
    {
        try
        {
            _input.Dispose();
            await _process.WaitForExitAsync().ConfigureAwait(false);
            string errors = await _errors.ConfigureAwait(false);
            if (_process.ExitCode != 0) throw new IOException($"FFmpeg exited with code {_process.ExitCode}: {errors}");
        }
        finally { _process.Dispose(); }
    }
}
