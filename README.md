# 画像リサイズ変換（JPG / PNG）

フォルダ内の JPG / PNG を長辺指定で一括リサイズする Windows 用 GUI ツール。

**C# 版（推奨）** と Python 版の2つがあり、機能は同じです。

## 使い方

### C# 版（推奨・インストール不要）

[Releases](https://github.com/HatoAttack/image-sizechange/releases) から `ImageResizer.exe` をダウンロードしてダブルクリックするだけ。ランタイム等のインストールは不要で、この exe 1個を他の PC にコピーしても動きます。

ソースは `csharp/` 以下（.NET 8 + WinForms + ImageSharp）。再ビルドは:

```
dotnet publish csharp/ImageResizer -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
```

動作テストは `dotnet run --project csharp/ImageResizer.Tests`。

### Python 版

`start.bat` をダブルクリック（要 Python 3.10 以降 + Pillow）。ソースは `image_resizer.py`。

## 機能

| 項目 | 内容 |
|---|---|
| 入力 | フォルダ単位（フォルダ直下の `.jpg` `.jpeg` `.png` が対象） |
| サイズ | 長辺 1600 / 1200 / 600 / 560 px から選択、または任意のピクセル数を直接指定 |
| アルゴリズム | Bilinear / Bicubic / Lanczos から選択 |
| ファイル名処理 | 小文字化（拡張子含む）・文字列置換・末尾に任意文字列付与 |
| 出力先 | 任意のフォルダを選択可能 |
| 同名ファイル | スキップ / 上書き を選択可能 |
| メタデータ削除 | EXIF・コメント等を削除（デフォルトON、チェックを外すと保持） |

## 補足

- ファイル名処理の適用順は「置換 → 末尾付与 → 小文字化」です。
- EXIF の回転情報は反映したうえで変換するため、メタデータを削除しても画像の向きは崩れません。
- メタデータ削除時も ICC プロファイル(色管理情報)は保持します。削除をオフにすると EXIF は保持されます。
  - C# 版では JPEG コメント(COM)は削除オフでも保持されません(ライブラリの仕様)。Python 版はオフなら保持します。
- 「拡大しない」チェックを外すと、長辺が指定より小さい画像も指定サイズまで拡大します。
- JPEG は品質 90 で保存します。
- 入力フォルダ選択時、出力フォルダが未指定なら `入力フォルダ\resized` が自動入力されます。
