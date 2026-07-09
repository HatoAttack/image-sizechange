// 切り抜きロジック（GUI 非依存・Python 版 image_editor.py と同仕様）
using Rectangle = SixLabors.ImageSharp.Rectangle;

namespace ImageResizer;

public static class Cropper
{
    /// <summary>アスペクト比プリセット（表示名 -> 幅/高さ。null は自由比）。
    /// 縦向きは「縦横を反転」で対応する（4:3 → 3:4 など）。</summary>
    public static readonly (string Name, double? Ratio)[] AspectPresets =
    {
        ("自由", null),
        ("1:1", 1.0),
        ("4:3", 4.0 / 3),
        ("3:2", 3.0 / 2),
        ("16:9", 16.0 / 9),
    };

    /// <summary>反転指定を適用したアスペクト比を返す（4:3 + 反転 → 3:4）。</summary>
    public static double? Flip(double? aspect, bool flip) =>
        aspect is double a && flip ? 1 / a : aspect;

    /// <summary>画像中央に収まる最大の指定比率矩形（画像座標）を返す。</summary>
    public static (double X, double Y, double W, double H) CenterRect(
        int imgW, int imgH, double aspect)
    {
        double w = imgW, h = imgW / aspect;
        if (h > imgH)
        {
            h = imgH;
            w = imgH * aspect;
        }
        return ((imgW - w) / 2, (imgH - h) / 2, w, h);
    }

    /// <summary>実数矩形を画像範囲内の整数ボックスへ丸める。</summary>
    public static Rectangle ClampBox(double x0, double y0, double x1, double y1,
                                     int imgW, int imgH)
    {
        int ix0 = Math.Max(0, (int)Math.Round(x0));
        int iy0 = Math.Max(0, (int)Math.Round(y0));
        int ix1 = Math.Min(imgW, (int)Math.Round(x1));
        int iy1 = Math.Min(imgH, (int)Math.Round(y1));
        return Rectangle.FromLTRB(ix0, iy0, ix1, iy1);
    }
}
