// Converter の動作確認（GUI なし・Python 版のテストと同内容）
using System.Text;
using ImageResizer;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Png.Chunks;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;
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
    string suffix = "_s", bool overwrite = false, bool strip = true) => new()
{
    LongEdge = longEdge, Algorithm = algo, Lowercase = lowercase,
    ReplaceSearch = search, ReplaceWith = with, Suffix = suffix,
    Overwrite = overwrite, NoUpscale = true, StripMetadata = strip,
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
Check(stats is { Total: 3, Converted: 3, Skipped: 0, Error: 0 },
      $"1回目 stats: 変換{stats.Converted} スキップ{stats.Skipped} エラー{stats.Error}");

var names = Directory.GetFiles(outDir).Select(Path.GetFileName).OrderBy(n => n).ToArray();
Check(names.SequenceEqual(new[] { "photo_xyz_s.jpg", "sample_img_s.png", "small_s.jpg" }),
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

// ---- 2回目: 同名スキップ ----
stats = Run(outDir, Opts());
Check(stats is { Converted: 0, Skipped: 3 }, $"同名スキップ: スキップ{stats.Skipped}");

// ---- 3回目: 上書き（サイズ600） ----
stats = Run(outDir, Opts(longEdge: 600, overwrite: true));
Check(stats.Converted == 3, $"上書き: 変換{stats.Converted}");
using (var img = Image.Load(Path.Combine(outDir, "photo_xyz_s.jpg")))
    Check(img.Width == 600 && img.Height == 300, $"上書き後 長辺600: {img.Width}x{img.Height}");

// ---- 4回目: Bilinear / Bicubic + 名前変更なし ----
foreach (string algo in new[] { "Bilinear", "Bicubic" })
{
    string out2 = Path.Combine(baseDir, "out_" + algo);
    stats = Run(out2, Opts(algo: algo, lowercase: false, search: "", with: "", suffix: ""));
    var names2 = Directory.GetFiles(out2).Select(Path.GetFileName).OrderBy(n => n).ToArray();
    Check(stats.Converted == 3 &&
          names2.SequenceEqual(new[] { "PHOTO_ABC.JPG", "Sample_Img.PNG", "small.jpg" }),
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

Console.WriteLine();
Console.WriteLine(failed ? "FAILED" : "ALL PASSED");
return failed ? 1 : 0;
