// 画像連結ロジック（GUI 非依存・Python 版 image_editor.py と同仕様）
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Image = SixLabors.ImageSharp.Image;
using Point = SixLabors.ImageSharp.Point;

namespace ImageResizer;

public static class Combiner
{
    private const int JpegQuality = 90;
    private const int WebpQuality = 90;

    /// <summary>'#RGB' / '#RRGGBB' を Rgba32 へ。透過指定時は完全透明を返す。</summary>
    public static Rgba32 ParseColor(string hex, bool transparent)
    {
        if (transparent)
            return new Rgba32(0, 0, 0, 0);
        string s = hex.Trim().TrimStart('#');
        if (s.Length == 3)
            s = string.Concat(s.Select(c => $"{c}{c}"));
        if (s.Length != 6)
            throw new ArgumentException($"色の指定が不正です: {hex}");
        return new Rgba32(
            System.Convert.ToByte(s[..2], 16),
            System.Convert.ToByte(s.Substring(2, 2), 16),
            System.Convert.ToByte(s.Substring(4, 2), 16));
    }

    /// <summary>byHeight=true なら高さ、false なら幅を target に合わせ他方を比例させる。
    /// サイズが同じならそのまま返す（呼び出し側は戻り値の所有権に注意）。</summary>
    private static Image<Rgba32> ResizeTo(Image<Rgba32> img, int target, bool byHeight)
    {
        int w = img.Width, h = img.Height;
        if (byHeight)
        {
            if (h == target) return img;
            return img.Clone(x => x.Resize(
                Math.Max(1, (int)Math.Round((double)w * target / h)), target,
                KnownResamplers.Lanczos3));
        }
        if (w == target) return img;
        return img.Clone(x => x.Resize(
            target, Math.Max(1, (int)Math.Round((double)h * target / w)),
            KnownResamplers.Lanczos3));
    }

    /// <summary>アスペクト比を保ったまま cw×ch の枠に収まるよう縮小する（拡大はしない）。</summary>
    private static Image<Rgba32> Contain(Image<Rgba32> img, int cw, int ch)
    {
        double s = Math.Min((double)cw / img.Width, (double)ch / img.Height);
        if (s >= 1) return img;
        return img.Clone(x => x.Resize(
            Math.Max(1, (int)Math.Round(img.Width * s)),
            Math.Max(1, (int)Math.Round(img.Height * s)),
            KnownResamplers.Lanczos3));
    }

    private static int AlignOffset(int total, int size, string align) => align switch
    {
        "center" => (total - size) / 2,
        "end" => total - size,
        _ => 0,
    };

    /// <summary>横（horizontal=true）または縦に画像を連結する。
    /// normalize: none / min / max / fixed（横連結では高さ、縦連結では幅を揃える）。
    /// align: start / center / end。</summary>
    public static Image<Rgba32> CombineLinear(
        IReadOnlyList<Image<Rgba32>> imgs, bool horizontal, string normalize,
        int targetPx, string align, int spacing, int padding, Rgba32 bg)
    {
        var work = new List<Image<Rgba32>>(imgs.Count);
        var owned = new List<Image<Rgba32>>();   // ResizeTo が生成したクローン
        try
        {
            if (normalize != "none")
            {
                var dims = imgs.Select(im => horizontal ? im.Height : im.Width);
                int target = normalize switch
                {
                    "min" => dims.Min(),
                    "max" => dims.Max(),
                    _ => targetPx,
                };
                target = Math.Max(1, target);
                foreach (var im in imgs)
                {
                    var r = ResizeTo(im, target, horizontal);
                    if (!ReferenceEquals(r, im)) owned.Add(r);
                    work.Add(r);
                }
            }
            else
            {
                work.AddRange(imgs);
            }

            int n = work.Count;
            int contentW, contentH;
            if (horizontal)
            {
                contentH = work.Max(im => im.Height);
                contentW = work.Sum(im => im.Width) + spacing * (n - 1);
            }
            else
            {
                contentW = work.Max(im => im.Width);
                contentH = work.Sum(im => im.Height) + spacing * (n - 1);
            }

            var canvas = new Image<Rgba32>(
                contentW + 2 * padding, contentH + 2 * padding, bg);
            int cur = padding;
            foreach (var im in work)
            {
                if (horizontal)
                {
                    int y = padding + AlignOffset(contentH, im.Height, align);
                    canvas.Mutate(x => x.DrawImage(im, new Point(cur, y), 1f));
                    cur += im.Width + spacing;
                }
                else
                {
                    int x0 = padding + AlignOffset(contentW, im.Width, align);
                    canvas.Mutate(x => x.DrawImage(im, new Point(x0, cur), 1f));
                    cur += im.Height + spacing;
                }
            }
            return canvas;
        }
        finally
        {
            foreach (var o in owned) o.Dispose();
        }
    }

    /// <summary>画像をグリッド状（columns 列）に連結する。各画像はセルに収めて中央配置。
    /// normalize: none（各画像の最大寸法をセルに）/ fixed（cellPx 角のセル）。</summary>
    public static Image<Rgba32> CombineGrid(
        IReadOnlyList<Image<Rgba32>> imgs, int columns, string normalize,
        int cellPx, int spacing, int padding, Rgba32 bg)
    {
        int cw, ch;
        if (normalize == "fixed")
        {
            cw = ch = Math.Max(1, cellPx);
        }
        else
        {
            cw = imgs.Max(im => im.Width);
            ch = imgs.Max(im => im.Height);
        }

        int rows = (imgs.Count + columns - 1) / columns;
        int totalW = columns * cw + spacing * (columns - 1) + 2 * padding;
        int totalH = rows * ch + spacing * (rows - 1) + 2 * padding;
        var canvas = new Image<Rgba32>(totalW, totalH, bg);

        for (int i = 0; i < imgs.Count; i++)
        {
            int r = i / columns, c = i % columns;
            var fit = Contain(imgs[i], cw, ch);
            try
            {
                int cx = padding + c * (cw + spacing);
                int cy = padding + r * (ch + spacing);
                int ox = cx + (cw - fit.Width) / 2;
                int oy = cy + (ch - fit.Height) / 2;
                canvas.Mutate(x => x.DrawImage(fit, new Point(ox, oy), 1f));
            }
            finally
            {
                if (!ReferenceEquals(fit, imgs[i])) fit.Dispose();
            }
        }
        return canvas;
    }

    /// <summary>拡張子に応じて保存する。JPEG は白背景に合成して不透明化する。</summary>
    public static void SaveByExtension(Image image, string dst)
    {
        string ext = Path.GetExtension(dst).ToLowerInvariant();
        if (ext is ".jpg" or ".jpeg")
        {
            using var flat = new Image<Rgb24>(image.Width, image.Height,
                                              new Rgb24(255, 255, 255));
            flat.Mutate(x => x.DrawImage(image, 1f));
            flat.Save(dst, new JpegEncoder { Quality = JpegQuality });
        }
        else if (ext == ".webp")
        {
            image.Save(dst, new WebpEncoder { Quality = WebpQuality });
        }
        else if (ext == ".png")
        {
            image.Save(dst, new PngEncoder());
        }
        else
        {
            image.Save(dst);   // .bmp / .gif などは拡張子からエンコーダを自動選択
        }
    }
}
