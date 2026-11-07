using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Botamochi.Encoder;

/// <summary>
/// YMM4互換: 外部FFmpegプロセスを起動し、StandardInput経由で生NV12フレームをパイプストリーミング送信するブリッジ
/// </summary>
public sealed partial class FfmpegProcessBridge : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Stream _standardInput;
    private readonly ILogger _logger;
    private readonly Task _stderrReadingTask;
    private readonly CancellationTokenSource _cts = new();

    public FfmpegProcessBridge(
        string ffmpegPath,
        int width,
        int height,
        int fps,
        string outputPath,
        ILogger logger)
    {
        _logger = logger;

        // rawvideo (NV12) からのハードウェア高速化エンコード引数 (YMM4基準)
        string arguments = $"-y -f rawvideo -pix_fmt nv12 -s {width}x{height} -r {fps} " +
                           $"-i - -c:v libx264 -preset ultrafast -tune zerolatency -pix_fmt yuv420p " +
                           $"\"{outputPath}\"";

        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = false
        };

        _process = new Process { StartInfo = startInfo };
        
        try
        {
            _process.Start();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FFmpegプロセスの起動に失敗しました。パスを確認してください: {Path}", ffmpegPath);
            throw;
        }

        // 4MBのバッファリングストリームでパイプ転送詰まりを防止
        _standardInput = new BufferedStream(_process.StandardInput.BaseStream, 4 * 1024 * 1024);

        // 標準エラー出力を監視して進捗・FPSをリアルタイムパース
        _stderrReadingTask = Task.Run(() => ReadStderrAsync(_process.StandardError, _cts.Token));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public async ValueTask WriteFrameAsync(ReadOnlyMemory<byte> frameData, CancellationToken ct)
    {
        await _standardInput.WriteAsync(frameData, ct).ConfigureAwait(false);
    }

    private async Task ReadStderrAsync(StreamReader reader, CancellationToken ct)
    {
        Regex fpsRegex = FpsRegex();
        while (!ct.IsCancellationRequested && !_process.HasExited)
        {
            string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line == null) break;

            var match = fpsRegex.Match(line);
            if (match.Success)
            {
                _logger.LogDebug("[FFmpeg Progress] {Line}", line);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _standardInput.FlushAsync().ConfigureAwait(false);
            _standardInput.Close();

            await _cts.CancelAsync();
            await _process.WaitForExitAsync().ConfigureAwait(false);
            await _stderrReadingTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // キャンセル要求による正常終了
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FFmpegプロセスの終了待機中に例外が発生しました。");
        }
        finally
        {
            _process.Dispose();
            _cts.Dispose();
        }
    }

    [GeneratedRegex(@"fps=\s*([0-9.]+)", RegexOptions.Compiled)]
    private static partial Regex FpsRegex();
}
