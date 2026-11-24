# SMC シリーズ HDRP 全面移行計画（案）

作成 2026-11-21 ／ 管理: Claude（SMC シリーズ管理） ／ 状態: **計画案（未着手）**

## 0. 決定事項（オーナー決定 2026-11-21）

- SMC-AD 系統（Blender アドオン、Unity パッケージ、タンクリギング、SMC-DETAILS）の Unity 側は **HDRP のみ対応**。URP 対応は打ち切る。
- 将来 HDRP が URP に統合された場合は、切り替えを検討する。
- 作業は**バックグラウンドで実行**する（オーナーの画面を奪わない）。新しいプロジェクトは C:・D: 以外に作ってよい。

対象外: SMC-VX と Blueprint Lib（Unity を使わない）。

## 1. 前提として知っておくこと（重要）

Unity 公式の「Render Pipelines strategy for 2026」（2026-02-23、Unity スタッフの投稿）による:

- HDRP には**新機能の予定がない**。保守は安定性・不具合修正・重大な問題に絞る。Switch 2 対応のみ例外。終了日は示されていない。
- 投資は URP に集中する。URP に物理ライト単位、露出（自動・事前）、リアルタイム GI、SSR、物理スカイを入れる予定。
- 「HDRP と URP の統合」は、**一度に大きく切り替えることを意図的に避ける**と書かれている。統合ではなく、URP が HDRP の機能を少しずつ取り込む方向。
- Built-in RP は 6.5 で非推奨になる。

**この計画への影響:** HDRP のみにすること自体は問題ない（6.x の間は保守される）。ただし「いつか統合されたら乗り換える」日は来ず、**URP が十分に追いついたら戻る**、という形になる可能性が高い。そこで、次の設計方針を置く。

> **HDRP 専用にするが、URP へ戻る道は塞がない。**
> SM 独自の計算（塗装マスク、ASG、部品乱数、アトラス表、汚し）はパイプラインに依存しない HLSL に分離し、
> 描画は Shader Graph（HDRP ターゲット）から Custom Function で呼ぶ。Shader Graph は 1 つのグラフに URP ターゲットを足せるので、戻すときの費用が小さい。

## 2. 調査結果（URP 依存の洗い出し、2026-11-21）

| 対象 | 件数 | 難しさ | 主な作業 |
|---|---|---|---|
| com.smcad.unity（SM Lit / SM Glass） | 約21 | **高** | 手書きの URP 4 パスシェーダ（約 800 行）を HDRP へ。スペキュラー方式・クリアコート・頂点色の塗装・アトラス表・汚し層・両面描画・HDRP のデカールと法線バッファへの対応 |
| com.smcad.details（SMC-DETAILS） | 約21 | 中〜高 | Details Lit シェーダ、URP の Decal API → HDRP の DecalProjector、URP Lit の判定と `_BaseMap` などのプロパティ名 → HDRP/Lit の名前。**GPU 解析（compute）はパイプラインに依存しないのでそのまま使える** |
| SMCAD_TestProject（自動テスト） | 約10 | 中 | `SetupUrp()` の書き換え、ライトの単位・環境光・反射・露出 |
| com.tankrig.unity（タンクリギング） | 約15 | 低〜中 | パーティクルのマテリアル（URP の Particles/Unlit。代わりの Built-in シェーダは HDRP では描画されない）、地面マテリアル、ライトの強さ（物理単位で調整し直す）。URP の C# API は使っていない |
| TANK TRACK DEV（開発用プロジェクト固有のツール） | 約39 | **高** | RealisticTankFX（URP 専用のパーティクル・シェーダ・Bloom）、TestField（草のディテールは HDRP 非対応、空・環境光・霧は Volume で作る）、シーンの URP コンポーネント、SSAO |
| Blender アドオン（smc_ad、addon_plus） | 約6 | **低** | 書き出す内容は SM 本来のデータで、URP に依存していない。HDRP/Lit をそのまま使う場合だけ ASG ≠ マスクマップの問題が出るが、独自シェーダを続けるので問題にならない |

その他（移行と関係ない古い情報）: 凍結中の Blender 版 smc_details が「SMC-AD/SM Decal」シェーダと「details」JSON を前提にしているが、Unity 側にはもう存在しない。後でドキュメントだけ直す。

## 3. 方針（推奨案）

| 論点 | 推奨 | 理由 |
|---|---|---|
| Unity のバージョン | **6000.3.10f1（HDRP 17.3）を基準**。6000.5（17.5）でも動作確認だけする | 今のプロジェクトはすべて 6000.3。HDRP は 6.5 でも新機能がない |
| SM Lit の作り方 | **HDRP Shader Graph（Lit、Material Type = Specular Color）＋ Custom Function** | 手書きの HDRP シェーダは HDRP の更新で壊れやすい。Shader Graph なら遅延描画・デカール・SSAO・モーションベクトル・レイトレーシングのパスが自動で揃う |
| クリアコートの粗さ | まず Lit のコート（粗さは固定）で見比べる。差が目立つ場合だけ StackLit 版を追加する | StackLit は重く、前方描画専用 |
| 開発用の Unity プロジェクト | **新規に HDRP プロジェクト `E:\ClaudeCode\SMC-HDRP-DEV` を作る**。TANK TRACK DEV は URP の最終版として凍結して残す | 元のプロジェクトを壊さずに比べられる。バッチモードで使っても、オーナーが開いているプロジェクトと衝突しない |
| SMCAD_TestProject | 同じ場所で HDRP に変える（リポジトリに含まれているため） | 自動テストを HDRP で回す |
| RealisticTankFX・砲口シミュレーション | **移植しない（保留）**。作り直すときは HDRP の VFX Graph と Local Volumetric Fog で作る | 現行方式はオーナーが不採用にしている。「ひとかたまりの煙」は HDRP のボリューメトリックの方が向いている |
| 草（TestField） | テクスチャの草 → メッシュの草 | HDRP はテクスチャの草に非対応 |
| ライト | 物理単位（太陽は lux、点・スポットは lumen）＋露出の Volume。テスト撮影は固定露出 | 自動露出は新しいカメラで数フレーム安定しない |
| URP 版の保存 | 各リポジトリに `urp-final` タグを付け、README に「URP 版は○○まで」と書く | URP に戻るときの起点になる |

## 4. 作業の段階

各段階の「完了」は、オーナーが確認できる形（画像の比較、バッチテストの結果）で示す。

### P0 準備（すべてバックグラウンドで実行）
1. 各リポジトリに作業ブランチ `hdrp` を作る（SMC-AD は今の `dev` から）。`urp-final` タグは作るが、push はオーナーが了承してから。
2. **URP 版の見本画像を先に撮っておく**: TANK TRACK DEV（Stingray、TestField）と SMCAD_TestProject（HITOGATA など）を、バッチモードで同じカメラ位置から撮影する。移行後の比較に使う。
3. `SMC-HDRP-DEV`（6000.3.10f1、HDRP 17.3、Linear、DX12）を作り、3 つのパッケージを `file:` で参照する。HDRP アセットではデカール・SSAO・SSR・ボリューメトリックを有効にする。
4. ClaudeBridge を新しいプロジェクトへ移す（撮影は固定露出、HD のカメラ設定をコピー）。

### P1 SM Lit / SM Glass（核の部分）
1. SMCommon.hlsl から URP の include を外し、SRP Core だけで動く「SM 計算部」にする（塗装・ASG・上書き色・表面・レイヤー・マスク・アトラス）。
2. `SM Lit.shadergraph`（HDRP Lit・Specular Color・両面・Alpha Clip）を作る。Custom Function で SM 計算部と SMDetails を呼ぶ。UV2〜UV7 と頂点色は Shader Graph の標準ノードで読める。
3. `SM Glass.shadergraph`（透過）を作る。
4. 取り込み処理（SMBlueprintImporter、SMFbxPostprocessor、SMLitGUI）を HDRP のマテリアル検証に合わせ、`_CLEARCOAT` → HDRP のコートに置き換える。
5. 確認: 見本画像との比較（HITOGATA、Stingray）、デカールが載ること、SSAO、モーションベクトル、影。

**P1 の結果（2026-11-21、コミット前）**
- 計算部: `SMCommon.hlsl`（SRP Core のみ）＋新しい `SMSurface.hlsl`（照明の手前まで）。Shader Graph 用の入口は `SMGraph.hlsl`（`SMLit` / `SMAlpha` / `SMGlass`）。URP の `SMLit.shader`・`SMGlass.shader`・`SMForward.hlsl` は `hdrp` ブランチから削除（`urp-final` に残る）。
- グラフは手で編集せず `unity/tools/make_sm_graphs.py` で生成する（プロパティの一覧はスクリプトの中。オブジェクト ID は名前から作るので、作り直しても差分が出ない）。
- **UV の割り当てが変わった**: HDRP はモーションベクトルのパスで TEXCOORD4 を使うため、SMC-DETAILS の「凸の角までの距離」は **UV4 → UV1** に移した（UV5 静止位置、UV6 静止法線、UV7 凹の角は同じ）。P2 で SMDetailsBuilder の書き込み先を UV1 にすること。UV1 はライトマップ用だが、SM の機体はライトマップを使わない前提。
- HDRP の設定: Lit は Specular Color・クリアコート・両面（Flip）・Alpha Clip（不透明のマテリアルでは切る）。SM の発光は露出を打ち消す（Blender / URP と同じ明るさ）。
- 注意: HDRP がまだ一度も描画していないとき（バッチモード、開いた直後の取り込み）は `Shader.globalRenderPipeline` が空で、HDRP のマテリアル検証が何もしない。`SMMaterialSetup.Validate` がこのタグを先に入れる。
- 確認済み（SMC-HDRP-DEV、バッチ）: HITOGATA・ROBO-TEST の見本との比較、全バリアント（不透明・切り抜き・コート・アトラス・汚し・ガラス）のコンパイルと描画、シェーダーエラー 0。未確認: デカール、SSAO・モーションベクトルの見た目、Stingray（P3/P4 の後）。

### P2 SMC-DETAILS
1. Details Lit → HDRP Shader Graph（P1 の部品を再利用）。SMDetailsBuilder の凸の角のデータは UV1 に書く（上記）。
2. 車輪・履帯の判定を `HDRP/Lit` と HDRP のプロパティ名（`_BaseColorMap`、`_NormalMap`、`_MaskMap` の A）にする。
3. デカールを HDRP の DecalProjector と `HDRP/Decal` にする。ウィンドウの URP 設定チェックは HDRP 用（Frame Settings）に書き換える。
4. 確認: 汚し 8 種、ブラシ、プリセット、履帯の泥・砂が URP 版と同じに見えること。

**P2 の結果（2026-11-21、コミット前）**
- `Details Lit` も `make_sm_graphs.py` で生成（HDRP Lit・メタリック・片面、プロパティ名は HDRP/Lit と同じ: `_BaseColorMap`、`_MaskMap`＋remap、`_NormalMap`）。計算は `com.smcad.details/Runtime/Shaders/SMDetailsGraph.hlsl`（SMDetails.hlsl をそのまま使う）。URP の `SMDetailsLit.shader` は削除。生成し直しても SM Lit / SM Glass は 1 バイトも変わらないことを確認。
- 凸の角のデータは UV1 に書く（SMDetailsEdges）。元メッシュの UV1 は引き継がない。
- 複製の判定は `HDRP/Lit`、複製後に `SMMaterialSetup.Validate`。履帯のリンク（実行時）も HDRP/Lit の名前でコピー。
- デカールは HDRP の DecalProjector＋`HDRP/Decal`（色だけを載せる）。ウィンドウの警告は「HDRP アセットの Decals」が無効なとき。
- パッケージ: com.smcad.details 0.2.0（HDRP 17.3、com.smcad.unity 0.4.0）。
- 確認済み（SMC-HDRP-DEV、バッチ、ROBO-TEST）: 解析 3 秒、全 17 マテリアルに汚し、プリセット 4 種、角・くぼみの表示、SM Lit と地面へのデカール、Details Lit の全バリアント（通常・汚し・履帯）のコンパイル。画像は `verify/hdrp/p2`。
- 未確認: 手描きブラシ（シーンビューの操作なのでバッチでは試せない）、履帯・車輪（P3 のタンクリギングが HDRP になってから）、URP 版との並べた比較（戦車で P3/P4 の後）。

### P3 タンクリギング
1. パーティクル用の HDRP Shader Graph（Unlit、加算と半透明）をパッケージに入れ、TankFxFactory を対応させる。
2. 地面を `HDRP/Lit` にする。マズル・ヘッドライト・尾灯を物理単位で調整し直す。
3. 確認: 組み立て → Complete → 走行・射撃（バッチテストと撮影）。

**P3 の結果（2026-11-21、コミット前）**
- タンクリギング 0.8.0-alpha（HDRP 17.3 必須）。パーティクルはパッケージ内の `TankRig/Particle Unlit`（HDRP Unlit・半透明 / 加算、`make_sm_graphs.py` で生成。シェーダー名は m_Path＋ファイル名なのでファイル名は `Particle Unlit.shadergraph`）。HDRP の Unlit は露出をかけないので、色は URP と同じに見える。
- ライトは物理単位: 発砲光 800,000 lm、前照灯 3,000 lm、尾灯 60 lm。地面は HDRP/Lit。古いマテリアルは同じファイルのまま差し替え。
- 移行用の道具を 2 つ追加: `Tools > SMC-AD > URP のマテリアルを HDRP に変換`（SM Lit / Glass / Details Lit / URP Lit。.mat に書かれた旧シェーダーの GUID で探すので、シェーダーが消えていても値を引き継ぐ）、`Tools > TankRig > HDRP に合わせる（ライト・エフェクト）`。
- 注意: `Shader.Find` は読み込み済みのシェーダーしか見つけない。パッケージの Shader Graph は名前で見つからないことがあるので、ファイルのパスから読む（`SMMaterialSetup.FindShader`、`HDRPSetup.ParticleShader`）。
- 確認済み（SMC-HDRP-DEV、バッチ）: TANK TRACK DEV の戦車シーン（KS-25A3V）をそのまま持ち込み → 変換 → 見本と同じ 9 方向で撮影。形・カメラ・日の当たる面は一致。発砲（閃光・発砲光・煙）。ビルダーの自動組み立て（自動検出 → ポイント → 履帯 7W → 完成）、履帯のリンク、汚しが履帯・車輪にも付くこと。画像は `verify/hdrp/p3`。
- 未確認: 走行（Play モードが必要。P4 で Play モードのテストを作る）、前照灯・尾灯の見た目（夜のシーン）。影側が URP より暗いのは、持ち込んだシーンに HDRP の空（Volume）がないため → P4 のシーン作り直しで直す。
- 気づいたこと（HDRP とは無関係）: スクリプトで自動組み立てすると、KS-25A3V は前後が逆で履帯が外へ開く。ウィンドウで手直しする前提なら問題ないが、自動検出の改善候補。

### P4 開発用プロジェクトの道具
1. TestField: HDRP/Lit、HDRP/TerrainLit、メッシュの草、PB Sky・霧・露出の Volume、太陽は lux。
2. シーン（TankRig_Stingray、TestField）を新しいプロジェクトで作り直す（URP → HDRP の自動変換はない）。
3. ScreenRecorder の確認（上下反転だけ注意）。

**P4 の結果（2026-11-21、SMC-HDRP-DEV、git なし・手動バックアップの対象）**
- TestField（`Assets/TestField/Editor/TestFieldBuilder.cs`）を HDRP に移植: HDRP/Lit・HDRP/TerrainLit、草はインスタンス描画のメッシュ（HDRP には地形のビルボード草がない）、太陽 100,000 lux、Volume に PB Sky・霧（平均自由行程 285 m）・固定露出 EV 13・モーションブラーなし。生成 21 秒。
- 太陽の高さを 38° → 55° に変更: HDRP の影は 500 m 先まで描かれる（URP は約 50 m）ため、低い太陽だと道と戦車が木の影に入った。
- 戦車シーン `TankRig_Stingray` は TANK TRACK DEV からコピーし、`SMCADHdrpTankTest.RebuildTankScene` で作り直して保存（変換・URP 専用部品の削除・空と露出の Volume）。TestField の「戦車を置く」はここから戦車を写す。TANK TRACK DEV は読むだけで変更していない。
- **走行テスト（Play モード、バッチ）**: `Assets/Tests/PlayMode/TankDriveTest.cs`、実行は `SMC-Tools/unity-test.ps1`。5 秒で約 31 m（約 11 m/s）、3 秒の旋回で約 40°、主砲の発砲光 約 77 万 lm。合格。
- HDRP の見た目の違い（不具合ではない）: 空からの環境光は物理的な強さなので、URP の Trilight より影側が暗い。暗い塗装の戦車は影側がほぼ黒になる。明るくしたいときは露出を下げる・自動露出・Indirect Lighting Controller のどれか。
- 道具の修正: `unity-batch.ps1` が追加の引数なしで動かなかったのを修正。`unity-test.ps1` を追加。
- 残り: RealisticTankFX と発射炎のシミュレーションは保留のまま（VFX Graph で作り直す案）。

### P5 Blender 側・ドキュメント・リリース
1. Blender アドオン: 動作は変えない。表記を「Unity 側は HDRP」にする。凍結中の smc_details の古い記述を直す。
2. package.json の依存を HDRP に変え、版を上げる（例: com.smcad.unity 0.4.0、details 0.2.0、tankrig 0.8.0-alpha）。README、CHANGELOG、インストール手順。
3. GitHub への push とリリースは**オーナーの了承を得てから**行う。

**P5 の結果（2026-11-21、コミット前・未公開）**
- Blender アドオンのコードは URP に触れていないため変更なし。README（リポジトリ・凍結中の smc_details）と CHANGELOG を HDRP に合わせた。凍結中の smc_details から Unity へ汚しを持っていく道（UV4）は廃止と明記。
- リリース用の tgz を `dist_release/hdrp-2026-11-21/` に作成（com.smcad.unity 0.4.0、com.smcad.details 0.2.0、com.tankrig.unity 0.8.0-alpha、SHA256SUMS、RELEASE_NOTES 下書き）。SMC-HDRP-DEV に tgz から入れて全シェーダーの組み合わせが動くことを確認。
- 残り（オーナーの了承待ち）: GitHub への push（`hdrp` ブランチ・`urp-final` タグ）、リリースの公開。`SMCAD_TestProject` は URP のまま凍結（HDRP のテストは SMC-HDRP-DEV にある。リポジトリに入れるなら別途）。

**残りの確認（2026-11-21）**
- .glb の直接取り込み（SMBlueprintImporter）: hitogata_edited / plain を URP の見本と比較、色・テクスチャ一致。
- 機銃（1.5 秒で 19 発・曳光弾・着弾 7）、副砲、主砲の着弾エフェクト、Kinematic 走行（4 秒で約 30 m）: Play モードのテスト `WeaponsAndKinematic` 合格。
- 夜: 前照灯（3,000 lm）が木を照らし、尾灯（ブレーキで 3 倍）が赤く光る。
- ScreenRecorder を SMC-HDRP-DEV へ移植（描画方式に依存しないのでコードは無変更）。バッチモードでは `WaitForEndOfFrame` が来ないため録画は 0 フレーム → テストはバッチでは飛ばす。**エディタで F12 を押しての確認が必要**。
- 実物のガラス・アトラスの機体は手元の見本にない（球では描画を確認済み）。手描きブラシと材質の画面はエディタで触って確認が必要。

**名前の整理（2026-11-24）**: この文書の `com.smcad.unity` は `com.smcad.importer`、`com.tankrig.unity` は `com.smcad.tankrig`（フォルダ `E:\ClaudeCode\SMC-AD-TankRigging`）になった。`SMCAD_TestProject`・TANK TRACK DEV・`_URP-Archive` は削除済み（URP 版のコードは `urp-final` タグに残る）。

## 5. バックグラウンドでの作業の決まり

- Unity は `-batchmode`（ウィンドウなし）で、オーナーが開いていないプロジェクトにだけ使う。Unity MCP（エディタを前面に出す必要がある）は使わない。
- Blender は `-b`（ウィンドウなし）。
- 重い処理（HDRP の初回のシェーダコンパイル、撮影）は**優先度を下げて**（BelowNormal）実行する。GPU（RTX 3080）も使うので、オーナーがゲームなどで GPU を使っている時間には大きな撮影をしない。
- 進捗は SMC シリーズ管理のセッションでまとめて報告する。

## 6. オーナーに決めてもらうこと

1. 基準の Unity を 6000.3 にしてよいか（推奨: はい）
2. TANK TRACK DEV を凍結し、新しく `SMC-HDRP-DEV` を作ってよいか（推奨: はい）
3. RealisticTankFX と砲口シミュレーションを保留にしてよいか（推奨: はい。作り直すときは HDRP で）
4. この計画で P0 から始めてよいか

## 参考
- Unity Discussions「Render Pipelines strategy for 2026」 https://discussions.unity.com/t/render-pipelines-strategy-for-2026/1710004
