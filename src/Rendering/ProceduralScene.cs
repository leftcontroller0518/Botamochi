using System.Numerics;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Runtime.CompilerServices;

namespace Botamochi.Rendering;

public enum TestPattern
{
    Plasma,          // 鮮やかなサイケデリック・プラズマ波
    CyberGrid,       // ネオン色のサイバーグリッド ＆ バウンドする光球
    RainbowGradient, // 時間とともに色相がダイナミックに変化するグラデーション
    ColorBars,       // 放送用SMPTE風カラーバー ＆ タイムバー
    Cyber3D          // 映画風3Dネオントンネル ＆ サイバーパンクHUD複合シーン
}

/// <summary>
/// フレームごとの映像データを生成するプロシージャル・ジェネレーター (Zero-Allocation)
/// </summary>
public static class ProceduralScene
{
    [MethodImpl(MethodImplOptions.AggressiveOptimization | MethodImplOptions.AggressiveInlining)]
    public static unsafe void GenerateFrame(
        Span<byte> rgbaBuffer,
        int width,
        int height,
        int frameIndex,
        float timeSeconds,
        TestPattern pattern)
    {
        if (pattern == TestPattern.ColorBars)
        {
            RenderColorBars(rgbaBuffer, width, height, frameIndex, timeSeconds);
            return;
        }
        uint[]? gridRow = pattern == TestPattern.CyberGrid ? CreateGridRow(width) : null;
        fixed (byte* start = rgbaBuffer[..checked(width * height * 4)])
        {
            nint address = (nint)start;
            int count = Math.Min(Environment.ProcessorCount, height);
            Parallel.For(0, count, i => RenderRows(address, width, height, i * height / count,
                (i + 1) * height / count, timeSeconds, pattern, gridRow, null, null));
        }
    }

    internal static unsafe void RenderRows(nint address, int width, int height, int first, int end,
        float timeSeconds, TestPattern pattern, uint[]? gridRow, Vector2[]? polar, Vector3[]? plasma)
    {
        switch (pattern)
        {
            case TestPattern.Cyber3D:
                RenderCyber3D((byte*)address, width, height, first, end, timeSeconds, polar);
                break;
            case TestPattern.Plasma:
                RenderPlasma((byte*)address, width, height, first, end, timeSeconds, plasma);
                break;
            case TestPattern.CyberGrid:
                RenderCyberGrid((byte*)address, width, height, first, end, timeSeconds, gridRow!);
                break;
            case TestPattern.RainbowGradient:
                RenderRainbowGradient((byte*)address, width, height, first, end, timeSeconds);
                break;
        }
    }

    /// <summary>
    /// パターン5: 映画風3Dネオントンネル ＆ 多重回転リング ＆ サイバーHUD複合シーン
    /// </summary>
    internal static Vector2 Polar(float dx, float dy) => new(MathF.Sqrt(dx * dx + dy * dy), MathF.Atan2(dy, dx));

    private static unsafe void RenderCyber3D(byte* pBuf, int width, int height, int first, int end, float t, Vector2[]? polar)
    {
        float centerX = width * 0.5f;
        float centerY = height * 0.5f;
        float coreRadius = 35.0f + 10.0f * MathF.Sin(t * 8.0f);

        for (int y = first; y < end; y++)
        {
            byte* row = pBuf + (y * width * 4);
            float dy = y - centerY;
            float barHeight = 0;
            for (int x = 0; x < width; x++)
            {
                float dx = x - centerX;

                // 1. 極座標変換 (3Dトンネル用)
                Vector2 position = polar == null ? Polar(dx, dy) : polar[y * width + x];
                float dist = position.X;
                float angle = position.Y;

                // 3Dワームホールトンネル: 距離の逆数でパースペクティブ（奥への奥行き）を表現
                float zDepth = (dist > 1.0f) ? (300.0f / dist) : 300.0f;
                float tunnelCoord = zDepth - (t * 4.0f); // 手前へ流れる速度

                // トンネルの壁面グリッド (シアンとマゼンタ)
                float spiral = MathF.Sin(angle * 6.0f + tunnelCoord * 3.0f);
                float ring = MathF.Sin(tunnelCoord * 12.0f);

                float tunnelGlow = Math.Clamp((spiral * ring) * 1.5f, 0.0f, 1.0f);

                // トンネルのベース色（奥ほど暗く、手前はネオン）
                float depthFade = Math.Clamp(dist / centerX, 0.0f, 1.0f);
                float r = depthFade * tunnelGlow * 180.0f;
                float g = depthFade * tunnelGlow * 60.0f;
                float b = depthFade * tunnelGlow * 255.0f;

                // 2. 中央の3D回転サイバーリング (3重リング)
                if (dist > 67.0f && dist < 213.0f)
                {
                    int ringIdx = dist < 105.0f ? 1 : dist < 175.0f ? 2 : 3;
                    float ringRadius = 70.0f * ringIdx;
                    float ringDist = MathF.Abs(dist - ringRadius);
                    if (ringDist < 3.0f)
                    {
                        float ringAngle = angle + (t * (ringIdx % 2 == 0 ? 2.0f : -2.0f));
                        float notch = MathF.Sin(ringAngle * (4.0f * ringIdx));
                        if (notch > -0.3f)
                        {
                            r = MathF.Min(255.0f, r + 50.0f);
                            g = MathF.Min(255.0f, g + 230.0f);
                            b = MathF.Min(255.0f, b + 255.0f); // エメラルド・シアン
                        }
                    }
                }

                // 3. 中央のパルスエネルギーコア
                if (dist < coreRadius)
                {
                    float coreIntensity = 1.0f - (dist / coreRadius);
                    r = MathF.Min(255.0f, r + coreIntensity * 255.0f);
                    g = MathF.Min(255.0f, g + coreIntensity * 180.0f);
                    b = MathF.Min(255.0f, b + coreIntensity * 255.0f); // 高輝度パルス
                }

                // 4. サイバーHUD: 照準レティクル (クロスヘア)
                if (MathF.Abs(dx) < 1.0f && MathF.Abs(dy) < 120.0f && MathF.Abs(dy) > 20.0f)
                {
                    g = 255.0f; r = 100.0f; b = 255.0f;
                }
                if (MathF.Abs(dy) < 1.0f && MathF.Abs(dx) < 120.0f && MathF.Abs(dx) > 20.0f)
                {
                    g = 255.0f; r = 100.0f; b = 255.0f;
                }

                // 5. 下部: オーディオスペクトラム風のイコライザーバー
                if (y >= height - 120 && y <= height - 40)
                {
                    if (x % 24 == 0)
                    {
                        float barFreq = (x / 24) * 0.4f;
                        barHeight = MathF.Abs(MathF.Sin(barFreq + t * 6.0f) * MathF.Cos(barFreq * 0.5f + t * 3.0f)) * 70.0f;
                    }
                    float currentBarY = (height - 40) - y;

                    if (currentBarY <= barHeight && (x % 24) < 18)
                    {
                        // 下から上へのグラデーション
                        float ratio = currentBarY / 70.0f;
                        r = MathF.Min(255.0f, r + ratio * 255.0f);
                        g = MathF.Min(255.0f, g + (1.0f - ratio) * 220.0f);
                        b = MathF.Min(255.0f, b + 150.0f);
                    }
                }

                // 6. 左上: ステータスHUDボックス (半透明ガラスパネル風)
                if (x >= 40 && x <= 360 && y >= 40 && y <= 160)
                {
                    // 枠線
                    if (x == 40 || x == 360 || y == 40 || y == 160)
                    {
                        r = 80; g = 220; b = 255;
                    }
                    else
                    {
                        // 半透明背景
                        r = (r * 0.4f) + 15.0f;
                        g = (g * 0.4f) + 35.0f;
                        b = (b * 0.4f) + 60.0f;
                    }
                }

                // 7. 映画風スキャンライン (走査線) 効果
                if (y % 3 == 0)
                {
                    r *= 0.85f;
                    g *= 0.85f;
                    b *= 0.85f;
                }

                int idx = x * 4;
                row[idx] = (byte)r;
                row[idx + 1] = (byte)g;
                row[idx + 2] = (byte)b;
                row[idx + 3] = 255;
            }
        }
    }

    /// <summary>
    /// パターン1: 連続的な三角関数によるプラズマアニメーション
    /// </summary>
    internal static Vector3 PlasmaColumn(int x, int width, float time, float sinHalfTime, float offset)
    {
        float nx = (float)x / width;
        float cx = nx + offset - 0.5f;
        return new Vector3(MathF.Sin(nx * 10.0f + time * 2.0f), nx * sinHalfTime, cx * cx);
    }

    private static unsafe void RenderPlasma(byte* pBuf, int width, int height, int first, int end, float t, Vector3[]? columns)
    {
        float sinHalfTime = MathF.Sin(t / 2.0f);
        float offset = 0.5f * MathF.Sin(t / 5.0f);
        float cosThirdTime = MathF.Cos(t / 3.0f);
        for (int y = first; y < end; y++)
        {
            byte* row = pBuf + (y * width * 4);
            float ny = (float)y / height;
            float vertical = ny * cosThirdTime;
            float cy = ny + 0.5f * cosThirdTime - 0.5f;
            float cySquared = cy * cy;
            for (int x = 0; x < width; x++)
            {
                Vector3 column = columns == null ? PlasmaColumn(x, width, t, sinHalfTime, offset) : columns[x];
                float v1 = column.X;
                float v2 = MathF.Sin(10.0f * (column.Y + vertical) + t);
                float v3 = MathF.Sin(MathF.Sqrt(100.0f * (column.Z + cySquared) + 1.0f) + t * 3.0f);

                float v = (v1 + v2 + v3) / 3.0f;

                byte r = (byte)(MathF.Sin(v * MathF.PI) * 127.0f + 128.0f);
                byte g = (byte)(MathF.Cos(v * MathF.PI) * 127.0f + 128.0f);
                byte b = (byte)(MathF.Sin(v * MathF.PI + 2.0f * MathF.PI / 3.0f) * 127.0f + 128.0f);

                int idx = x * 4;
                row[idx] = r;
                row[idx + 1] = g;
                row[idx + 2] = b;
                row[idx + 3] = 255;
            }
        }
    }

    /// <summary>
    /// パターン2: ネオングリッドとバウンドする光球
    /// </summary>
    private const int GridSpacing = 60;
    private const uint GridLine = 0xffff7828;
    private const uint GridBackground = 0xff1e0f0f;

    internal static uint[] CreateGridRow(int width)
    {
        // The prefix also covers the negative remainders of a backwards scroll.
        var row = new uint[width + 2 * GridSpacing];
        for (int i = 0; i < row.Length; i++)
            row[i] = (i - GridSpacing) % GridSpacing < 2 ? GridLine : GridBackground;
        return row;
    }

    private static unsafe void RenderCyberGrid(byte* pBuf, int width, int height, int first, int end, float t, uint[] gridRow)
    {
        float ballX = (0.5f + 0.35f * MathF.Sin(t * 2.5f)) * width;
        float ballY = (0.5f + 0.35f * MathF.Abs(MathF.Sin(t * 3.5f))) * height;
        float radius = height * 0.12f;
        float radiusSq = MathF.Pow(radius, 2.0f);
        int left = Math.Max(0, (int)(ballX - radius) - 1);
        int right = Math.Min(width, (int)(ballX + radius) + 2);
        int scroll = (int)(t * 80.0f) % GridSpacing;
        var background = gridRow.AsSpan(GridSpacing + scroll, width);

        for (int y = first; y < end; y++)
        {
            var row = new Span<uint>((uint*)pBuf + y * width, width);
            bool horizontalLine = (y + scroll) % GridSpacing < 2;
            if (horizontalLine) row.Fill(GridLine);
            else background.CopyTo(row);

            float dy = y - ballY;
            float dySq = dy * dy;
            if (dySq >= radiusSq) continue;
            for (int x = left; x < right; x++)
            {
                float dx = x - ballX;
                float distSq = dx * dx + dySq;
                if (distSq >= radiusSq) continue;
                uint pixel = horizontalLine ? GridLine : background[x];
                float intensity = 1.0f - distSq / radiusSq;
                uint r = (byte)Math.Clamp((pixel & 255) + intensity * 255, 0, 255);
                uint g = (byte)Math.Clamp(((pixel >> 8) & 255) + intensity * 80, 0, 255);
                uint b = (byte)Math.Clamp(((pixel >> 16) & 255) + intensity * 200, 0, 255);
                row[x] = 0xff000000 | r | (g << 8) | (b << 16);
            }
        }
    }

    /// <summary>
    /// パターン3: レインボーグラデーション
    /// </summary>
    private static unsafe void RenderRainbowGradient(byte* pBuf, int width, int height, int first, int end, float t)
    {
        for (int y = first; y < end; y++)
        {
            byte* row = pBuf + (y * width * 4);
            float ny = (float)y / height;

            int x = Avx2.IsSupported && t >= 0 ? RenderRainbowVectors((uint*)row, width, ny, t) : 0;
            for (; x < width; x++)
            {
                float nx = (float)x / width;
                float phase = nx + ny + (t * 0.2f);
                float hue = phase - MathF.Truncate(phase);

                HsvToRgb(hue, 0.85f, 0.95f, out byte r, out byte g, out byte b);

                int idx = x * 4;
                row[idx] = r;
                row[idx + 1] = g;
                row[idx + 2] = b;
                row[idx + 3] = 255;
            }
        }
    }

    private static unsafe int RenderRainbowVectors(uint* row, int width, float ny, float time)
    {
        const float chroma = 0.95f * 0.85f;
        const float minimum = 0.95f - chroma;
        const int low = (int)(minimum * 255f);
        const int high = (int)((chroma + minimum) * 255f);

        // Each HSV sector has one high channel, one low channel, and one varying channel.
        var bases = Vector256.Create(high | low << 16, high << 8 | low << 16,
            low | high << 8, low | high << 16, low << 8 | high << 16, high | low << 8, 0, 0);
        var shifts = Vector256.Create(8, 0, 16, 8, 0, 16, 0, 0);
        var positions = Vector256.Create(0f, 1f, 2f, 3f, 4f, 5f, 6f, 7f);
        int x = 0;
        for (; x <= width - 8; x += 8)
        {
            var phase = positions / (float)width + Vector256.Create(ny) + Vector256.Create(time * 0.2f);
            var sector = (phase - Vector256.Truncate(phase)) * 6f;
            var indices = Vector256.ConvertToInt32Native(sector);
            var triangle = sector - Vector256.Truncate(sector * 0.5f) * 2f;
            var middle = Vector256.ConvertToInt32Native(((Vector256<float>.One - Vector256.Abs(triangle - Vector256<float>.One))
                * chroma + Vector256.Create(minimum)) * 255f);
            var packed = Avx2.PermuteVar8x32(bases, indices).AsUInt32() | Vector256.Create(0xff000000u);
            var offset = Avx2.PermuteVar8x32(shifts, indices).AsUInt32();
            (packed | Avx2.ShiftLeftLogicalVariable(middle.AsUInt32(), offset)).Store(row + x);
            positions += Vector256.Create(8f);
        }
        return x;
    }

    /// <summary>
    /// パターン4: SMPTE風カラーバー & 進行バー
    /// </summary>
    private static void RenderColorBars(Span<byte> rgba, int width, int height, int frame, float t)
    {
        Span<uint> pixels = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(rgba);
        int topRows = height - height / 16;
        Span<uint> bars = pixels[..(topRows * width)];
        FillColorBarRow(bars[..width]);
        RepeatRow(bars, width);

        Span<uint> progress = pixels[(topRows * width)..(width * height)];
        if (progress.IsEmpty) return;
        FillProgressRow(progress[..width], t);
        RepeatRow(progress, width);
    }

    internal static void FillColorBarRow(Span<uint> row)
    {
        ReadOnlySpan<uint> colors =
        [
            0xffc0c0c0, 0xff00c0c0, 0xffc0c000, 0xff00c000,
            0xffc000c0, 0xff0000c0, 0xffc00000
        ];
        int width = row.Length;
        int barWidth = width / 7;
        for (int i = 0; i < colors.Length; i++)
        {
            int start = i * barWidth;
            int end = i == colors.Length - 1 ? width : start + barWidth;
            row[start..end].Fill(colors[i]);
        }
    }

    internal const uint ProgressForeground = 0xff80ff00;
    internal const uint ProgressBackground = 0xff282828;
    internal static int ProgressSplit(int width, float time) =>
        Math.Clamp((int)((time % 5.0f) / 5.0f * width) + 1, 0, width);

    internal static void FillProgressRow(Span<uint> progress, float t)
    {
        int width = progress.Length;
        int split = ProgressSplit(width, t);
        progress[..split].Fill(ProgressForeground);
        progress[split..].Fill(ProgressBackground);
    }

    internal static void RepeatRow<T>(Span<T> pixels, int width)
    {
        for (int filled = width; filled < pixels.Length; filled *= 2)
        {
            int count = Math.Min(filled, pixels.Length - filled);
            pixels[..count].CopyTo(pixels[filled..]);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void HsvToRgb(float h, float s, float v, out byte r, out byte g, out byte b)
    {
        float c = v * s;
        float sector = h * 6.0f;
        float phase = sector - 2.0f * MathF.Truncate(sector * 0.5f);
        float x = c * (1.0f - MathF.Abs(phase - 1.0f));
        float m = v - c;

        float rf, gf, bf;
        int i = (int)(h * 6.0f);
        switch (i % 6)
        {
            case 0: rf = c; gf = x; bf = 0; break;
            case 1: rf = x; gf = c; bf = 0; break;
            case 2: rf = 0; gf = c; bf = x; break;
            case 3: rf = 0; gf = x; bf = c; break;
            case 4: rf = x; gf = 0; bf = c; break;
            default: rf = c; gf = 0; bf = x; break;
        }

        r = (byte)((rf + m) * 255.0f);
        g = (byte)((gf + m) * 255.0f);
        b = (byte)((bf + m) * 255.0f);
    }
}
