# SMC-AD 解析メモ（初回）

対象: `GitHub/` に置かれた4つのzip（`extracted/` 以下に個別展開済み）

| zip | 展開先 | 中身 |
|---|---|---|
| SM-Converter-1.2.4-pre.zip | `extracted/converter-src` | 変換ツール本体のC++ソース（VS2022 / Qt6 / assimp / simdjson）。ビルド番号 2860 |
| SMConverterQt-ReleaseDebugInfo.zip | `extracted/converter-bin` | ビルド済み配布物（`SMConverterQt.exe` GUI＋`SMConverterCLI.exe`）。2026-09-20ビルドで、ソースzip（2026-06-10）より新しい |
| SM-Converter-Plugin-0.1.1.zip | `extracted/plugin-src` | Blenderアドオンのソース（README・画像付き） |
| SM.Converter.Plugin (Release).zip | `extracted/plugin-release` | 上と同じもの（改行コードが違うだけ） |

---

## 1. SMConverter（C++）

### 変換の流れ（ブループリントの場合）
1. `BlueprintConv::ConvertToModel` が `blueprint.json` を simdjson で読み込む
2. `SMBlueprint::LoadChild` で、各childを次の3種類に振り分ける
   - **Block**（`bounds` あり）: 直方体をその場で生成する（`Block.cpp: FillCustomCube`）。UVはワールド座標から計算し、`tiling` でタイル状に並べる
   - **Wedge**（`bounds` あり・Block DBにないもの）: 親ブロックのテクスチャを使う
   - **Part**（`bounds` なし）: ゲーム内のメッシュを assimp で読み込む（`ModelStorage::LoadModel`）
   - **Joint**: `joints[]` から読み込む
3. 分割モード（`SeparationType`）に応じて `SMBody`（OBJの `o` 単位）にまとめる
   - 0 None / 1 All / 2 Shapes / 3 Joints / 4 Uuid / 5 Color / 6 Uuid+Color
4. 全体の中心が原点に来るように平行移動する（エンティティ位置の平均を使う）。スケールは **0.25**（1ブロック＝0.25m）
5. `.obj` と `.mtl` を書き出す
6. オプション: `ApplyControllerPresets`（コントローラのプリセット角度をボディに適用する。ソース上の新機能）

### マテリアル名の書式（アドオンとの連携の要）
```
<shape uuid> <塗装色hex6桁> <サブメッシュ番号(1始まり)> m<シェーダID>
例) 628b2d61-5ceb-43e9-8334-a4135566df7a 0a3ee2 1 m1
```
- シェーダIDは `Resources/MaterialIds.json` で決まる（SMのシェーダ名 → 1〜11）
- `.mtl` の中身: `Kd`＝塗装色、`map_Kd`＝Dif、`map_Ks`＝Asg、`map_Bump`＝Nor
- テクスチャは**Scrap Mechanic本体のフォルダを絶対パスで参照**している（テクスチャはコピーされない）

### CLI（`SMConverterCLI.exe`）
`-type -path -name -septype -applyrotations -exportmaterials -exportuvs -exportnormals -deduplicateverts …`
→ Blenderアドオンから直接呼び出して自動化できる

### Blender向けに見た問題点・改良の余地
| # | 問題 | 影響 |
|---|---|---|
| C1 | **ブロックが1個ずつ独立した直方体（12三角形）として出力される**。隣り合うブロック間の見えない面も残り、`DeduplicateVertices` がオフだと頂点も共有されない | ポリゴン数が膨大になり、内部面が残ってブーリアンやスムーズシェードの邪魔になる |
| C2 | **マテリアルが「uuid×色×サブメッシュ」の組み合わせごとに増える** | 色違いのブロックだけでマテリアルが何百個にもなる。Blenderでの編集が重い |
| C3 | スムージング情報（`s` 行）を出力しない。ブロックは面法線のみ | パーツの見た目は法線データ次第になる |
| C4 | テクスチャをゲームフォルダの絶対パスで参照している | `.blend` を別のPCに移すとテクスチャが切れる |
| C5 | 形状ID・色・シェーダなどの情報がマテリアル名にしか残らない | マテリアルを統合すると情報が消える |
| C6 | 分割モードが平坦で、ボディやジョイントの親子階層・ピボットがない | 可動部のリギングやアニメーションがやりにくい |
| C7 | 座標系はSM（Z-up）のまま出力している（**要検証**: Blender側のインポート軸設定との組み合わせ） | 読み込むと横倒しになる可能性がある |

---

## 2. SM Converter Plugin（Blenderアドオン v0.1.1）

### 構成
- `__init__.py`: 3つのオペレーターとNパネルを登録（`bl_info` は blender 2.92）
- `BasicFunc.py`: マテリアル名の検証（uuid／色／`mN`）、シェーダIDとノードグループの対応表、sRGB→リニア変換
- `AssignMaterialsFunc.py`: OBJインポーターが作ったノードから Dif／Asg／Nor の画像ノードを探し、Principledなどを削除して SMノードグループに差し替え、塗装色を入力する
- `CreateNodeFunc.py`: `SMMaterials.blend`（Blender 3.4で保存）からノードグループを追加する
- `SMMaterials.blend` 内のノードグループ: SM_DifAsgNor / Glass / Grass / Leaves / DifAsgNorAlpha / Water / LightCone / Liquid / Clutter / GroundMaterial
  - Water・Chemicals・Oil はすべて `SM_Water` をパラメータ違いで使っている

### 見つかったバグ・弱点
| # | 内容 | 場所 |
|---|---|---|
| P1 | 削除しているコレクションをそのままループしているので、ノードが削除されずに残ることがある | `AssignMaterialsFunc.GetMaterialOutputNode` |
| P2 | `SM_Clutter` が `sm_known_materials` に入っていないので、2回実行するとClutterのマテリアルにノードが重複して追加される | `BasicFunc.sm_known_materials` |
| P3 | 空のマテリアルスロットがあると `None.name_full` でクラッシュする | `AssignMaterialsSelected` |
| P4 | インポーターが作るノード構成（ソケット名やノードラベル）に依存しているので、Blenderのバージョン（特に4.x以降のOBJインポーター）によっては壊れやすい | `FindNeededNodes` |
| P5 | マテリアル数そのものは減らせない（C2と同じ問題） | — |
| P6 | 旧形式のアドオン（`bl_info`）。4.2以降の「拡張機能」形式（`blender_manifest.toml`）ではない | `__init__.py` |

---

## 3. 環境
- Blender: `C:\Program Files\Blender Foundation\Blender 3.6` がインストール済み（Blender MCPも利用できる）
- Scrap Mechanic: Steamにインストール済み

## 4. 改良方針の候補（たたき台）
1. **自作アドオン（SMC-AD）がCLIを呼び出し、インポートまでを一括で行う**（パスと軸の設定、テクスチャのコピーを自動化）
2. **ブロックのメッシュ最適化**: 内部面の削除と同一平面の結合（C++側で行うか、インポート後にPythonで行うか）
3. **マテリアルの統合**: 形状×シェーダ単位の共通マテリアルにまとめ、色は頂点カラーかアトリビュートで渡す（C2・C5の解決）
4. **メタデータを別ファイル（JSON）で出力**し、オブジェクトのカスタムプロパティや階層を再現する（C5・C6）
5. 既存プラグインのバグ（P1〜P3）を修正し、新しいBlenderに対応させる
