# α版（Git に入れる前）の確認（2026-11-13）

開いている Blender 5.3 に HITOGATA を取り込み、SMC-AD と SMC-AD+ のパネルとボタンをすべて確認した。

## 確認できたこと
- 操作（オペレーター）は SMC-AD が14個、SMC-AD+ が17個。すべてパネルかメニュー（ファイル > インポート／エクスポート）から使える
- 両アドオンのマニフェストは、Blender の検証（`extension validate`）を通った
- これまでの機能のテスト（取り込み、マテリアル、インポート直後に戻す、ボディを外す、FBX → Unity）はすべて通っている

## A. α の前に直すもの（不具合）
| # | 内容 | 状態 |
|---|---|---|
| A1 | ジョイントの「角度」スライダーが、関節の軸ではなく親の Z 軸で回っていた | **対応済み**: 機能ごと外した（ベアリングはボディを分ける目印として使い、回転はユーザーが付ける方針） |
| A2 | ジョイントのパネルが、ボディを番号で表示していた | **対応済み**: ジョイントのパネルをなくし、「ボディの親子」パネルに「ベアリング: HITOGATA_01」のように名前で出す |

## B. α の前にあるとよいもの（使い勝手）
| # | 内容 | 状態 |
|---|---|---|
| B1 | 変換はコマンドラインだけ。Blender から「ブループリントを名前で選ぶ → 変換 → 取り込む」ができない | **対応済み**: パネルの「ブループリントから取り込む…」とファイル > インポートから、名前で検索して選ぶと裏で変換して取り込む（Esc で中止）。変換ツールとフォルダは自動で探し、アドオンの設定で指定もできる。変換のオプションも設定にある |
| B2 | ~~全ジョイントの姿勢を一度に戻すボタン~~ | 不要（ジョイントで動かす機能そのものを、Blender・Unity の両方から外したため） |
| B3 | パネルの順番が作業の流れに合っていない。色の差し替えと一括操作は、既定で閉じておく | **対応済み**: 取り込み → ボディの親子 → 色の差し替え（閉）→ 一括操作（閉）→ 書き出し・やり直し。既に開いて保存した .blend では、開閉の状態はそのまま残る |
| B4 | サイドバーの幅（280px）で文字が切れる | **対応済み**: 長い文言を短くし、2行に分けた。子のボディは1行に1つ |
| B5 | パネルに版数が出ない | **対応済み**: 「SMC-AD  v0.6.0」「材質（Surfaces）  v0.3.0」（マニフェストから読む） |

### 今回の確認で見つけて直したもの
- 一番上の Empty を削除した機体で「取り込み直後に戻す」「マテリアルを作り直す」「見た目だけ（.glb）の書き出し」が、元の .glb を見つけられず効かなかった。取り込み元の .glb と取り込みの設定を、すべてのオブジェクトに記録するようにした（ROBO-TEST BEXT には後から記録を足した）
- スクリプトやバックグラウンドから書き出しを呼ぶと、結果のポップアップで Blender が落ちた。画面がないときはポップアップを出さないようにした

## C. 方針を決めるもの
| # | 内容 |
|---|---|
| C1 | Blender で撮影するための支援（照明・カメラ・背景のセット）が何もない。SMC-AD の目的の1つなので、α に入れるか |
| C2 | 古い「見た目だけ（.glb）」の書き出しを残すか（FBX と2つあって紛らわしい） |

## D. Git の準備（2026-11-14 対応済み）
### ライセンス
| 部分 | ライセンス | 確認したこと・したこと |
|---|---|---|
| `SMConverter/` | GPL-3.0 | GitHub の LICENSE は GPL-3.0 の原文のまま、リリースのページにも追加の条件はない。GPL の求める「改変した旨と日付」を `SMConverter/SMC-AD_CHANGES.md` に書いた（変更 28 ファイル、追加 3 か所） |
| Blender 拡張 2つ | GPL-3.0-or-later | 各フォルダに `LICENSE` を入れ、マニフェストに `copyright` を足した（`extension validate` 通過）。.py はすべて SPDX 付き |
| Unity パッケージ | **MIT** | SMConverter のコードを含まないので GPL に縛られない。ゲームのビルドに入るため MIT にした。`LICENSE.md`、`Third Party Notices.md`（SM-Converter-Plugin の MIT 表記）、`package.json` の `license`、全ソースに SPDX。Unity でコンパイルを確認 |
| 同梱ライブラリ | BSD・MIT・Apache など | `SMConverter/Dependencies/` は元のまま。ライセンスファイルが入っていないため、一覧を `THIRD_PARTY_NOTICES.md` にまとめた |
| Scrap Mechanic | Axolot Games | ゲームのデータは入れない（変換結果・Unity の取り込み結果・検証の出力は除外）。非公式である旨を README に書いた |

- 名義: **SMC-AD Project**（SMC-AD = Scrap Mechanic Converter – Assemble & Deliver）
- 一番上に `README.md`、`LICENSE`（GPL-3.0）、`THIRD_PARTY_NOTICES.md`、`.gitignore`、`.gitattributes` を置いた

### ファイルの整理
- リポジトリは1つにまとめる。`SMConverter/.git`（元の 1.2.4-pre をステージしただけ）は `_archive/SMConverter.git` へ移した。元との差分は `_archive/SMConverter_changes_vs_1.2.4-pre.patch` にも残した
- 使わなくなったものを `_archive/` へ移した（削除はしていない）: `addon/dist/`（古い 0.2.0 の zip）、`addon_plus/_old/`、`unity/create.log`、`.bp_root`（個人のフォルダのパス）
- `__pycache__/` は削除した（作り直されるもの）
- `.gitignore` で除外するもの: `_archive/`、`GitHub/`、`extracted/`、`verify/`、`SMConverter/Build/`、Unity の Library・Temp・Logs・UserSettings、取り込んだ機体（`Assets/SMCAD*`）、変換結果のフォルダ
- Git に入るもの: 848 ファイル、約 38MB（一番大きいのは同梱ライブラリの DLL、約 7MB）。個人のパスや ID が入っていないことを確認した
- Git: 2026-11-14 に作成し、GitHub に公開した（https://github.com/KULALENUMIO/SMC-AD 、ブランチ main）
  1. `Import SM-Converter v1.2.4-pre (upstream)`: 元の SM-Converter をそのまま `SMConverter/` に入れた
  2. `SMC-AD alpha`: SMC-AD のすべて。`git diff HEAD~1 -- SMConverter` で元からの改変だけが見える
  - コミットの名義は `SMC-AD Project`（メールは実在しない仮のもの。個人のメールを残さないため）
- README の一番上に「α版・実験的。気まぐれで更新。正式版では削除・置き換えの可能性あり」を日英で書いた
- まだしていないこと: Blender 拡張の zip（`extension build`）と GitHub の Releases への登録
