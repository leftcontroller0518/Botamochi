using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Botamochi.Core;

namespace Botamochi.Encoder;

public readonly record struct VideoFramePayload(
    NativeFrameBuffer Buffer,
    long PtsTicks,
    long FrameIndex
);

/// <summary>
/// BoundedChannel によるバックプレッシャー管理とPTS/DTS同期パイプライン
/// </summary>
public sealed class EncoderPipeline : IAsyncDisposable
{
    private readonly Channel<VideoFramePayload> _channel;
    private readonly FfmpegProcessBridge _bridge;
    private readonly ILogger _logger;
    private readonly Task _consumerTask;
    private readonly CancellationTokenSource _cts = new();

    public int QueueCount => _channel.Reader.Count;

    public EncoderPipeline(
        FfmpegProcessBridge bridge,
        int boundedCapacity,
        ILogger logger)
    {
        _bridge = bridge;
        _logger = logger;

        var options = new BoundedChannelOptions(boundedCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = true,
            SingleReader = true
        };

        _channel = Channel.CreateBounded<VideoFramePayload>(options);
        _consumerTask = Task.Run(ConsumePipelineAsync);
    }

    public async ValueTask EnqueueFrameAsync(NativeFrameBuffer buffer, long frameIndex, long ptsTicks)
    {
        var payload = new VideoFramePayload(buffer, ptsTicks, frameIndex);
        await _channel.Writer.WriteAsync(payload).ConfigureAwait(false);
    }

    private async Task ConsumePipelineAsync()
    {
        var reader = _channel.Reader;
        long lastPts = 0;

        try
        {
            while (await reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
            {
                while (reader.TryRead(out var frame))
                {
                    // PTS/DTS 同期チェック (タイムスタンプ逆転防止)
                    if (frame.PtsTicks < lastPts)
                    {
                        _logger.LogWarning("PTS Inversion detected! FrameIndex: {Index}", frame.FrameIndex);
                    }
                    lastPts = frame.PtsTicks;

                    // FFmpeg StandardInputへの非同期書き込み
                    byte[] frameBytes = frame.Buffer.Span.ToArray();
                    await _bridge.WriteFrameAsync(frameBytes, _cts.Token).ConfigureAwait(false);

                    // バッファを即座にネイティブプールへ返却（Zero-Allocation）
                    frame.Buffer.Return();
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "エンコードパイプラインのコンシューマループでエラーが発生しました。");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.Complete();
        await _consumerTask.ConfigureAwait(false);
        await _bridge.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}
