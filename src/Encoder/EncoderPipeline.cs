using Botamochi.Core;

namespace Botamochi.Encoder;

/// <summary>
/// Single-producer/single-consumer ring with allocation-free fast paths.
/// </summary>
internal sealed class EncoderPipeline : IAsyncDisposable
{
    private readonly NativeFrameBuffer[] _frames;
    private readonly FfmpegProcessBridge _bridge;
    private readonly AutoResetEvent _available = new(false);
    private readonly AutoResetEvent _space = new(false);
    private readonly Task _consumerTask;
    private long _read, _write;
    private int _readSlot, _writeSlot;
    private int _waitingConsumer, _waitingProducer;
    private volatile bool _complete;
    private Exception? _error;

    public int QueueCount => (int)(Volatile.Read(ref _write) - Volatile.Read(ref _read));

    public EncoderPipeline(FfmpegProcessBridge bridge, int boundedCapacity,
        int? numaNode = null, bool physical = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(boundedCapacity);
        _frames = new NativeFrameBuffer[boundedCapacity];
        _bridge = bridge;
        _consumerTask = Task.Factory.StartNew(() => Consume(numaNode, physical), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    public void EnqueueFrame(NativeFrameBuffer buffer)
    {
        if (TryWrite(buffer)) return;
        while (true)
        {
            Interlocked.Exchange(ref _waitingProducer, 1);
            if (TryWrite(buffer)) return;
            _space.WaitOne();
        }
    }

    private bool TryWrite(NativeFrameBuffer buffer)
    {
        if (_complete) throw new IOException("Encoder queue closed", _error);
        long write = _write;
        if (write - Volatile.Read(ref _read) == _frames.Length) return false;
        _frames[_writeSlot] = buffer;
        if (++_writeSlot == _frames.Length) _writeSlot = 0;
        Volatile.Write(ref _write, write + 1);
        if (Interlocked.Exchange(ref _waitingConsumer, 0) != 0) _available.Set();
        return true;
    }

    private void Consume(int? node, bool physical)
    {
        try
        {
            Botamochi.Pipeline.WorkerPlacement.Apply(0, node, physical);
            while (true)
            {
                long read = _read;
                if (read == Volatile.Read(ref _write))
                {
                    Interlocked.Exchange(ref _waitingConsumer, 1);
                    if (read != Volatile.Read(ref _write)) continue;
                    if (_complete && read == Volatile.Read(ref _write)) break;
                    _available.WaitOne();
                    continue;
                }
                NativeFrameBuffer frame = _frames[_readSlot];
                if (++_readSlot == _frames.Length) _readSlot = 0;
                Volatile.Write(ref _read, read + 1);
                if (Interlocked.Exchange(ref _waitingProducer, 0) != 0) _space.Set();
                _bridge.WriteFrameSpanSync(frame.Span);
            }
        }
        catch (Exception ex)
        {
            _error = ex;
            _complete = true;
            if (Interlocked.Exchange(ref _waitingProducer, 0) != 0) _space.Set();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _complete = true;
        _available.Set();
        try { await _consumerTask.ConfigureAwait(false); }
        finally
        {
            _space.Dispose();
            _available.Dispose();
            await _bridge.DisposeAsync().ConfigureAwait(false);
        }
    }
}
