// ImageSharp 画像を WinForms 表示用の System.Drawing.Bitmap に変換する
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using SixLabors.ImageSharp.PixelFormats;

namespace ImageResizer;

public static class GdiBridge
{
    public static Bitmap ToBitmap(SixLabors.ImageSharp.Image image)
    {
        // GDI+ の 32bppArgb はリトルエンディアンで B,G,R,A 並び = Bgra32 と一致
        using var bgra = image.CloneAs<Bgra32>();
        var bmp = new Bitmap(bgra.Width, bgra.Height, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
                                ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var buf = new byte[bgra.Width * 4];
            bgra.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < accessor.Height; y++)
                {
                    MemoryMarshal.AsBytes(accessor.GetRowSpan(y)).CopyTo(buf);
                    Marshal.Copy(buf, 0, data.Scan0 + y * data.Stride, buf.Length);
                }
            });
        }
        finally
        {
            bmp.UnlockBits(data);
        }
        return bmp;
    }
}
