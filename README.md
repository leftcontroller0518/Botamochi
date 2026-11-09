# Botamochi
JP, [ENG](README-eng.md), [CN](README-zh.md)

汎用C#映像レンダラー

# 概要
Botamochiは、C#用の映像レンダリング＆ハードウェアエンコード・エンジンです。

## 詳細技術仕様

### 1. Zero-Allocation メモリ設計 (GC非発生アーキテクチャ)
- **128バイトアラインメント・ネイティブリング**:
  `NativeMemory.AlignedAlloc` で全フレームを起動時に一括確保します。単一生産者の循環インデックスで再利用し、フレームごとの確保、解放、除算、原子的インクリメントを行いません。
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

### 4. 固定長SPSCリングによるバックプレッシャー同期
- **単一生産者・単一消費者パイプライン**:
  GPUレンダリング側とFFmpeg書き込み側を事前確保した配列で接続します。読み書き位置はそれぞれのスレッドだけが更新し、空きがある高速経路ではカーネル待機を行いません。
- **割り当てのない背圧制御**:
  キューが満杯または空のときだけ再利用可能なイベントで待機します。Task、Channelノード、フレームメタデータを毎フレーム生成しません。

### 5. 固定フレームレート出力
- FFmpeg入力に指定したフレームレートと投入順からタイムスタンプを生成します。Direct複数セッションでもフレーム区間を順番に結合してからMP4へ格納します。

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
[ Native Frame Ring ] ◄── (Zero-Allocation 128-byte AlignedAlloc)
                    │ (Pooled Frame Payload)
                    ▼
[ SPSC Ring (EncoderPipeline) ] ── (Allocation-free Backpressure)
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
    ├── RenderOptions.cs            # CLI解析と入力検証
    ├── Core/
    │   └── NativeRingBufferPool.cs # 事前確保ネイティブリング
    ├── Gpu/
    │   ├── GpuComputeKernel.cs     # ComputeSharp (DirectX 12 Compute Shader)
    │   └── RenderPipeline.cs       # Silk.NET Direct3D 12 オフスクリーン描画
    ├── Simd/
    │   └── SimdConverter.cs        # AVX2/AVX-512 SIMD色空間変換エンジン
    └── Encoder/
        ├── EncoderPipeline.cs      # SPSCリングとバックプレッシャー
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

全オプションは `dotnet run -c Release --no-build -- --help` で確認できます。未知のオプションや範囲外の値は描画開始前に終了コード2で報告します。

実行が完了すると、`render_output.mp4` が出力されます。

---

Native AOT版のビルド、D3D11共有テクスチャ経由のNVENC出力、複数セッション、PGOの再現手順は [Windows高速レンダリング経路](native/README.md) に記載しています。

## ベンチマーク & パフォーマンス

GeForce RTX 3080 Ti Laptop GPU、NVENC、Releaseビルドで、1920×1080・60fps・3秒（180フレーム）のMP4を書き出しました。表は変更前後を交互に各3回実行した中央値です。完了時間はプロセス起動からFFmpegの終了までを含みます。

| パターン | 変更前の完了時間 | 変更後の完了時間 | レンダリングループの平均FPS（変更前 → 変更後） |
| --- | ---: | ---: | ---: |
| `bars` | 2.49秒 | 0.94秒 | 109.0 → 337.9 |
| `plasma`（既定） | 1.55秒 | 1.52秒 | 262.9 → 275.4 |

`src` での実行例：

```powershell
dotnet run -c Release --no-build -- --pattern bars --duration 3 --output bars.mp4
```

カラーバーは同じ内容の行をNV12で複製します。プラズマのGPU経路は輝度と色差の計算でRGB値を共有し、ネイティブフレームバッファに読み戻します。幅と高さには偶数を指定してください。幅が4の倍数でない場合はCPUで処理します。

---

## コントリビューション

プルリクエストや Issue の報告は大歓迎です。開発原則やコーディング規約の詳細は [CONTRIBUTING.md](CONTRIBUTING.md) をご確認ください。

## ライセンス

本プロジェクトは [Apache License 2.0](LICENSE) のもとで公開されています。  
Copyright (c) 2026 leftcontroller
