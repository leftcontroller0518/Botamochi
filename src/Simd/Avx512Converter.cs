using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace Botamochi.Simd;

/// <summary>
/// AVX-512 / AVX2 ハードウェア Intrinsics を極限まで駆使した超並列色空間変換エンジン
/// </summary>
public static unsafe class Avx512Converter
{
    // BT.709 規格 固定小数点係数 (16bit shift << 14 = 16384)
    private const short CYR = 3483;
    private const short CYG = 11718;
    private const short CYB = 1183;
    private const int Y_OFFSET = 262144;

    /// <summary>
    /// AVX-512 (512bit) または AVX2 (256bit) を自動判別し、RGBAからNV12へ超並列変換
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization | MethodImplOptions.AggressiveInlining)]
    public static void ConvertRgbaToNv12Vectorized(byte* pSrcRgba, byte* pDstY, byte* pDstUv, int width, int height)
    {
        int totalPixels = width * height;
        int i = 0;

        if (System.Runtime.Intrinsics.X86.Avx512BW.IsSupported)
        {
            for (; i <= totalPixels - 64; i += 64)
            {
                uint* rgba = (uint*)pSrcRgba + i;
                var lo = Vector512.Narrow(Luma512(rgba), Luma512(rgba + 16));
                var hi = Vector512.Narrow(Luma512(rgba + 32), Luma512(rgba + 48));
                Vector512.Narrow(lo, hi).Store(pDstY + i);
            }
        }
        if (Vector256.IsHardwareAccelerated)
        {
            for (; i <= totalPixels - 32; i += 32)
            {
                uint* rgba = (uint*)pSrcRgba + i;
                var lo = Vector256.Narrow(Luma(rgba), Luma(rgba + 8));
                var hi = Vector256.Narrow(Luma(rgba + 16), Luma(rgba + 24));
                Vector256.Narrow(lo, hi).Store(pDstY + i);
            }
        }

        // 残余ピクセルのスカラー処理
        for (; i < totalPixels; i++)
        {
            byte* pPixel = pSrcRgba + (i * 4);
            int r = pPixel[0];
            int g = pPixel[1];
            int b = pPixel[2];

            int yVal = (r * CYR + g * CYG + b * CYB + Y_OFFSET) >> 14;
            pDstY[i] = (byte)Math.Clamp(yVal, 16, 235);
        }

        // 3. UV平面の2x2クロマサブサンプリング (インターリーブ)
        int uvIndex = 0;
        for (int y = 0; y < height; y += 2)
        {
            for (int x = 0; x < width; x += 2)
            {
                byte* pPixel = pSrcRgba + ((y * width + x) * 4);
                int r = pPixel[0];
                int g = pPixel[1];
                int b = pPixel[2];

                int u = (-1877 * r - 6315 * g + 8192 * b + 2097152) >> 14;
                int v = (8192 * r - 7441 * g - 751 * b + 2097152) >> 14;

                pDstUv[uvIndex++] = (byte)Math.Clamp(u, 16, 240);
                pDstUv[uvIndex++] = (byte)Math.Clamp(v, 16, 240);
            }
        }
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<uint> Luma(uint* rgba)
    {
        var pixels = Vector256.Load(rgba);
        var mask = Vector256.Create(255u);
        var r = pixels & mask;
        var g = (pixels >> 8) & mask;
        var b = (pixels >> 16) & mask;
        var y = (r * (uint)CYR + g * (uint)CYG + b * (uint)CYB + Vector256.Create((uint)Y_OFFSET)) >> 14;
        return Vector256.Min(y, Vector256.Create(235u));
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector512<uint> Luma512(uint* rgba)
    {
        var pixels = Vector512.Load(rgba);
        var mask = Vector512.Create(255u);
        var r = pixels & mask;
        var g = (pixels >> 8) & mask;
        var b = (pixels >> 16) & mask;
        var y = (r * (uint)CYR + g * (uint)CYG + b * (uint)CYB + Vector512.Create((uint)Y_OFFSET)) >> 14;
        return Vector512.Min(y, Vector512.Create(235u));
    }
}
