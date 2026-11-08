# Windows高速レンダリング経路

Windows x64、.NET 10 SDK、Visual StudioのC++デスクトップ開発ツール、Windows SDK（dxc.exe）、CMake、Git、FFmpegを使います。Direct経路にはNVENC API 12.1、共有NV12サーフェス、Shader Model 6のWave演算に対応するNVIDIA GPUとドライバーが必要です。以下はリポジトリのルートからPowerShellで実行します。

```powershell
cmake -S native -B out/native -A x64
cmake --build out/native --config Release
$dll = (Resolve-Path out/native/Release/BotamochiNvenc.dll).Path
$wave = (Resolve-Path out/native/PackWave.dxil).Path
dotnet publish src/Botamochi.csproj -r win-x64 -c Release -p:PublishProfile=NativeAot "-p:NvencLibrary=$dll" "-p:WaveShader=$wave" -o out/publish
out/publish/Botamochi.exe --direct -p plasma -d 5 -o plasma.mp4
```

初回のCMake構成でバージョン固定のNVENCヘッダーを取得します。オフラインの場合は同じコミットのチェックアウトを `-DFETCHCONTENT_SOURCE_DIR_NVHEADERS=<絶対パス>` で指定できます。発行フォルダーには実行ファイルとDLLを一緒に配置してください。

圧縮による画質劣化を避けたい場合は `--lossless` を指定してください。Direct経路でNVENCの可逆チューニングとQP 0を使用します。ビットレートは可変で、出力サイズは通常の12 Mbps設定より大きくなります。

```powershell
out/publish/Botamochi.exe --lossless -p cyber3d -d 5 -o cyber3d.mp4
```

## 30項目の実装と適用範囲

| 番号 | 経路と動作 |
| --- | --- |
| 1・23・24 | `--direct` でD3D11が作成したNV12テクスチャをD3D12で開き、NVENCに1回登録します。GPUがY/UV平面に直接書き込み、CPUは圧縮済みパケットをFFmpegへ渡します。プラズマ・レインボー・カラーバーはGPU上で生成します。グリッドとCyber3DはGPUのマップ済みアップロード領域にCPUでRGBAを描画します。 |
| 2・26 | `NativeAot` プロファイルでwin-x64の自己完結型バイナリを発行します。ILの全トリミング、速度優先コンパイル、ネイティブDLLのReleaseリンク時最適化を使用します。 |
| 3・11・12・13・27 | 通常経路はCPU描画用のネイティブアリーナまたはGPUのマップ済み読み戻しリングを使います。各スロットは128バイト境界です。キューは最大8枚で、書き込み中と生成中に各1枚を確保します。プラズマの一括描画では先行フレーム用に最大3枚を追加します。高解像度では96 MiBを目安にキューを縮め、最低3スロットを維持します。CPUの描画ジョブも再利用します。GCや外部ライブラリまで含む完全なゼロ割り当ては保証していません。 |
| 4・15・20・22 | プラズマはシーン評価とNV12出力を1ディスパッチに融合します。Direct経路は32画素×8行の帯で共有テクスチャを処理します。ComputeSharp用の `PackWave.dxil` はRGBAの隣接レーンからY/UVを集めてパックします。対応するWave幅と埋め込みDXILが揃ったときに有効です。両方のHLSLをビルド時にコンパイルします。ドライバーのパイプライン初期化は起動時にあります。 |
| 5・14・29 | Direct経路は各入力サーフェスのコマンドリストを初期化時に1回記録します。NVENC登録済みサーフェスを渡す時点でD3D12フェンスの完了値を確認し、未完了のときだけ待ちます。圧縮結果の取得完了後にサーフェスを再利用します。同時アクセス可能なテクスチャには暗黙のステート昇格と降格を使用します。 |
| 6 | 通常経路は単一生産者・単一消費者の固定長リングを使用します。空きがある間はインデックスだけを公開し、満杯または空のときだけ再利用可能なイベントで待機します。フレームごとのTaskとキューノードは生成しません。書き込み失敗は生産者側にも伝わります。 |
| 7・30 | 既定の標準入力に加え `--named-pipe` で現在のユーザーに限定した名前付きパイプを選べます。`--pipe-bytes 1048576` で名前付きパイプの送信バッファ、標準入力の場合は追加のバッファリング量を指定できます。 |
| 8・9 | Server GCとJITのTiered PGOが有効です。`--no-gc-mb 64` でGC抑止領域の予算を指定できます。AOTのMIBC入力は `-p:PgoProfile=<絶対パス>`、ネイティブDLLの実測PGOは下記を使用します。 |
| 10・21・25 | `--realtime-priority` はWindowsにプロセスのRealTime優先度を要求します。`--numa 0 --physical-cores` はエンコードワーカーを指定ノード内の物理コア別CPUセットに配置します。OSの権限と利用可能なCPUセットに従います。CPUシーン生成のスレッドプールはOSのスケジューリングに従います。 |
| 16・17 | 通常のNVENC設定はP1・低遅延・CBR 12 Mbpsです。`--sessions 2` で1 GPUに2セッション、`--gpus 0,1 --sessions 2` で異なるNVIDIAアダプターに分散します。連続したフレーム区間を並列エンコードし、圧縮ストリームを順番に結合して一度だけMP4に格納します。区間境界で新しいGOPが始まります。複数セッションは一時ディスクを使用します。 |
| 18・19・28 | 詳細なフレーム計測はReleaseではコンパイルから除外します。`-p:RenderStats=true` で再度有効にできます。CPU色変換にはAVX-512BW、256ビットSIMD、スカラー端数処理があります。既定は無制限、`--pace` は非同期タイマーによる単一セッションのペーシングです。 |

## 実測PGOでDLLをビルドする

Visual Studioのx64 Developer PowerShellで実行してください。訓練用DLLはMSVCのPGOランタイムを使います。作業フォルダーには新しいビルドディレクトリを指定してください。

```powershell
cmake -S native -B out/pgo -A x64 -DNVENC_PGO=train
cmake --build out/pgo --config Release
$dll = (Resolve-Path out/pgo/Release/BotamochiNvenc.dll).Path
dotnet build src/Botamochi.csproj -c Release "-p:NvencLibrary=$dll" -o out/train
foreach ($pattern in 'plasma','grid','rainbow','bars','cyber3d') {
    out/train/Botamochi.exe --direct -p $pattern -d 3 -o "out/train/$pattern.mp4"
    if ($LASTEXITCODE) { throw "Training failed for $pattern" }
}
Copy-Item out/train/BotamochiNvenc!*.pgc out/pgo/Release
cmake -S native -B out/pgo -DNVENC_PGO=use
cmake --build out/pgo --config Release
```

上の発行コマンドで `out/pgo/Release/BotamochiNvenc.dll` と `out/pgo/PackWave.dxil` を指定します。PGOを実行したリンクでは、プロファイルを結合した件数と適用した関数数がMSVCの出力に表示されます。

## 描画処理の高速化と画素比較

前回の測定時は、Direct経路のカラーバーで固定領域を一度だけ描画し、進捗バーの変化した部分を更新していました。現在のDirect経路はNV12テクスチャ上でカラーバーを生成します。グリッドは背景行を複製して光球の範囲を上書きします。レインボーはAVX2対応時に8画素ずつ描画します。Cyber3Dは画素の距離と角度を再利用し、座標キャッシュの合計は全セッションで64 MiBを上限とします。

GeForce RTX 3080 Ti Laptop GPU、1920×1080、60fpsのNative AOT版で、直前のNative AOT版と比較しました。値はプロセス起動からMP4完成までを各3回測った中央値です。

| パターン | フレーム数・設定 | 変更前 | 変更後 |
| --- | --- | ---: | ---: |
| カラーバー | 60・可逆 | 4.00秒 | 1.40秒 |
| グリッド | 60・可逆 | 1.50秒 | 1.32秒 |
| レインボー | 60・可逆 | 2.13秒 | 1.85秒 |
| Cyber3D | 60・可逆 | 2.81秒 | 2.42秒 |
| プラズマ | 300・既定 | 2.87秒 | 2.89秒 |

各動画をFFmpegでデコードし、全フレームのSHA-256とタイムスタンプの一致を確認しました。幅642のCPU変換、幅644のGPU変換、6秒の2セッション可逆出力でも全5パターンを比較しています。

## GPU読み戻しとDirectシーン生成の測定

通常経路ではGPUがNV12をマップ済みリングへコピーし、FFmpegの書き込みスレッドが同じスロットを読みます。CPUでRGBAを生成する場合も、GPUへアップロードする領域に直接描画します。Direct経路のレインボーはCPUと同じ丸めを維持するため、正規化した行・列の座標だけを初期化時に転送します。

RTX 3080 Ti Laptop GPU、Full HD・60fps、1セッションで直前の配布版と比較しました。時間は各版5回の中央値で、起動とMP4の確定を含みます。

| シーン・設定 | フレーム数 | 変更前 | 変更後 |
| --- | ---: | ---: | ---: |
| プラズマ・既定 | 300 | 1.380秒 | 1.270秒 |
| グリッド・既定 | 120 | 1.443秒 | 1.077秒 |
| レインボー・可逆 | 120 | 1.712秒 | 0.894秒 |
| カラーバー・可逆 | 120 | 0.897秒 | 0.812秒 |

いずれもFFmpegのフレーム別SHA-256とタイムスタンプが一致しました。追加で3840×2160のプラズマ、644×398と642×398の全5シーン、可逆2セッションでのレインボーとカラーバーも比較しています。

## GPU描画とNVENCの並列実行

無制限のDirect経路ではセッションごとに4枚の共有サーフェスを循環させます。D3D12が次のフレームを描画する間に、NVENCは描画済みのフレームを圧縮します。非同期NVENCに対応する環境ではサーフェスごとの完了イベントを使い、出力はフレーム順に回収します。末尾のパケットも回収してからMP4を確定します。ペーシング時、またはセッションの担当区間が4枚未満の場合は1枚を使用します。起動ログで実際の枚数を確認できます。

通常経路の無制限プラズマ描画では、解像度とリングの空き範囲に応じて最大4フレームの計算と読み戻しをまとめます。

RTX 3080 Ti Laptop GPU、4Kプラズマ、60 fps・600フレーム、Direct・1セッションで、新旧を交互に各3回測定しました。起動からMP4確定までの中央値は4.517秒から3.942秒に短縮しました。別途1,200フレームの同条件で取得したnvidia-smiのエンコーダー使用率の中央値は87%から100%になりました。Full HDの通常経路では300フレームの中央値が1.351秒から1.371秒で、同じ改善は見られませんでした。

## SPSCリングの測定

通常経路から未使用のPTS・フレーム番号を除き、Channel待機を固定長リングと再利用イベントへ置き換えました。リングのスロット位置は除算や原子的インクリメントを使わずに循環します。RTX 3080 Ti Laptop GPU、Full HD・60 fps・600フレーム、PGO付きNative AOTで新旧を交互に各5回測定した中央値は、プラズマが4.319秒から4.103秒へ短縮しました。カラーバーは2.815秒から2.853秒で測定揺らぎの範囲でした。
