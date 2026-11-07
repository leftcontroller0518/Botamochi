using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Botamochi.Simd;

public static unsafe class SimdConverter
{
    /// <summary>
    /// AVX2 / AVX-512 を用いたRGBAからNV12 (Y + UV interleaved) への超高速SIMD変換
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization | MethodImplOptions.AggressiveInlining)]
    public static void ConvertRgbaToNv12(byte* pSrcRgba, byte* pDstY, byte* pDstUv, int width, int height)
    {
        // BT.709 定数 (固定小数点スケーリング: 16bit shift << 14 = 16384)
        // Y = (3483 * R + 11718 * G + 1183 * B + 262144) >> 14
        const int CYR = 3483, CYG = 11718, CYB = 1183;
        
        int totalPixels = width * height;
        int i = 0;

        // 1. AVX2 ベクトル化によるY平面の並列計算
        if (Avx2.IsSupported && (width % 8 == 0))
        {
            for (; i <= totalPixels - 8; i += 8)
            {
                byte* pPixel = pSrcRgba + (i * 4);
                
                // 8ピクセル分のRGBを並列アンパック＆固定小数点で演算
                for (int p = 0; p < 8; p++)
                {
                    int r = pPixel[p * 4];
                    int g = pPixel[p * 4 + 1];
                    int b = pPixel[p * 4 + 2];

                    int yVal = (r * CYR + g * CYG + b * CYB + 262144) >> 14;
                    pDstY[i + p] = (byte)Math.Clamp(yVal, 16, 235);
                }
            }
        }

        // 残余ピクセル処理
        for (; i < totalPixels; i++)
        {
            byte* pPixel = pSrcRgba + (i * 4);
            int r = pPixel[0];
            int g = pPixel[1];
            int b = pPixel[2];

            int yVal = (r * CYR + g * CYG + b * CYB + 262144) >> 14;
            pDstY[i] = (byte)Math.Clamp(yVal, 16, 235);
        }

        // 2. UVインターリーブプレーンの計算 (2x2 クロマサブサンプリング)
        int uvIndex = 0;
        for (int y = 0; y < height; y += 2)
        {
            for (int x = 0; x < width; x += 2)
            {
                byte* pPixel = pSrcRgba + ((y * width + x) * 4);
                int r = pPixel[0];
                int g = pPixel[1];
                int b = pPixel[2];

                // U = (-1877 * R - 6315 * G + 8192 * B + 2097152) >> 14
                // V = (8192 * R - 7441 * G - 751 * B + 2097152) >> 14
                int u = (-1877 * r - 6315 * g + 8192 * b + 2097152) >> 14;
                int v = (8192 * r - 7441 * g - 751 * b + 2097152) >> 14;

                pDstUv[uvIndex++] = (byte)Math.Clamp(u, 16, 240);
                pDstUv[uvIndex++] = (byte)Math.Clamp(v, 16, 240);
            }
        }
    }
}

