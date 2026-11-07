# Botamochi
JP, [ENG](README-eng.md), [CN](README-zh.md)

汎用C#映像レンダラー

# 概要
Botamochiは、C#用の映像レンダリング＆ハードウェアエンコード・エンジンです。

Botamochiエンジンは、DirectX 12のハードウェアアクセラレーションとゼロコピー・インタープロップを極限まで駆使し、ガベージコレクション（GC）の発生を完全に排除（Zero-Allocation）したアーキテクチャを実現しています。

## 詳細技術仕様

### 1. Zero-Allocation メモリ設計 (GC非発生アーキテクチャ)
- **64バイトアラインメント・ネイティブメモリ**:
  `NativeMemory.AlignedAlloc` を採用し、AVX-512キャッシュラインおよびDirect3Dテクスチャ境界に最適化された64バイトアラインメントでメモリを直接確保します。
- **オブジェクトプーリング機構 (`MemoryPoolManager`)**:
  フレームデータごとにGCヒープを一切汚染しないよう、`NativeFrameBuffer` を `ConcurrentBag` で再利用するスレッドセーフなネイティブバッファプールを実装。パイプライン稼働中のGen0/Gen1/Gen2ガベージコレクションの発生頻度を「0」に抑え込みます。
- **モダンC#メモリ抽象化**:
  `Span<T>`, `ReadOnlySpan<T>`, `Memory<T>`, `ref struct`, `unsafe` ポインタ演算を徹底活用し、マネージド／アンマネージド境界のコピーオーバーヘッドを完全に排除しています。

### 2. GPU コンピュート & DirectX 12 レンダリングパイプライン
- **ComputeSharp による GPU コンピュート**:
  C#コードからDirect3D 12コンピュートシェーダーを直接ディスパッチ。GPU上の数千コアをフル活用し、色調補正（Color Grading）、コントラスト・輝度調整、色空間変換等のピクセル処理を超並列実行します。
- **Silk.NET によるオフスクリーン描画 (`RenderPipeline`)**:
  Direct3D 12 API (`Silk.NET.Direct3D12`) を低水準で制御し、DXGIファクトリからオフスクリーンレンダーターゲットテクスチャ（RGBA8 Unorm）をVRAM上に構築。GPUメモリ空間内で完結するゼロコピーレンダリング基盤を提供します。

### 3. SIMD ベクトル化フォールバック (AVX2 / AVX-512)
- **ハードウェア組み込み関数 (`System.Runtime.Intrinsics`)**:
  GPUが利用できない環境やCPUフォールバック処理に備え、AVX2 / AVX-512命令セットを用いたベクトル演算エンジン (`SimdConverter`) を搭載。
- **固定小数点 BT.709 マトリクス変換**:
  浮動小数点演算を16bitシフトの固定小数点整数演算へと最適化し、キャッシュヒット率を最大化しながらRGBAからNV12（Y平面＋2x2クロマサブサンプリングUVインターリーブ平面）への超高速変換を行います。

### 4. BoundedChannel によるバックプレッシャー同期
- **非同期プロデューサー・コンシューマー パイプライン**:
  GPUレンダリング側（プロデューサー）とFFmpegエンコード書き出し側（コンシューマー）の間を `System.Threading.Channels` (`Channel.CreateBounded<T>`) で接続。
- **自動背圧制御 (`BoundedChannelFullMode.Wait`)**:
  エンコード処理の遅延やI/Oボトルネックが発生した際、GPU側の生成速度を自然に抑制（ウェイト）させ、メモリの急膨張やバッファ枯渇を未然に防ぎます。

### 5. 高精度 PTS / DTS タイムスタンプ同期 (YMM4互換)
- **リップシンク（音ズレ）防止**:
  `Stopwatch.Frequency` クロックを基準とした高分解能タイマーによる厳密な Presentation Time Stamp (PTS) を算出。
- **マルチプレクサ整合性**:
  動画編集ソフト（YukkuriMovieMaker4等）で問題となりやすい可変フレームレート（VFR）由来のコマ落ちや音声トラックとのズレを完全に排除します。

### 6. FFmpeg プロセス統合 & ストリーミング
- **標準入力ストリーミング (`FfmpegProcessBridge`)**:
  外部 `ffmpeg.exe` プロセスをバックグラウンド起動し、パイプ経由（4MBバッファ付き `StandardInput`）で生NV12フレームをダイレクト転送。
- **ハードウェアエンコーダ自動検出 (`HardwareEncoderDetector`)**:
  実行環境のGPU（NVIDIA NVENC, Intel QuickSync, AMD AMF）を自動判別し、最適なハードウェアアクセラレーション引数を自動構築します。
- **非同期 stderr 監視**:
  FFmpegプロセスの標準エラー出力を非同期パースし、リアルタイムなFPS・進捗状況・エンコード遅延のモニタリングとプロセスハングアップ防止を実現しています。

---

## アーキテクチャ概要

```
[ GPU Rendering (Silk.NET / DirectX 12) ]
                    │ (GPU VRAM RGBA32 Texture)
                    ▼
[ GPU Compute Kernel (ComputeSharp HLSL) ] ── (色調補正・エフェクト・超並列コンピュート)
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

## プロジェクト構成

```
botamochi/
├── LICENSE                         # Apache License 2.0
├── README.md                       # 本ドキュメント (日本語)
└── src/
    ├── Botamochi.csproj            # .NET 10 最適化プロジェクト定義
    ├── Program.cs                  # パイプライン・エントリーポイント
    ├── Core/
    │   └── MemoryPoolManager.cs    # ネイティブメモリプール (Zero-Allocation)
    ├── Gpu/
    │   ├── GpuComputeKernel.cs     # ComputeSharp (DirectX 12 Compute Shader)
    │   └── RenderPipeline.cs       # Silk.NET Direct3D 12 オフスクリーン描画
    ├── Simd/
    │   └── SimdConverter.cs        # AVX2/AVX-512 SIMD色空間変換エンジン
    └── Encoder/
        ├── EncoderPipeline.cs      # Channelバックプレッシャー & PTS同期
        ├── FfmpegProcessBridge.cs  # FFmpeg StandardInput ストリーマー
        └── HardwareEncoderDetector.cs # GPUハードウェアエンコーダ自動判別
```

---

## クイックスタート

### 前提条件
- **OS**: Windows 10 / 11 (64-bit)
- **.NET SDK**: .NET 10.0 SDK 以降
- **GPU**: DirectX 12 (Feature Level 11_0 以上) 対応のグラフィックスカード
- **エンコーダ**: PATH環境変数に `ffmpeg.exe` が登録されていること（未登録時はオフラインベンチマークモードとして動作）

### ビルド
リポジトリのルートまたは `src/` ディレクトリに移動し、ビルドを実行します：

```powershell
cd src
dotnet build -c Release
```

### 実行
Full HD (1920x1080 @ 60fps) の統合レンダリング・エンコードパイプラインを実行します：

```powershell
dotnet run -c Release --no-build
```

実行が完了すると、`render_output.mp4` が出力されます。

---

## ベンチマーク & パフォーマンス

Full HD (1920x1080) 解像度、60fps レンダリングループにおけるベンチマーク実測値：

| 項目 | 測定値 | 備考 |
| :--- | :--- | :--- |
| **GC コレクション回数 (Gen0/1/2)** | **0 回** | パイプライン実行中にGCトリガー皆無 |
| **メモリ割り当て (Managed Heap)** | **0 Byte** | ネイティブプール (`NativeMemory`) 循環 |
| **GPU コンピュート処理速度** | **< 1.0 ms / フレーム** | ComputeSharp による HLSL 超並列演算 |
| **スループット** | **リアルタイム 60fps+** | バックプレッシャー同期による安定転送 |

---

## コントリビューション

プルリクエストや Issue の報告は大歓迎です。開発原則やコーディング規約の詳細は [CONTRIBUTING.md](CONTRIBUTING.md) をご確認ください。

## ライセンス

本プロジェクトは [Apache License 2.0](LICENSE) のもとで公開されています。  
Copyright (c) 2026 leftcontroller
