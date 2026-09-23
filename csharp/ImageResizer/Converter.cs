// JPG/PNG/WEBP 一括リサイズ・フォーマット変換ツール - 変換ロジック（GUI 非依存）
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Processors.Transforms;
using Image = SixLabors.ImageSharp.Image;

namespace ImageResizer;

/// <summary>出力フォーマット。Keep は元ファイルの拡張子をそのまま使う。</summary>
public enum OutputFormat { Keep, Jpeg, Png, Webp }

public record ConvertOptions
{
    public required int LongEdge { get; init; }          // 長辺の指定サイズ（KeepSize ならリサイズしない）
    public required string Algorithm { get; init; }      // Bilinear / Bicubic / Lanczos
    public bool Lowercase { get; init; }                 // ファイル名と拡張子を小文字化
    public string ReplaceSearch { get; init; } = "";     // 置換元文字列（空なら置換しない）
    public string ReplaceWith { get; init; } = "";       // 置換先文字列
    public string Suffix { get; init; } = "";            // ファイル名末尾に付与する文字列
    public bool Overwrite { get; init; }                 // true=上書き / false=スキップ
    public bool NoUpscale { get; init; } = true;         // 長辺が指定値より小さい画像は拡大しない
    public bool StripMetadata { get; init; } = true;     // EXIF・コメント等のメタデータを削除
    public OutputFormat Format { get; init; } = OutputFormat.Keep;   // 出力フォーマット
}

public class ConvertStats
{
    public int Total { get; set; }
    public int Converted { get; set; }
    public int Skipped { get; set; }
    public int Error { get; set; }
}

public static class Converter
{
    public static readonly int[] SizePresets = { 1600, 1200, 600, 560 };
    public static readonly string[] Algorithms = { "Bilinear", "Bicubic", "Lanczos" };

    /// <summary>LongEdge に指定するとリサイズを行わない番兵値。</summary>
    public const int KeepSize = -1;

    /// <summary>出力形式ラジオの表示名（Python 版 FORMAT_LABELS と同じ並び）。</summary>
    public static readonly (string Label, OutputFormat Value)[] FormatLabels =
    {
        ("元のまま", OutputFormat.Keep),
        ("JPG", OutputFormat.Jpeg),
        ("PNG", OutputFormat.Png),
        ("WEBP", OutputFormat.Webp),
    };

    private const int JpegQuality = 90;
    private const int WebpQuality = 90;

    private static readonly HashSet<string> TargetExts =
        new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".webp" };

    public static IResampler GetResampler(string name) => name switch
    {
        "Bilinear" => KnownResamplers.Triangle,
        "Bicubic" => KnownResamplers.Bicubic,
        "Lanczos" => KnownResamplers.Lanczos3,
        _ => throw new ArgumentException($"未知のアルゴリズム: {name}"),
    };

    /// <summary>出力フォーマットに対応する拡張子。Keep なら元の拡張子を返す。</summary>
    public static string ExtensionFor(OutputFormat format, string srcExt) => format switch
    {
        OutputFormat.Jpeg => ".jpg",
        OutputFormat.Png => ".png",
        OutputFormat.Webp => ".webp",
        _ => srcExt,
    };

    /// <summary>変換オプションに従って出力ファイル名を組み立てる。</summary>
    public static string BuildDestName(string fileName, ConvertOptions opts)
    {
        string stem = Path.GetFileNameWithoutExtension(fileName);
        string ext = ExtensionFor(opts.Format, Path.GetExtension(fileName));
        if (!string.IsNullOrEmpty(opts.ReplaceSearch))
            stem = stem.Replace(opts.ReplaceSearch, opts.ReplaceWith);
        stem += opts.Suffix;
        if (opts.Lowercase)
        {
            stem = stem.ToLowerInvariant();
            ext = ext.ToLowerInvariant();
        }
        return stem + ext;
    }

    /// <summary>ログ見出し用に、サイズ・形式・アルゴリズムを1行にまとめる。</summary>
    public static string DescribeOptions(ConvertOptions opts)
    {
        string size = opts.LongEdge == KeepSize
            ? "サイズ変更なし" : $"長辺{opts.LongEdge}px";
        string fmt = FormatLabels.First(f => f.Value == opts.Format).Label;
        return $"{size} / 形式{fmt} / {opts.Algorithm}";
    }

    /// <summary>フォルダ直下の JPG/PNG/WEBP を列挙する。</summary>
    public static List<string> CollectTargets(string folder) =>
        Directory.EnumerateFiles(folder)
            .Where(f => TargetExts.Contains(Path.GetExtension(f)))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>1ファイルを変換する。戻り値は "converted" / "skipped"。</summary>
    public static string ConvertOne(string src, string dst, ConvertOptions opts)
    {
        if (File.Exists(dst) && !opts.Overwrite)
            return "skipped";

        using var image = Image.Load(src);

        // アニメーション WEBP 等は先頭フレームだけを静止画として扱う。
        // （残すと PNG 出力が APNG になり、Python 版とも挙動が食い違うため）
        while (image.Frames.Count > 1)
            image.Frames.RemoveFrame(image.Frames.Count - 1);

        // EXIF の回転情報を反映（反映後に Orientation タグは除去される）
        image.Mutate(x => x.AutoOrient());

        int longNow = Math.Max(image.Width, image.Height);
        bool skipResize = opts.LongEdge == KeepSize ||
                          longNow == opts.LongEdge ||
                          (opts.NoUpscale && longNow < opts.LongEdge);
        if (!skipResize)
        {
            double scale = (double)opts.LongEdge / longNow;
            int w = Math.Max(1, (int)Math.Round(image.Width * scale));
            int h = Math.Max(1, (int)Math.Round(image.Height * scale));
            image.Mutate(x => x.Resize(w, h, GetResampler(opts.Algorithm)));
        }

        if (opts.StripMetadata)
        {
            // ICC プロファイル（色管理情報）は保持し、それ以外を削除する。
            // JPEG の COM マーカーは ImageSharp が読み込まないため再保存で消える。
            image.Metadata.ExifProfile = null;
            image.Metadata.XmpProfile = null;
            image.Metadata.IptcProfile = null;
            image.Metadata.GetPngMetadata().TextData.Clear();
        }

        // 出力拡張子で保存形式が決まる（BuildDestName が Format を反映済み）
        string ext = Path.GetExtension(dst).ToLowerInvariant();
        if (ext is ".jpg" or ".jpeg")
        {
            // JPEG は透過を扱えないため、透過部分を白で塗りつぶして不透明化する
            image.Mutate(x => x.BackgroundColor(SixLabors.ImageSharp.Color.White));
            image.Save(dst, new JpegEncoder { Quality = JpegQuality });
        }
        else if (ext == ".webp")
        {
            image.Save(dst, new WebpEncoder { Quality = WebpQuality });
        }
        else
        {
            image.Save(dst, new PngEncoder());
        }
        return "converted";
    }

    /// <summary>フォルダ単位の変換。log / progress で進捗を通知する。</summary>
    public static ConvertStats ProcessFolder(
        string inDir, string outDir, ConvertOptions opts,
        Action<string> log, Action<int, int> progress,
        CancellationToken ct = default)
    {
        var files = CollectTargets(inDir);
        var stats = new ConvertStats { Total = files.Count };
        log($"対象ファイル: {files.Count}件");
        progress(0, files.Count);

        Directory.CreateDirectory(outDir);

        for (int i = 0; i < files.Count; i++)
        {
            if (ct.IsCancellationRequested)
            {
                log("―― 中断しました ――");
                break;
            }
            string src = files[i];
            string srcName = Path.GetFileName(src);
            string dst = Path.Combine(outDir, BuildDestName(srcName, opts));
            try
            {
                string result = ConvertOne(src, dst, opts);
                if (result == "skipped")
                {
                    stats.Skipped++;
                    log($"スキップ: {srcName} → {Path.GetFileName(dst)}（同名ファイルあり）");
                }
                else
                {
                    stats.Converted++;
                    log($"変換: {srcName} → {Path.GetFileName(dst)}");
                }
            }
            catch (Exception e)
            {
                stats.Error++;
                log($"エラー: {srcName} … {e.Message}");
            }
            progress(i + 1, files.Count);
        }
        return stats;
    }
}
