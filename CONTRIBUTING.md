# Contributing to Botamochi (貢献ガイドライン)

Botamochi プロジェクトへの関心と貢献に感謝します！  
本プロジェクトは、C# (.NET 10) を用いて **「GC非発生（Zero-Allocation）」「GPUコンピューティング」「SIMDベクトル化」「高スループット映像エンコード」** を極限まで追求した先進的映像レンダリングエンジンです。

高品質なコードベースと圧倒的なパフォーマンスを維持するため、以下の開発原則・コーディング規約・プルリクエストの手順を定めています。

---

## 1. コア開発哲学 (Core Engineering Philosophy)

Botamochi のすべての機能拡張や修正は、以下の3大原則に基づいて行われます：

1. **Zero-Allocation in Hot Paths (ホットパスにおけるアロケーション絶対禁止)**
   - レンダリングループやエンコードパイプライン（1フレームごとに呼ばれる処理）の中で、マネージドヒープへのアロケーション（GC Gen0/1/2 の発生要因）を行うことは **厳禁** です。
   - `new byte[]`、LINQ クエリ、ラムダ式による外部変数のキャプチャ（クロージャ）、ボクシング（`object` への暗黙変換）は一切使用しないでください。
   - すべてのフレームバッファは `MemoryPoolManager`（ネイティブアラインメントメモリプール）から借用・返却してください。

2. **Hardware-First Acceleration (ハードウェア最優先設計)**
   - 処理の主軸は GPU（DirectX 12 / ComputeSharp）に委ね、CPU側は非同期スケジューリングとオーケストレーションに専念します。
   - CPU側で画像・ピクセル処理を行う場合は、必ず `System.Runtime.Intrinsics` (AVX2 / AVX-512) を用いたSIMDベクトル演算を実装してください。

3. **Deterministic Synchronization (確実な同期とバックプレッシャー)**
   - スレッド同期には `System.Threading.Channels` を用い、過剰なメモリバッファリングを防ぐためのバックプレッシャー制御（`BoundedChannelFullMode.Wait`）を維持してください。
   - タイムスタンプは `Stopwatch.Frequency` ベースの高分解能クロックを用いて算出し、リップシンク（音ズレ）破綻を防止してください。

---

## 2. コーディング規約 (Coding Standards)

### メモリとアンマネージド操作
- バッファの受け渡しには `Span<T>`, `ReadOnlySpan<T>`, `Memory<T>`, `ReadOnlyMemory<T>` を使用してください。
- 非同期メソッドでアロケーションを抑えるため、即時完了の可能性があるホットパスでは `Task` ではなく `ValueTask` を使用してください。
- ネイティブメモリの確保・解放には `NativeMemory.AlignedAlloc` / `AlignedFree` を用い、64バイト境界（AVX-512キャッシュライン）を保ってください。

### GPU コンピュート (ComputeSharp)
- コンピュートシェーダー構造体には必ず `[ThreadGroupSize]` 属性を付与し、事前コンパイル（AOT/Build-time Compilation）を可能にしてください。
- シェーダーフィールドには HLSL でサポートされている型（`Float4`, `Float3`, `int`, `uint` 等）のみを使用してください。

### ローレベルグラフィックス (Silk.NET.Direct3D12)
- COM インターフェース（`ID3D12Resource*`, `ID3D12Device*` 等）は、スコープ終了時または `Dispose()` 時に必ず明示的に `Release()` を呼び出してください。
- COM オブジェクトのアドレス取得時には、ポインタのピン留め・ローカル変数退避を徹底し、未定義動作を防いでください。

### SIMD ベクトル化 (System.Runtime.Intrinsics)
- SIMD 命令を呼び出す前に、必ず `Avx2.IsSupported` や `Avx512F.IsSupported` 等のハードウェアサポート確認を行ってください。
- ベクトル幅（256bit / 512bit）に満たない余剰ピクセルは、スカラー演算によるフォールバック処理を必ず記述してください。

---

## 3. 開発環境のセットアップ

### 必要要件
- **OS**: Windows 10 / 11 (64-bit)
- **SDK**: .NET 10.0 SDK 以降
- **IDE**: Visual Studio 2026 / Visual Studio Code (C# Dev Kit 拡張機能) / JetBrains Rider
- **GPU**: DirectX 12 対応 GPU
- **外部ツール**: `ffmpeg.exe` (PATH環境変数に通しておくことを推奨)

### リポジトリのクローンとビルド

```powershell
git clone https://github.com/leftcontroller0518/botamochi.git
cd botamochi/src
dotnet build -c Release
```

### 実行テスト

```powershell
dotnet run -c Release --no-build
```

---

## 4. プルリクエスト (Pull Request) の提出手順

1. **Issue の確認 / 作成**:
   大規模な設計変更や新規機能の追加を行う場合は、事前に Issue を作成して設計方針を議論してください。
2. **トピックブランチの作成**:
   `main` ブランチから分岐したわかりやすいブランチ名を作成します（例: `feature/avx512-chroma-key`, `fix/nvenc-deadlock`）。
3. **ローカル検証**:
   - `dotnet build -c Release` で警告・エラーが「0」であることを確認してください。
   - `dotnet run -c Release` を実行し、パイプラインがデッドロックせずに完了し、出力された動画ファイルが破損していないことを確認してください。
   - プロファイラ（dotTrace や Visual Studio 診断ツール）を使用し、ホットパス内で GC アロケーションが発生していないことを確認してください。
4. **コミットメッセージ**:
   変更意図が明確に伝わる簡潔なメッセージを記述してください（Conventional Commits 形式を推奨）。
5. **PR の提出**:
   変更内容、ベンチマーク結果（FPSやメモリ使用状況の改善等）、テスト結果をPR本文に明記してください。

---

## 5. 行動規範 (Code of Conduct)

すべてのコントリビューターおよび利用者が快適かつ建設的に協力できるよう、お互いを尊重したコミュニケーションを心がけてください。技術的な議論においては、客観的なベンチマークデータやプロファイリング結果に基づく議論を歓迎します。

---

## 6. お問い合わせ / サポート

質問やアイデア、アーキテクチャに関するディスカッションは、GitHub の [Issues](https://github.com/leftcontroller0518/botamochi/issues) または [Discussions](https://github.com/leftcontroller0518/botamochi/discussions) にてお気軽にお寄せください。

