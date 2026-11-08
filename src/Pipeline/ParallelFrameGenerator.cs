using System.Runtime.CompilerServices;
using Botamochi.Rendering;

namespace Botamochi.Pipeline;

/// <summary>
/// TPL (Task Parallel Library) を駆使したマルチスレッド・フレーム事前生成エンジン
/// </summary>
public static class ParallelFrameGenerator
{
    /// <summary>
    /// CPUコア数に応じてフレームバッファを並行生成
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void GenerateFramesParallel(
        Span<byte> targetBuffer,
        int width,
        int height,
        int frameIndex,
        float timeSeconds,
        TestPattern pattern)
    {
        // 内部で並列プロシージャル生成
        ProceduralScene.GenerateFrame(targetBuffer, width, height, frameIndex, timeSeconds, pattern);
    }
}
