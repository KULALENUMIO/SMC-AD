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

### P2 SMC-DETAILS
1. Details Lit → HDRP Shader Graph（P1 の部品を再利用）。
2. 車輪・履帯の判定を `HDRP/Lit` と HDRP のプロパティ名（`_BaseColorMap`、`_NormalMap`、`_MaskMap` の A）にする。
3. デカールを HDRP の DecalProjector と `HDRP/Decal` にする。ウィンドウの URP 設定チェックは HDRP 用（Frame Settings）に書き換える。
4. 確認: 汚し 8 種、ブラシ、プリセット、履帯の泥・砂が URP 版と同じに見えること。

### P3 タンクリギング
1. パーティクル用の HDRP Shader Graph（Unlit、加算と半透明）をパッケージに入れ、TankFxFactory を対応させる。
2. 地面を `HDRP/Lit` にする。マズル・ヘッドライト・尾灯を物理単位で調整し直す。
3. 確認: 組み立て → Complete → 走行・射撃（バッチテストと撮影）。

### P4 開発用プロジェクトの道具
1. TestField: HDRP/Lit、HDRP/TerrainLit、メッシュの草、PB Sky・霧・露出の Volume、太陽は lux。
2. シーン（TankRig_Stingray、TestField）を新しいプロジェクトで作り直す（URP → HDRP の自動変換はない）。
3. ScreenRecorder の確認（上下反転だけ注意）。

### P5 Blender 側・ドキュメント・リリース
1. Blender アドオン: 動作は変えない。表記を「Unity 側は HDRP」にする。凍結中の smc_details の古い記述を直す。
2. package.json の依存を HDRP に変え、版を上げる（例: com.smcad.unity 0.4.0、details 0.2.0、tankrig 0.8.0-alpha）。README、CHANGELOG、インストール手順。
3. GitHub への push とリリースは**オーナーの了承を得てから**行う。

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
