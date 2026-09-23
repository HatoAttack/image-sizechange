// Converter の動作確認（GUI なし・Python 版のテストと同内容）
using System.Text;
using ImageResizer;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Png.Chunks;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Image = SixLabors.ImageSharp.Image;

Console.OutputEncoding = Encoding.UTF8;

string baseDir = Path.Combine(Path.GetTempPath(), "image_resizer_cs_test");
string inDir = Path.Combine(baseDir, "in");
string outDir = Path.Combine(baseDir, "out");
bool failed = false;

void Check(bool cond, string msg)
{
    Console.WriteLine((cond ? "OK  " : "NG  ") + msg);
    if (!cond) failed = true;
}

ConvertOptions Opts(int longEdge = 1200, string algo = "Lanczos",
    bool lowercase = true, string search = "ABC", string with = "xyz",
    string suffix = "_s", bool overwrite = false, bool strip = true,
    OutputFormat format = OutputFormat.Keep) => new()
{
    LongEdge = longEdge, Algorithm = algo, Lowercase = lowercase,
    ReplaceSearch = search, ReplaceWith = with, Suffix = suffix,
    Overwrite = overwrite, NoUpscale = true, StripMetadata = strip,
    Format = format,
};

ConvertStats Run(string outPath, ConvertOptions opts) =>
    Converter.ProcessFolder(inDir, outPath, opts, _ => { }, (_, _) => { });

// ---- テストデータ作成 ----
if (Directory.Exists(baseDir)) Directory.Delete(baseDir, true);
Directory.CreateDirectory(inDir);

// 横長JPG 2000x1000（大文字名・EXIF付き）
// ※ JPEG コメント(COM)は ImageSharp が読み書きに対応しないため常に除去される
using (var img = new Image<Rgb24>(2000, 1000, new Rgb24(255, 0, 0)))
{
    var exif = new ExifProfile();
    exif.SetValue(ExifTag.Make, "TestMaker");
    exif.SetValue(ExifTag.Model, "TestCamera");
    img.Metadata.ExifProfile = exif;
    img.Save(Path.Combine(inDir, "PHOTO_ABC.JPG"), new JpegEncoder { Quality = 95 });
}
// 縦長PNG 800x1900（透過あり・tEXt付き）
using (var img = new Image<Rgba32>(800, 1900, new Rgba32(0, 255, 0, 128)))
{
    img.Metadata.GetPngMetadata().TextData.Add(
        new PngTextData("Comment", "png secret", "", ""));
    img.Save(Path.Combine(inDir, "Sample_Img.PNG"), new PngEncoder());
}
// 小さいJPG 400x300（拡大しない対象）
using (var img = new Image<Rgb24>(400, 300, new Rgb24(0, 0, 255)))
    img.Save(Path.Combine(inDir, "small.jpg"), new JpegEncoder());
// 半透明WEBP 1400x700（WEBP 入力の対象確認用）
using (var img = new Image<Rgba32>(1400, 700, new Rgba32(0, 255, 0, 128)))
    img.Save(Path.Combine(inDir, "shot.webp"), new WebpEncoder { Quality = 95 });
// 対象外ファイル
File.WriteAllText(Path.Combine(inDir, "note.txt"), "ignore me");

// 入力にメタデータが入っていることの前提確認
using (var img = Image.Load(Path.Combine(inDir, "PHOTO_ABC.JPG")))
{
    Check(img.Metadata.ExifProfile is { Values.Count: > 0 },
          "入力JPGにEXIFあり");
}

// ---- 1回目: 変換（小文字化+置換+末尾付与、メタデータ削除ON） ----
var stats = Run(outDir, Opts());
Check(stats is { Total: 4, Converted: 4, Skipped: 0, Error: 0 },
      $"1回目 stats: 変換{stats.Converted} スキップ{stats.Skipped} エラー{stats.Error}");

var names = Directory.GetFiles(outDir).Select(Path.GetFileName).OrderBy(n => n).ToArray();
Check(names.SequenceEqual(new[]
      { "photo_xyz_s.jpg", "sample_img_s.png", "shot_s.webp", "small_s.jpg" }),
      $"出力ファイル名（小文字化+置換+末尾付与）: {string.Join(", ", names)}");

using (var img = Image.Load(Path.Combine(outDir, "photo_xyz_s.jpg")))
{
    Check(img.Width == 1200 && img.Height == 600, $"横長JPG 長辺1200: {img.Width}x{img.Height}");
    Check(img.Metadata.ExifProfile is null or { Values.Count: 0 },
          "EXIF削除（デフォルトON）");
}
using (var img = Image.Load(Path.Combine(outDir, "sample_img_s.png")))
{
    Check(img.Width == 505 && img.Height == 1200, $"縦長PNG 長辺1200: {img.Width}x{img.Height}");
    Check(img.Metadata.GetPngMetadata().ColorType == PngColorType.RgbWithAlpha,
          $"PNG 透過保持: {img.Metadata.GetPngMetadata().ColorType}");
    Check(img.Metadata.GetPngMetadata().TextData.Count == 0, "PNG tEXt削除");
}
using (var img = Image.Load(Path.Combine(outDir, "small_s.jpg")))
    Check(img.Width == 400 && img.Height == 300, $"小画像は拡大しない: {img.Width}x{img.Height}");
// WEBP は入力対象であり、「元のまま」では WEBP として保存される
using (var img = Image.Load(Path.Combine(outDir, "shot_s.webp")))
{
    Check(img.Width == 1200 && img.Height == 600, $"WEBP 長辺1200: {img.Width}x{img.Height}");
    Check(img.Metadata.DecodedImageFormat is WebpFormat,
          $"元のままなら WEBP のまま: {img.Metadata.DecodedImageFormat?.Name}");
}

// ---- 2回目: 同名スキップ ----
stats = Run(outDir, Opts());
Check(stats is { Converted: 0, Skipped: 4 }, $"同名スキップ: スキップ{stats.Skipped}");

// ---- 3回目: 上書き（サイズ600） ----
stats = Run(outDir, Opts(longEdge: 600, overwrite: true));
Check(stats.Converted == 4, $"上書き: 変換{stats.Converted}");
using (var img = Image.Load(Path.Combine(outDir, "photo_xyz_s.jpg")))
    Check(img.Width == 600 && img.Height == 300, $"上書き後 長辺600: {img.Width}x{img.Height}");

// ---- 4回目: Bilinear / Bicubic + 名前変更なし ----
foreach (string algo in new[] { "Bilinear", "Bicubic" })
{
    string out2 = Path.Combine(baseDir, "out_" + algo);
    stats = Run(out2, Opts(algo: algo, lowercase: false, search: "", with: "", suffix: ""));
    var names2 = Directory.GetFiles(out2).Select(Path.GetFileName).OrderBy(n => n).ToArray();
    Check(stats.Converted == 4 &&
          names2.SequenceEqual(new[]
          { "PHOTO_ABC.JPG", "Sample_Img.PNG", "shot.webp", "small.jpg" }),
          $"{algo} + 名前変更なし: {string.Join(", ", names2)}");
}

// ---- 5回目: メタデータ削除オフ（オプトアウト）で EXIF が保持されるか ----
string outKeep = Path.Combine(baseDir, "out_keepmeta");
stats = Run(outKeep, Opts(strip: false));
using (var img = Image.Load(Path.Combine(outKeep, "photo_xyz_s.jpg")))
{
    string? make = null;
    if (img.Metadata.ExifProfile?.TryGetValue(ExifTag.Make, out var v) == true)
        make = v.Value;
    Check(make == "TestMaker", $"削除オフ時はEXIF保持: Make={make}");
}

// ---- 6回目: 出力形式を JPG に統一（透過PNG / WEBP → JPG） ----
string outJpg = Path.Combine(baseDir, "out_jpg");
stats = Run(outJpg, Opts(format: OutputFormat.Jpeg));
var namesJpg = Directory.GetFiles(outJpg).Select(Path.GetFileName).OrderBy(n => n).ToArray();
Check(stats.Converted == 4 &&
      namesJpg.SequenceEqual(new[]
      { "photo_xyz_s.jpg", "sample_img_s.jpg", "shot_s.jpg", "small_s.jpg" }),
      $"出力形式JPG（拡張子が .jpg に統一）: {string.Join(", ", namesJpg)}");
using (var img = Image.Load<Rgb24>(Path.Combine(outJpg, "sample_img_s.jpg")))
{
    Check(img.Width == 505 && img.Height == 1200,
          $"PNG→JPG 長辺1200: {img.Width}x{img.Height}");
    // 半透明の緑(0,255,0,128) を白に合成 → おおよそ (127,255,127)。
    // 黒で塗りつぶすと R/B が 0 付近になるため、白合成の確認になる。
    var px = img[img.Width / 2, img.Height / 2];
    Check(px.R > 100 && px.B > 100,
          $"PNG→JPG は透過を白で塗りつぶす: ({px.R},{px.G},{px.B})");
}
using (var img = Image.Load<Rgb24>(Path.Combine(outJpg, "shot_s.jpg")))
{
    Check(img.Width == 1200 && img.Height == 600,
          $"WEBP→JPG 長辺1200: {img.Width}x{img.Height}");
    var px = img[img.Width / 2, img.Height / 2];
    Check(px.R > 100 && px.B > 100,
          $"WEBP→JPG は透過を白で塗りつぶす: ({px.R},{px.G},{px.B})");
}

// ---- 7回目: 出力形式 PNG + サイズ変更なし（フォーマット変換のみ） ----
string outPng = Path.Combine(baseDir, "out_png_keep");
stats = Run(outPng, Opts(longEdge: Converter.KeepSize, format: OutputFormat.Png));
var namesPng = Directory.GetFiles(outPng).Select(Path.GetFileName).OrderBy(n => n).ToArray();
Check(stats.Converted == 4 &&
      namesPng.SequenceEqual(new[]
      { "photo_xyz_s.png", "sample_img_s.png", "shot_s.png", "small_s.png" }),
      $"出力形式PNG（拡張子が .png に統一）: {string.Join(", ", namesPng)}");
using (var img = Image.Load(Path.Combine(outPng, "photo_xyz_s.png")))
    Check(img.Width == 2000 && img.Height == 1000,
          $"サイズ変更なしは元寸法のまま: {img.Width}x{img.Height}");
using (var img = Image.Load(Path.Combine(outPng, "sample_img_s.png")))
    Check(img.Metadata.GetPngMetadata().ColorType == PngColorType.RgbWithAlpha,
          $"PNG出力では透過を保持: {img.Metadata.GetPngMetadata().ColorType}");
using (var img = Image.Load<Rgba32>(Path.Combine(outPng, "shot_s.png")))
{
    Check(img.Width == 1400 && img.Height == 700,
          $"WEBP→PNG サイズ変更なし: {img.Width}x{img.Height}");
    Check(img[img.Width / 2, img.Height / 2].A < 200,
          $"WEBP→PNG は透過を保持: A={img[img.Width / 2, img.Height / 2].A}");
}

// ---- 8回目: 出力形式 WEBP に統一（JPG/PNG → WEBP） ----
string outWebp = Path.Combine(baseDir, "out_webp");
stats = Run(outWebp, Opts(format: OutputFormat.Webp));
var namesWebp = Directory.GetFiles(outWebp).Select(Path.GetFileName).OrderBy(n => n).ToArray();
Check(stats.Converted == 4 &&
      namesWebp.SequenceEqual(new[]
      { "photo_xyz_s.webp", "sample_img_s.webp", "shot_s.webp", "small_s.webp" }),
      $"出力形式WEBP（拡張子が .webp に統一）: {string.Join(", ", namesWebp)}");
using (var img = Image.Load(Path.Combine(outWebp, "photo_xyz_s.webp")))
{
    Check(img.Width == 1200 && img.Height == 600,
          $"JPG→WEBP 長辺1200: {img.Width}x{img.Height}");
    Check(img.Metadata.DecodedImageFormat is WebpFormat,
          $"WEBP として保存されている: {img.Metadata.DecodedImageFormat?.Name}");
}

// ---- 9回目: アニメーションWEBPは先頭フレームだけの静止画になる ----
// （フレームを残すと PNG 出力が APNG になり、Python 版と挙動が食い違う）
{
    string animIn = Path.Combine(baseDir, "anim_in");
    Directory.CreateDirectory(animIn);
    using (var img = new Image<Rgba32>(900, 600, new Rgba32(255, 0, 0, 255)))
    {
        using var f2 = new Image<Rgba32>(900, 600, new Rgba32(0, 255, 0, 255));
        img.Frames.AddFrame(f2.Frames.RootFrame);
        img.Save(Path.Combine(animIn, "anim.webp"), new WebpEncoder());
    }
    using (var img = Image.Load(Path.Combine(animIn, "anim.webp")))
        Check(img.Frames.Count == 2, $"入力はアニメーションWEBP: {img.Frames.Count}フレーム");

    foreach (var (fmt, name) in new[]
             { (OutputFormat.Keep, "anim.webp"), (OutputFormat.Png, "anim.png"),
               (OutputFormat.Jpeg, "anim.jpg") })
    {
        string animOut = Path.Combine(baseDir, "anim_out_" + fmt);
        Converter.ProcessFolder(animIn, animOut,
            Opts(longEdge: Converter.KeepSize, lowercase: false, search: "", with: "",
                 suffix: "", format: fmt),
            _ => { }, (_, _) => { });
        using var img = Image.Load(Path.Combine(animOut, name));
        Check(img.Frames.Count == 1,
              $"アニメーションWEBP→{fmt} は1フレーム: {img.Frames.Count}");
    }
}

// ================ Combiner / Cropper（連結・切り抜き）のテスト ================
// Python 版 image_editor.py のテストと同じ期待値

Image<Rgba32> Mk(int w, int h, byte r, byte g, byte b) =>
    new(w, h, new Rgba32(r, g, b, 255));

{
    using var a = Mk(100, 200, 255, 0, 0);
    using var b = Mk(300, 150, 0, 255, 0);
    using var c = Mk(120, 120, 0, 0, 255);
    var imgs = new List<Image<Rgba32>> { a, b, c };
    var white = new Rgba32(255, 255, 255, 255);
    var clear = new Rgba32(0, 0, 0, 0);

    // 横連結 高さを最大に揃え、間隔10 余白5 中央整列
    using (var img = Combiner.CombineLinear(imgs, true, "max", 0, "center", 10, 5, white))
        Check(img.Width == 730 && img.Height == 210,
              $"横連結(max): {img.Width}x{img.Height} (期待 730x210)");

    // 縦連結 そのまま 左寄せ
    using (var img = Combiner.CombineLinear(imgs, false, "none", 0, "start", 0, 0, clear))
        Check(img.Width == 300 && img.Height == 470,
              $"縦連結(none): {img.Width}x{img.Height} (期待 300x470)");

    // 指定px 揃え（高さ100）
    using (var img = Combiner.CombineLinear(imgs, true, "fixed", 100, "start", 0, 0, white))
        Check(img.Width == 350 && img.Height == 100,
              $"横連結(fixed100): {img.Width}x{img.Height} (期待 350x100)");

    // グリッド 2列 元最大 間隔4 余白2
    using (var img = Combiner.CombineGrid(imgs, 2, "none", 0, 4, 2, white))
        Check(img.Width == 608 && img.Height == 408,
              $"グリッド2列(none): {img.Width}x{img.Height} (期待 608x408)");

    // グリッド 3列 セル150角
    using (var img = Combiner.CombineGrid(imgs, 3, "fixed", 150, 0, 0, clear))
        Check(img.Width == 450 && img.Height == 150,
              $"グリッド3列(fixed150): {img.Width}x{img.Height} (期待 450x150)");
}

// 色パース
{
    var col = Combiner.ParseColor("#f80", false);
    Check(col is { R: 255, G: 136, B: 0, A: 255 }, $"色 #f80: ({col.R},{col.G},{col.B},{col.A})");
    var tr = Combiner.ParseColor("#ffffff", true);
    Check(tr.A == 0, $"透過指定: A={tr.A}");
}

// 保存（JPEG の透過フラット化 / PNG / WEBP）
{
    string saveDir = Path.Combine(baseDir, "combine_save");
    Directory.CreateDirectory(saveDir);
    using var img = new Image<Rgba32>(60, 40, new Rgba32(255, 0, 0, 128));
    foreach (string name in new[] { "out.png", "out.jpg", "out.webp" })
    {
        string dst = Path.Combine(saveDir, name);
        Combiner.SaveByExtension(img, dst);
        using var loaded = Image.Load(dst);
        Check(loaded.Width == 60 && loaded.Height == 40,
              $"保存/再読込 {name}: {loaded.Width}x{loaded.Height}");
    }
}

// Cropper: 中央矩形とアスペクト反転
{
    var (x, y, w, h) = Cropper.CenterRect(400, 300, 1.0);
    Check(x == 50 && y == 0 && w == 300 && h == 300,
          $"CenterRect 1:1 (400x300): x={x} y={y} {w}x{h} (期待 50,0,300x300)");

    var r169 = Cropper.CenterRect(400, 300, 16.0 / 9);
    Check(r169.W == 400 && Math.Abs(r169.H - 225) < 0.01 && Math.Abs(r169.Y - 37.5) < 0.01,
          $"CenterRect 16:9: y={r169.Y} {r169.W}x{r169.H} (期待 37.5, 400x225)");

    Check(Math.Abs((Cropper.Flip(4.0 / 3, true) ?? 0) - 0.75) < 1e-9, "反転 4:3 → 3:4 (0.75)");
    Check(Cropper.Flip(null, true) is null, "自由比は反転の影響なし");

    // 実クロップ: 400x300 を 1:1 中央 → 300x300
    string cropDir = Path.Combine(baseDir, "crop_save");
    Directory.CreateDirectory(cropDir);
    using var src = Mk(400, 300, 255, 128, 0);
    var box = Cropper.ClampBox(x, y, x + w, y + h, 400, 300);
    src.Mutate(m => m.Crop(box));
    string cropDst = Path.Combine(cropDir, "center_crop.png");
    Combiner.SaveByExtension(src, cropDst);
    using (var loaded = Image.Load(cropDst))
        Check(loaded.Width == 300 && loaded.Height == 300,
              $"1:1 中央クロップ保存: {loaded.Width}x{loaded.Height}");
}

Console.WriteLine();
Console.WriteLine(failed ? "FAILED" : "ALL PASSED");
return failed ? 1 : 0;
