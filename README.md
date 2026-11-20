# SMC-AD

> [!WARNING]
> **α版・試作品です。中身は完全に実験的なものです。**
> - 作者の気まぐれで作っています。まともな更新やサポートは期待しないでください
> - 不具合があっても直すとは限りません。使い方や中身は予告なく変わります
> - 正式版にするときは、このリポジトリを削除するか、別のリポジトリに置き換えるかもしれません
>
> **Alpha / prototype. Everything here is experimental.** Updates happen on a whim — please don't expect
> regular updates, fixes or support. Things may change without notice, and this repository may be deleted or
> replaced by another one if a proper release is ever made.

**SMC-AD（Scrap Mechanic Converter – Assemble & Deliver）** は、Scrap Mechanic のブループリントを Blender に取り込み、
位置・回転・原点・親子を整えて（リギングは手作業）、Unity へ持っていくか Blender で撮影するための道具一式です。

> 非公式のツールです。Scrap Mechanic は Axolot Games の商標で、SMC-AD は Axolot Games とは関係ありません。
> このリポジトリにゲームのデータは入っていません（変換時に、利用者の PC のゲームから読み込みます）。

## 中身
| フォルダ | 内容 | ライセンス |
|---|---|---|
| `SMConverter/` | 変換ツール（QuestionableM の SM-Converter v1.2.4-pre を改変）。glTF 書き出し、ジョイントごとのボディ分け、見えない面の削除など。改変点は `SMConverter/SMC-AD_CHANGES.md` | GPL-3.0 |
| `addon/smc_ad/` | Blender 拡張「SMC-AD」: ブループリントの変換と取り込み、SM の見た目のマテリアル、色の差し替え、ボディの親子の整理、Unity 向け FBX 書き出し | GPL-3.0-or-later |
| `addon_plus/smc_ad_plus_surfaces/` | Blender 拡張「SMC-AD+ Surfaces」: パーツごとに材質（金属・ゴム・木など）を付ける | GPL-3.0-or-later |
| `addon_plus/tools/` | 材質のサムネイル用テクスチャを作るスクリプト | GPL-3.0-or-later |
| `unity/com.smcad.unity/` | Unity パッケージ: SMC-AD の .glb / FBX を SM の見た目（HDRP の Shader Graph）で取り込む | **MIT** |
| `unity/com.smcad.details/` | Unity パッケージ（試作）: SMC-DETAILS for Unity。汚し・手描き・デカール（HDRP） | **MIT** |
| `unity/tools/` | Shader Graph の生成スクリプト（`make_sm_graphs.py`。グラフは手で編集しない） | MIT |
| `unity/SMCAD_TestProject/` | Unity パッケージの確認用プロジェクト（設定とテスト用スクリプトのみ）。**URP 版のまま凍結**（`urp-final` のパッケージ用） | MIT |
| `docs/`、`CHANGELOG.md`、`ANALYSIS.md`、`DESIGN.md` | 設計・確認の記録、変更履歴 | — |

- Unity パッケージは、作ったゲームのビルドにも入るため MIT にしています（SMConverter のコードは含みません）
- 他者の著作物とライセンスの一覧: `THIRD_PARTY_NOTICES.md`
- 著作権: Copyright (C) 2026 SMC-AD Project（SMConverter 部分は QuestionableM ほか）

## 必要なもの
- Scrap Mechanic（変換に、インストールされたゲームのデータを使う）
- Windows、Visual Studio（C++、MSBuild）: 変換ツールのビルド
- Blender 4.2 以降（確認は 5.3）
- Unity 6000.3 以降 + **HDRP 17.3 以降**（Unity 側は HDRP 専用。URP 版は `urp-final` タグ／v0.6.0-alpha まで）

## 入れ方（リリースから）
[Releases](https://github.com/KULALENUMIO/SMC-AD/releases) にプレリリース版を置いています（Windows のみ）。
1. `SMC-AD-Converter-win64-*.zip` を好きな場所に展開する
2. Blender の「編集 > プリファレンス > 拡張機能 > ディスクからインストール」で `smc_ad-*.zip`（と、必要なら `smc_ad_plus_surfaces-*.zip`）を入れる
3. SMC-AD の設定の「変換ツール（SMConverterCLI.exe）」に、1. で展開したフォルダの `SMConverterCLI.exe` を指定する
4. Unity（**HDRP のプロジェクト**）: Package Manager の「+ > Add package from tarball...」で `com.smcad.unity-*.tgz` を入れる（tgz は入れたあとも消さずに置いておく）。URP で作ったマテリアルは `Tools > SMC-AD > URP のマテリアルを HDRP に変換` で移せる

## 使い方（概要。ソースからビルドする場合）
1. 変換ツールをビルドする
   ```
   MSBuild SMConverter/SM-Converter.sln -t:SMConverterCLI -p:Configuration=Release -p:Platform=x64
   ```
   `SMConverter/Build/SMConverterCLI-Release/SMConverterCLI.exe` ができる（アドオンはここを自動で探す）
2. Blender で `addon/smc_ad` を拡張としてインストールする（必要なら `addon_plus/smc_ad_plus_surfaces` も）
3. サイドバー「SMC-AD」→「ブループリントから取り込む…」で、名前を選んで変換・取り込み
4. 整えたら「Unity 向けに書き出し（FBX）」
5. Unity で `unity/com.smcad.unity` をパッケージとして追加し、`Tools > SMC-AD > Blender から書き出した FBX を取り込む...`

詳しくは `unity/com.smcad.unity/README.md`、`addon_plus/smc_ad_plus_surfaces/README.md`、`CHANGELOG.md`。

## 謝辞
- SM-Converter / SM-Converter-Plugin: QuestionableM（https://github.com/QuestionableM）
