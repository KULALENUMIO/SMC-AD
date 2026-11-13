# Third-party notices（他者の著作物とライセンス）

SMC-AD は、次の他者の著作物を含むか、それをもとにしています。

## 1. SM-Converter（変換ツール本体）
- https://github.com/QuestionableM/SM-Converter
- Copyright (C) QuestionableM and contributors
- **GNU General Public License v3.0**
- 使い方: `SMConverter/` は v1.2.4-pre を SMC-AD Project が改変したもの。改変点は `SMConverter/SMC-AD_CHANGES.md`、
  ライセンス全文は `SMConverter/LICENSE`（一番上の `LICENSE` と同じ）

## 2. SM-Converter-Plugin（Blender 用のマテリアル設定）
- https://github.com/QuestionableM/SM-Converter-Plugin
- **MIT License**
- 使い方: マテリアルのノード構成（SM の見た目の作り方）を、Blender アドオン（`addon/smc_ad`）と
  Unity パッケージ（`unity/com.smcad.unity` のシェーダー）に移植した。コードの直接のコピーではない

```
MIT License

Copyright (c) 2022 QuestionableM

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## 3. SM-Converter が同梱しているライブラリ（`SMConverter/Dependencies/`、元のまま）
元の SM-Converter のリポジトリに入っているもので、SMC-AD では変更していない。各ファイルの先頭の
著作権表示はそのまま残している。

| ライブラリ | ライセンス | 著作権者 | 入っているもの |
|---|---|---|---|
| Assimp | BSD 3-Clause | assimp team | ヘッダー、ライブラリ、DLL |
| FreeImage | FreeImage Public License 1.0 または GPL v2 / v3 の選択（SMC-AD は GPL v3 として使う） | FreeImage project | ヘッダー、ライブラリ、DLL |
| PerlinNoise (Siv3D) | MIT | Ryo Suzuki | ヘッダー |
| ValveFileVDF | MIT | Matthias Moeller | ヘッダー |
| emhash | MIT | Huang Yuanbing | ヘッダー |
| GLM | MIT（または Happy Bunny License） | G-Truc Creation | ヘッダー |
| LZ4 | BSD 2-Clause | Yann Collet | ソース |
| nlohmann/json | MIT | Niels Lohmann | ヘッダー |
| simdjson | Apache-2.0 または MIT | The simdjson authors | ソース（中に含まれる部品は、それぞれのファイルに記載のライセンス） |
| Version Counter（`VersionCounter.exe`） | SM-Converter の一部（ビルド番号を数える道具） | QuestionableM | 実行ファイル |

ビルドした SMConverterCLI.exe を配る場合は、Assimp（BSD 3-Clause）と LZ4（BSD 2-Clause）の著作権表示と
条件文を一緒に配ること（それぞれのヘッダーの先頭にある）。

## 4. Scrap Mechanic
- Scrap Mechanic は Axolot Games の商標・著作物です。SMC-AD は Axolot Games とは関係のない非公式のツールです
- このリポジトリには、ゲームのデータ（モデル・テクスチャなど）は入っていません。変換ツールは、利用者の PC に
  入っているゲームから読み込みます
- 変換して作ったモデルにはゲームのテクスチャが含まれます。それを配ったり公開したりするときは、Axolot Games の
  利用規約に従ってください
