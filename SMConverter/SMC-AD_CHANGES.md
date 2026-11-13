# SMC-AD による変更（Modification notice）

このフォルダは、QuestionableM による **SM-Converter v1.2.4-pre**（https://github.com/QuestionableM/SM-Converter 、
GPL-3.0）を、SMC-AD Project が改変したものです。改変後も GNU General Public License v3.0 で配布します
（全文は `LICENSE`）。

This directory is a modified version of SM-Converter v1.2.4-pre by QuestionableM, licensed under the
GNU General Public License v3.0. Modified by the SMC-AD Project, 2026-11. The modified work is also
distributed under GPL-3.0 (see `LICENSE`).

- 元にした版: v1.2.4-pre（コミット 0b73726、ビルド番号 2860）。元の zip はリポジトリの外に保管
- 変更した期間: 2026-11-13 〜 2026-11-14
- 変更の内容（詳細は一番上の `CHANGELOG.md`）
  - glTF（.glb）での書き出し（`-gltf`）。ジョイントの階層、ボディごとのオブジェクト、塗装色の頂点カラー、
    マテリアル情報を extras に入れる
  - 見えない面の削除（`-cullhidden`）、ブロックの面をまとめる（`-optimizeblocks`）、重なった面のちらつき対策
    （`-fixcoplanar`）
  - 新しい形式の renderable（`"textures": {...}`）に対応し、パーツの面が消える不具合を直した
  - ブロックだけのボディが `-optimizeblocks` で消える不具合を直した
  - ブループリント名（description.json）を書き出しに入れる

## 変更したファイル（元の v1.2.4-pre から）
```
Code/Converter/BlueprintConverter/BlueprintConverter.cpp
Code/Converter/BlueprintConverter/BlueprintConverter.hpp
Code/Converter/ConvertSettings.hpp
Code/Converter/Entity/Block.cpp
Code/Converter/Entity/Block.hpp
Code/Converter/Entity/Blueprint.cpp
Code/Converter/Entity/Body.hpp
Code/Converter/Entity/Entity.hpp
Code/Converter/Entity/Joint.cpp
Code/Converter/Entity/Joint.hpp
Code/Converter/Entity/Part.cpp
Code/Converter/Entity/Part.hpp
Code/Converter/Entity/Wedge.cpp
Code/Converter/Entity/Wedge.hpp
Code/ObjectDatabase/ModelStorage.cpp
Code/ObjectDatabase/ObjectData.hpp
Code/ObjectDatabase/Readers/BlockListLoader.cpp
Code/ObjectDatabase/Readers/DefaultLoader.cpp
Code/ObjectDatabase/Readers/DefaultLoader.hpp
Code/ObjectDatabase/Readers/PartListLoader.cpp
Code/ObjectDatabase/SubMeshData.cpp
Code/ObjectDatabase/SubMeshData.hpp
Code/cli_parser.cpp
Code/cli_parser.hpp
Code/main_cli.cpp
SM-Converter.vcxproj
SMConverterCLI.vcxproj
SMConverterQt.vcxproj
```

## 追加したファイル
```
Code/Converter/BlockMesher/      ブロックの面の生成・まとめ・見えない面の削除
Code/Converter/Gltf/             glTF（.glb）の書き出し
Resources/SurfacePresets.json    材質のプリセット（書き出しの extras 用）
SMC-AD_CHANGES.md                このファイル
```

## 同梱しているライブラリ
`Dependencies/` は元の v1.2.4-pre のまま（変更なし）。各ライブラリのライセンスは、一番上の
`THIRD_PARTY_NOTICES.md` にまとめた。
