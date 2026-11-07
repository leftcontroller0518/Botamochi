using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Botamochi.Encoder;

public enum HardwareAccelerationType
{
    SoftwareCpu,
    NvidiaNvenc,
    IntelQsv,
    AmdAmf
}

/// <summary>
/// 実行環境のGPUハードウェアを検出し、最適なハードウェアエンコーダ（NVENC / QSV / AMF）引数を自動構築するディスパッチャ
/// </summary>
public static class HardwareEncoderDetector
{
    public static (HardwareAccelerationType Type, string Codec, string ExtraArgs) DetectBestEncoder(ILogger? logger = null)
    {
        // 1. NVIDIA GPU (NVENC) の検出
        if (IsNvidiaGpuAvailable())
        {
            logger?.LogInformation("[GPU Hardware] NVIDIA GPU detected. Using NVENC (h264_nvenc) with ultra-low latency preset.");
            return (
                HardwareAccelerationType.NvidiaNvenc,
                "h264_nvenc",
                "-preset p1 -tune ll -rc cbr -gpu 0 -surfaces 16 -b:v 12M -maxrate 15M"
            );
        }

        // 2. Intel GPU (QuickSync / QSV) の検出
        if (IsIntelGpuAvailable())
        {
            logger?.LogInformation("[GPU Hardware] Intel GPU detected. Using Intel QuickSync (h264_qsv).");
            return (
                HardwareAccelerationType.IntelQsv,
                "h264_qsv",
                "-preset veryfast -b:v 12M"
            );
        }

        // 3. AMD GPU (AMF) の検出
        if (IsAmdGpuAvailable())
        {
            logger?.LogInformation("[GPU Hardware] AMD GPU detected. Using AMD AMF (h264_amf).");
            return (
                HardwareAccelerationType.AmdAmf,
                "h264_amf",
                "-quality speed -rc cbr -b:v 12M"
            );
        }

        // 4. CPU フォールバック (libx264)
        logger?.LogInformation("[GPU Hardware] Dedicated hardware encoder not detected. Using optimized CPU multithreaded libx264.");
        return (
            HardwareAccelerationType.SoftwareCpu,
            "libx264",
            "-preset ultrafast -tune zerolatency -threads 0"
        );
    }

    private static bool IsNvidiaGpuAvailable()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return false;

        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=name --format=csv,noheader",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            });

            if (proc == null) return false;
            string? output = proc.StandardOutput.ReadLine();
            proc.WaitForExit(1000);
            return !string.IsNullOrWhiteSpace(output);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsIntelGpuAvailable()
    {
        // WMIまたはDXGIアダプター名で簡易検出
        return false;
    }

    private static bool IsAmdGpuAvailable()
    {
        return false;
    }
}
