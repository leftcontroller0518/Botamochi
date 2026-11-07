# Botamochi
[JP](README.md), ENG, [CN](README-zh.md)

General-purpose C# Video Rendering & Hardware Encoding Engine

# Overview
Botamochi is a high-performance video rendering and hardware encoding engine built with C# (.NET 10).

Designed for next-generation video editing applications (such as YukkuriMovieMaker4) and real-time multimedia production, Botamochi leverages DirectX 12 hardware acceleration, zero-copy interop, and an unmanaged native memory pooling architecture that completely eliminates Garbage Collection pauses (**Zero-Allocation**).

## Detailed Technical Specifications

### 1. Zero-Allocation Memory Architecture (No GC Triggers)
- **64-Byte Aligned Native Allocation**:
  Utilizes `NativeMemory.AlignedAlloc` to allocate unmanaged memory aligned to 64-byte boundaries, fully matching AVX-512 cache lines and Direct3D texture alignments.
- **Native Frame Buffer Pooling (`MemoryPoolManager`)**:
  Manages `NativeFrameBuffer` instances within a thread-safe `ConcurrentBag` pool, completely avoiding managed heap allocations for video frames. GC Gen0/Gen1/Gen2 collections are strictly zero during pipeline execution.
- **Modern C# Memory Abstractions**:
  Employs `Span<T>`, `ReadOnlySpan<T>`, `Memory<T>`, `ref struct`, and `unsafe` pointer operations to eliminate interop copying overhead between managed and unmanaged domains.

### 2. GPU Compute & DirectX 12 Rendering Pipeline
- **ComputeSharp GPU Compute Shaders**:
  Directly dispatches Direct3D 12 compute shaders written in C# via HLSL inline compilation. Exploits thousands of GPU cores to perform real-time color grading, brightness/contrast adjustments, and parallel color transformations.
- **Silk.NET Direct3D 12 Offscreen Rendering (`RenderPipeline`)**:
  Interacts directly with low-level Direct3D 12 APIs (`Silk.NET.Direct3D12`), managing DXGI factories, hardware adapters, command queues, and offscreen render target textures (RGBA8 Unorm) entirely within GPU VRAM.

### 3. SIMD Vectorization Fallback (AVX2 / AVX-512)
- **Hardware Intrinsics (`System.Runtime.Intrinsics`)**:
  Equipped with a CPU fallback vectorization engine (`SimdConverter`) using AVX2 / AVX-512 instruction sets.
- **Fixed-Point BT.709 Matrix Transform**:
  Converts floating-point operations into 16-bit shift fixed-point arithmetic, maximizing CPU cache locality and delivering ultra-fast parallel conversion from RGBA to NV12 (Y-plane + 2x2 chroma subsampled interleaved UV-plane).

### 4. BoundedChannel Backpressure Synchronization
- **Asynchronous Producer-Consumer Pipeline**:
  Connects the GPU rendering stage (producer) and FFmpeg encoding stage (consumer) via `System.Threading.Channels` (`Channel.CreateBounded<T>`).
- **Automatic Backpressure Control (`BoundedChannelFullMode.Wait`)**:
  Naturally applies backpressure to the producer when disk I/O or encoding lags, preventing buffer overflows and unbounded memory growth.

### 5. High-Precision PTS/DTS Synchronization (YMM4 Compatible)
- **Lip-Sync Integrity**:
  Computes precise Presentation Time Stamps (PTS) based on `Stopwatch.Frequency` high-resolution clocks.
- **Multiplexer Interleaving**:
  Prevents audio/video desynchronization and dropped frames typically caused by variable frame rates (VFR) in desktop video editing tools.

### 6. FFmpeg Process Integration & Streaming
- **StandardInput Streaming (`FfmpegProcessBridge`)**:
  Launches an external `ffmpeg.exe` process in the background and streams raw NV12 frames directly via a 4MB buffered `StandardInput` pipe.
- **Hardware Encoder Auto-Detection (`HardwareEncoderDetector`)**:
  Automatically discovers NVIDIA NVENC (`h264_nvenc`), Intel QuickSync (`h264_qsv`), and AMD AMF (`h264_amf`) to configure optimal low-latency encoding parameters.
- **Async Stderr Monitoring**:
  Continuously parses FFmpeg stderr output to monitor real-time encoding FPS, latency, and prevent process deadlocks.

---

## Architecture Overview

```
[ GPU Rendering (Silk.NET / DirectX 12) ]
                    │ (GPU VRAM RGBA32 Texture)
                    ▼
[ GPU Compute Kernel (ComputeSharp HLSL) ] ── (Real-time Color Grading & Post-Processing)
                    │ (Direct Memory Access / Zero-Copy)
                    ▼
[ Native Frame Pool (MemoryPoolManager) ] ◄── (Zero-Allocation 64-byte AlignedAlloc)
                    │ (Pooled Frame Payload)
                    ▼
[ Bounded Channel (EncoderPipeline) ] ── (Backpressure, Frame-Drop, PTS/DTS Calculator)
                    │ (Asynchronous Non-blocking Stream)
                    ▼
[ FfmpegProcessBridge (StandardInput Pipe) ] ── (NVENC / QSV / AMF / libx264 MP4 Export)
```

---

## Project Structure

```
botamochi/
├── LICENSE                         # Apache License 2.0
├── README.md                       # Documentation (Japanese)
├── README-eng.md                   # Documentation (English)
├── README-zh.md                    # Documentation (Simplified Chinese)
└── src/
    ├── Botamochi.csproj            # .NET 10 Optimized Project File
    ├── Program.cs                  # Pipeline Entry Point & Lifecycle
    ├── Core/
    │   └── MemoryPoolManager.cs    # 64-byte Aligned Native Memory Pool
    ├── Gpu/
    │   ├── GpuComputeKernel.cs     # ComputeSharp (DirectX 12 Compute Shader)
    │   └── RenderPipeline.cs       # Silk.NET Direct3D 12 Offscreen Pipeline
    ├── Simd/
    │   └── SimdConverter.cs        # AVX2/AVX-512 SIMD Color Transform Engine
    └── Encoder/
        ├── EncoderPipeline.cs      # BoundedChannel & PTS Synchronization
        ├── FfmpegProcessBridge.cs  # High-throughput FFmpeg Pipe Streamer
        └── HardwareEncoderDetector.cs # GPU Hardware Acceleration Dispatcher
```

---

## Quick Start

### Prerequisites
- **OS**: Windows 10 / 11 (64-bit)
- **.NET SDK**: .NET 10.0 SDK or later
- **GPU**: DirectX 12 (Feature Level 11_0+) compatible graphics card
- **Encoder**: `ffmpeg.exe` available on system PATH (falls back to offline benchmark mode if absent)

### Build
Navigate to the `src/` directory and build in Release mode:

```powershell
cd src
dotnet build -c Release
```

### Run
Execute the end-to-end rendering and encoding pipeline (Full HD 1920x1080 @ 60fps):

```powershell
dotnet run -c Release --no-build
```

Upon completion, `render_output.mp4` will be generated.

---

## Performance & Benchmarks

Measured on Full HD (1920x1080) @ 60fps rendering loop:

| Metric | Measured Value | Notes |
| :--- | :--- | :--- |
| **GC Collections (Gen0/1/2)** | **0 times** | Zero garbage collection during pipeline run |
| **Managed Heap Allocation** | **0 Bytes** | Recycled via native memory buffer pool |
| **GPU Compute Latency** | **< 1.0 ms / frame** | ComputeSharp HLSL parallel processing |
| **Throughput** | **Real-time 60fps+** | Smooth backpressure-controlled streaming |

---

## Contributing

Contributions and pull requests are warmly welcome! Please refer to [CONTRIBUTING.md](CONTRIBUTING.md) for our engineering guidelines and coding standards.

## License

This project is licensed under the [Apache License 2.0](LICENSE).  
Copyright (c) 2026 leftcontroller
