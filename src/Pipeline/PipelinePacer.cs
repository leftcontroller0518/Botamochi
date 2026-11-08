using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Botamochi.Pipeline;

/// <summary>
/// 高精度フレームペーシング & スロットリング制御 (Jitter-free Precision Timer)
/// </summary>
public sealed class PipelinePacer
{
    private readonly long _ticksPerFrame;
    private long _nextTargetTick;
    private readonly bool _pacingEnabled;

    public PipelinePacer(int targetFps, bool enablePacing = true)
    {
        _pacingEnabled = enablePacing;
        _ticksPerFrame = Stopwatch.Frequency / targetFps;
        _nextTargetTick = Stopwatch.GetTimestamp();
    }

    public ValueTask WaitNextFrameAsync()
    {
        if (!_pacingEnabled) return ValueTask.CompletedTask;
        long now = Stopwatch.GetTimestamp();
        _nextTargetTick += _ticksPerFrame;
        if (_nextTargetTick <= now)
        {
            _nextTargetTick = now;
            return ValueTask.CompletedTask;
        }
        return new ValueTask(Task.Delay(Stopwatch.GetElapsedTime(now, _nextTargetTick)));
    }

    /// <summary>
    /// 次のフレーム目標時刻まで精密に待機（スロットリング）
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization | MethodImplOptions.AggressiveInlining)]
    public void WaitNextFrame()
    {
        if (!_pacingEnabled) return;

        _nextTargetTick += _ticksPerFrame;
        long currentTick = Stopwatch.GetTimestamp();

        long diff = _nextTargetTick - currentTick;
        if (diff <= 0)
        {
            // フレーム遅延発生時は目標時刻を現在時刻にリスケジュール
            _nextTargetTick = currentTick;
            return;
        }

        // 粗い待機 (残り1ms以上はThread.Yield / Sleep(0))
        long msThreshold = Stopwatch.Frequency / 1000;
        while ((_nextTargetTick - Stopwatch.GetTimestamp()) > msThreshold)
        {
            Thread.Yield();
        }

        // ナノ秒精度のスピンウェイト（ジッター完全排除）
        var spinner = new SpinWait();
        while (Stopwatch.GetTimestamp() < _nextTargetTick)
        {
            spinner.SpinOnce();
        }
    }
}
