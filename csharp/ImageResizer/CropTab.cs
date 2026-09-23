// 切り抜きタブ - マウスで範囲選択し、固定/任意アスペクト比でクロップ保存
using System.Drawing.Drawing2D;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using ISImage = SixLabors.ImageSharp.Image;

namespace ImageResizer;

public class CropTab : TabPage
{
    private const int MinSizePx = 8;   // クロップ枠の最小サイズ（画像px）

    private static readonly string ImageFilter =
        "画像|*.jpg;*.jpeg;*.png;*.webp;*.bmp;*.gif|すべて|*.*";

    // ドラッグ＆ドロップで受け付ける拡張子
    private static readonly HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif",
    };

    // 画像状態
    private readonly List<string> _paths = new();
    private int _index = -1;
    private SixLabors.ImageSharp.Image<Rgba32>? _image;  // AutoOrient 済み（クロップ元）
    private Bitmap? _fullBmp;                            // 表示用フルサイズ
    private Bitmap? _dispBmp;                            // パネルサイズに縮小したキャッシュ

    // 画像→キャンバス変換
    private double _scale = 1;
    private double _offX, _offY;

    // クロップ枠（画像座標 x0,y0,x1,y1）とドラッグ状態
    private double[]? _rect;
    private enum DragMode { None, Move, Resize }
    private DragMode _mode = DragMode.None;
    private (double X, double Y) _fixed;     // Resize 時の固定コーナー
    private (double X, double Y) _moveOff;

    // UI
    private readonly DoubleBufferedPanel _canvas = new()
    {
        Dock = DockStyle.Fill, BackColor = Color.FromArgb(43, 43, 43),
        Cursor = Cursors.Cross,
    };
    private readonly Label _lblName = new()
    {
        Text = "（画像未選択）", AutoSize = true, Margin = new Padding(8, 8, 3, 3),
    };
    private readonly List<(RadioButton Rb, double? Ratio)> _aspectRadios = new();
    private readonly RadioButton _rbCustom = new() { Text = "任意", AutoSize = true };
    private readonly NumericUpDown _numW = new()
    {
        Minimum = 1, Maximum = 999, Value = 16, Width = 48,
        TextAlign = HorizontalAlignment.Right,
    };
    private readonly NumericUpDown _numH = new()
    {
        Minimum = 1, Maximum = 999, Value = 10, Width = 48,
        TextAlign = HorizontalAlignment.Right,
    };
    private readonly CheckBox _chkFlip = new()
    {
        Text = "縦横を反転（4:3 → 3:4）", AutoSize = true,
    };
    private readonly Label _lblSize = new() { Text = "", AutoSize = true };
    private readonly TextBox _txtOutDir = new() { Dock = DockStyle.Fill };
    private readonly System.Windows.Forms.Timer _resizeTimer = new() { Interval = 60 };

    private class DoubleBufferedPanel : Panel
    {
        public DoubleBufferedPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer, true);
        }
    }

    public CropTab()
    {
        Text = "切り抜き";
        UseVisualStyleBackColor = true;

        // タブ全体（キャンバス含む）で画像ファイル/フォルダのドロップを受け付ける
        AllowDrop = true;
        DragEnter += OnDragEnter;
        DragDrop += OnDragDrop;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2,
            Padding = new Padding(8),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        // 上段: ファイル操作
        var top = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        var btnOpen = new Button { Text = "画像を開く...", AutoSize = true };
        var btnPrev = new Button { Text = "◀ 前", AutoSize = true };
        var btnNext = new Button { Text = "次 ▶", AutoSize = true };
        btnOpen.Click += (_, _) => OpenImages();
        btnPrev.Click += (_, _) => Step(-1);
        btnNext.Click += (_, _) => Step(1);
        top.Controls.Add(btnOpen);
        top.Controls.Add(btnPrev);
        top.Controls.Add(btnNext);
        top.Controls.Add(_lblName);
        root.Controls.Add(top, 0, 0);
        root.SetColumnSpan(top, 2);

        // キャンバス
        _canvas.Paint += Canvas_Paint;
        _canvas.MouseDown += Canvas_MouseDown;
        _canvas.MouseMove += Canvas_MouseMove;
        _canvas.MouseUp += (_, _) => _mode = DragMode.None;
        _canvas.Resize += (_, _) => { _resizeTimer.Stop(); _resizeTimer.Start(); };
        _resizeTimer.Tick += (_, _) => { _resizeTimer.Stop(); RebuildDisp(); };
        root.Controls.Add(_canvas, 0, 1);

        // 右サイドパネル
        var side = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, AutoSize = true,
            WrapContents = false, Dock = DockStyle.Fill, Width = 190,
            Margin = new Padding(8, 3, 0, 3),
        };
        root.Controls.Add(side, 1, 1);

        var aspectBox = new GroupBox
        {
            Text = "アスペクト比", AutoSize = true, Padding = new Padding(8),
        };
        var aspectStack = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, AutoSize = true,
            WrapContents = false, Dock = DockStyle.Fill,
        };
        foreach (var (name, ratio) in Cropper.AspectPresets)
        {
            var rb = new RadioButton
            {
                Text = name, AutoSize = true, Checked = ratio is null,
            };
            rb.CheckedChanged += (_, _) => { if (rb.Checked) OnAspectChanged(); };
            _aspectRadios.Add((rb, ratio));
            aspectStack.Controls.Add(rb);
        }
        var customRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        _rbCustom.CheckedChanged += (_, _) => { if (_rbCustom.Checked) OnAspectChanged(); };
        _numW.Enter += (_, _) => _rbCustom.Checked = true;
        _numH.Enter += (_, _) => _rbCustom.Checked = true;
        _numW.ValueChanged += (_, _) => { if (_rbCustom.Checked) OnAspectChanged(); };
        _numH.ValueChanged += (_, _) => { if (_rbCustom.Checked) OnAspectChanged(); };
        customRow.Controls.Add(_rbCustom);
        customRow.Controls.Add(_numW);
        customRow.Controls.Add(new Label
        {
            Text = ":", AutoSize = true, Margin = new Padding(0, 6, 0, 3),
        });
        customRow.Controls.Add(_numH);
        aspectStack.Controls.Add(customRow);
        _chkFlip.CheckedChanged += (_, _) => OnAspectChanged();
        aspectStack.Controls.Add(_chkFlip);
        aspectBox.Controls.Add(aspectStack);
        side.Controls.Add(aspectBox);

        _lblSize.Margin = new Padding(3, 8, 3, 3);
        side.Controls.Add(_lblSize);

        var btnSave = new Button
        {
            Text = "この範囲で保存", AutoSize = true, Width = 170,
        };
        btnSave.Click += (_, _) => SaveCurrent();
        side.Controls.Add(btnSave);
        var btnSaveAll = new Button
        {
            Text = "全画像を同比率で\n中央切り抜き保存", AutoSize = true, Width = 170,
        };
        btnSaveAll.Click += (_, _) => SaveAllCenter();
        side.Controls.Add(btnSaveAll);

        var outBox = new GroupBox
        {
            Text = "出力先", AutoSize = true, Padding = new Padding(8),
            Margin = new Padding(3, 8, 3, 3),
        };
        var outStack = new TableLayoutPanel
        {
            ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill, Width = 160,
        };
        _txtOutDir.Width = 150;
        outStack.Controls.Add(_txtOutDir);
        var btnBrowse = new Button { Text = "参照...", AutoSize = true };
        btnBrowse.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog { Description = "出力フォルダを選択" };
            if (dlg.ShowDialog(this) == DialogResult.OK)
                _txtOutDir.Text = dlg.SelectedPath;
        };
        outStack.Controls.Add(btnBrowse);
        outBox.Controls.Add(outStack);
        side.Controls.Add(outBox);
    }

    // ---- ファイル操作 ----

    private void OpenImages()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "画像を選択", Filter = ImageFilter, Multiselect = true,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;
        SetPaths(dlg.FileNames);
    }

    private void SetPaths(IReadOnlyList<string> paths)
    {
        _paths.Clear();
        _paths.AddRange(paths);
        _index = 0;
        if (_txtOutDir.Text.Length == 0)
            _txtOutDir.Text = Path.Combine(
                Path.GetDirectoryName(_paths[0]) ?? "", "cropped");
        LoadCurrent();
    }

    // ドロップされたファイル/フォルダから対象画像を列挙（フォルダは直下のみ）
    private static List<string> CollectImages(IEnumerable<string> dropped)
    {
        var result = new List<string>();
        foreach (var p in dropped)
        {
            if (Directory.Exists(p))
                result.AddRange(Directory.EnumerateFiles(p)
                    .Where(f => ImageExts.Contains(Path.GetExtension(f)))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
            else if (File.Exists(p) && ImageExts.Contains(Path.GetExtension(p)))
                result.Add(p);
        }
        return result;
    }

    private static string[]? DroppedPaths(DragEventArgs e) =>
        e.Data?.GetDataPresent(DataFormats.FileDrop) == true
            ? e.Data.GetData(DataFormats.FileDrop) as string[]
            : null;

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        var dropped = DroppedPaths(e);
        e.Effect = dropped is not null && CollectImages(dropped).Count > 0
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        var dropped = DroppedPaths(e);
        if (dropped is null)
            return;
        var images = CollectImages(dropped);
        if (images.Count == 0)
        {
            MessageBox.Show(this, "対応する画像ファイルがありません。",
                MainForm.AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        SetPaths(images);
    }

    private void Step(int delta)
    {
        if (_paths.Count == 0)
            return;
        _index = ((_index + delta) % _paths.Count + _paths.Count) % _paths.Count;
        LoadCurrent();
    }

    private void LoadCurrent()
    {
        string path = _paths[_index];
        try
        {
            var img = ISImage.Load<Rgba32>(path);
            img.Mutate(x => x.AutoOrient());
            _image?.Dispose();
            _image = img;
            _fullBmp?.Dispose();
            _fullBmp = GdiBridge.ToBitmap(img);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"読み込み失敗: {Path.GetFileName(path)}\n{ex.Message}",
                MainForm.AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        _lblName.Text = $"[{_index + 1}/{_paths.Count}] {Path.GetFileName(path)}" +
                        $"  ({_image.Width}×{_image.Height})";
        ResetRect();
        RebuildDisp();
    }

    // ---- アスペクト比 ----

    private double? CurrentAspect()
    {
        double? aspect;
        if (_rbCustom.Checked)
        {
            double w = (double)_numW.Value, h = (double)_numH.Value;
            aspect = (w > 0 && h > 0) ? w / h : null;
        }
        else
        {
            aspect = _aspectRadios.First(a => a.Rb.Checked).Ratio;
        }
        return Cropper.Flip(aspect, _chkFlip.Checked);
    }

    private void OnAspectChanged()
    {
        if (_image is null)
            return;
        ResetRect();
        _canvas.Invalidate();
    }

    /// <summary>現在のアスペクト比で、画像中央に最大のクロップ枠を作る。</summary>
    private void ResetRect()
    {
        if (_image is null)
            return;
        double? aspect = CurrentAspect();
        if (aspect is null)
        {
            _rect = new[] { 0.0, 0.0, (double)_image.Width, (double)_image.Height };
        }
        else
        {
            var (x, y, w, h) = Cropper.CenterRect(_image.Width, _image.Height, aspect.Value);
            _rect = new[] { x, y, x + w, y + h };
        }
    }

    // ---- 座標変換 ----

    private (double X, double Y) I2C(double ix, double iy) =>
        (_offX + ix * _scale, _offY + iy * _scale);

    private (double X, double Y) C2I(double cx, double cy) =>
        ((cx - _offX) / _scale, (cy - _offY) / _scale);

    private (double X, double Y) ClampI(double ix, double iy) =>
        (Math.Min(Math.Max(ix, 0), _image!.Width),
         Math.Min(Math.Max(iy, 0), _image!.Height));

    private int HandleRadius => Math.Max(7, 7 * DeviceDpi / 96);

    // ---- 描画 ----
    // 重い処理（縮小ビットマップ生成）は RebuildDisp のみで行い、
    // ドラッグ中の Paint はキャッシュ描画＋オーバーレイだけで済ませる。

    private void RebuildDisp()
    {
        if (_fullBmp is null)
            return;
        int cw = Math.Max(1, _canvas.ClientSize.Width);
        int ch = Math.Max(1, _canvas.ClientSize.Height);
        int w = _fullBmp.Width, h = _fullBmp.Height;
        _scale = Math.Min((double)cw / w, (double)ch / h);
        int dw = Math.Max(1, (int)Math.Round(w * _scale));
        int dh = Math.Max(1, (int)Math.Round(h * _scale));
        _offX = (cw - dw) / 2.0;
        _offY = (ch - dh) / 2.0;

        _dispBmp?.Dispose();
        _dispBmp = new Bitmap(dw, dh);
        using (var g = Graphics.FromImage(_dispBmp))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.DrawImage(_fullBmp, 0, 0, dw, dh);
        }
        _canvas.Invalidate();
    }

    private void Canvas_Paint(object? sender, PaintEventArgs e)
    {
        if (_dispBmp is null)
            return;
        var g = e.Graphics;
        g.DrawImageUnscaled(_dispBmp, (int)Math.Round(_offX), (int)Math.Round(_offY));

        if (_rect is null)
            return;
        var (x0, y0) = I2C(_rect[0], _rect[1]);
        var (x1, y1) = I2C(_rect[2], _rect[3]);
        float dx0 = (float)_offX, dy0 = (float)_offY;
        float dx1 = dx0 + _dispBmp.Width, dy1 = dy0 + _dispBmp.Height;
        float fx0 = (float)x0, fy0 = (float)y0, fx1 = (float)x1, fy1 = (float)y1;

        // 選択範囲外を暗くする（上下左右の帯）
        using (var shade = new SolidBrush(Color.FromArgb(110, 0, 0, 0)))
        {
            if (fy0 > dy0) g.FillRectangle(shade, dx0, dy0, dx1 - dx0, fy0 - dy0);
            if (dy1 > fy1) g.FillRectangle(shade, dx0, fy1, dx1 - dx0, dy1 - fy1);
            if (fx0 > dx0) g.FillRectangle(shade, dx0, fy0, fx0 - dx0, fy1 - fy0);
            if (dx1 > fx1) g.FillRectangle(shade, fx1, fy0, dx1 - fx1, fy1 - fy0);
        }

        // 枠と三分割ガイド（破線）
        using (var frame = new Pen(Color.White, 1))
            g.DrawRectangle(frame, fx0, fy0, fx1 - fx0, fy1 - fy0);
        using (var guide = new Pen(Color.White, 1) { DashPattern = new[] { 3f, 3f } })
        {
            for (int k = 1; k <= 2; k++)
            {
                float gx = fx0 + (fx1 - fx0) * k / 3;
                float gy = fy0 + (fy1 - fy0) * k / 3;
                g.DrawLine(guide, gx, fy0, gx, fy1);
                g.DrawLine(guide, fx0, gy, fx1, gy);
            }
        }

        // コーナーハンドル
        using var fill = new SolidBrush(Color.White);
        using var border = new Pen(Color.FromArgb(0, 120, 215), 1);
        int hs = HandleRadius - 3;
        foreach (var (hx, hy) in new[] { (fx0, fy0), (fx1, fy0), (fx1, fy1), (fx0, fy1) })
        {
            g.FillRectangle(fill, hx - hs, hy - hs, hs * 2, hs * 2);
            g.DrawRectangle(border, hx - hs, hy - hs, hs * 2, hs * 2);
        }

        _lblSize.Text = $"選択: {Math.Round(_rect[2] - _rect[0])} × " +
                        $"{Math.Round(_rect[3] - _rect[1])} px";
    }

    // ---- マウス操作 ----

    private void Canvas_MouseDown(object? sender, MouseEventArgs e)
    {
        if (_image is null || e.Button != MouseButtons.Left)
            return;
        if (_rect is null)
        {
            var (nix, niy) = ClampI(C2I(e.X, e.Y).X, C2I(e.X, e.Y).Y);
            _rect = new[] { nix, niy, nix, niy };
            _mode = DragMode.Resize;
            _fixed = (nix, niy);
            return;
        }
        var corners = new (double X, double Y)[]
        {
            (_rect[0], _rect[1]), (_rect[2], _rect[1]),
            (_rect[2], _rect[3]), (_rect[0], _rect[3]),
        };
        int hr = HandleRadius;
        for (int i = 0; i < 4; i++)
        {
            var (cx, cy) = I2C(corners[i].X, corners[i].Y);
            if (Math.Abs(e.X - cx) <= hr && Math.Abs(e.Y - cy) <= hr)
            {
                _mode = DragMode.Resize;
                _fixed = corners[(i + 2) % 4];    // 対角を固定
                return;
            }
        }
        // 枠の内側なら移動、外側なら新規作成
        var (ix, iy) = C2I(e.X, e.Y);
        if (_rect[0] <= ix && ix <= _rect[2] && _rect[1] <= iy && iy <= _rect[3])
        {
            _mode = DragMode.Move;
            _moveOff = (ix - _rect[0], iy - _rect[1]);
        }
        else
        {
            (ix, iy) = ClampI(ix, iy);
            _rect = new[] { ix, iy, ix, iy };
            _mode = DragMode.Resize;
            _fixed = (ix, iy);
        }
    }

    private void Canvas_MouseMove(object? sender, MouseEventArgs e)
    {
        if (_mode == DragMode.None || _image is null || _rect is null)
            return;
        if (_mode == DragMode.Move)
            DoMove(e);
        else
            DoResize(e);
        _canvas.Invalidate();
    }

    private void DoMove(MouseEventArgs e)
    {
        int w = _image!.Width, h = _image.Height;
        var (ix, iy) = C2I(e.X, e.Y);
        double rw = _rect![2] - _rect[0];
        double rh = _rect[3] - _rect[1];
        double x0 = Math.Min(Math.Max(ix - _moveOff.X, 0), w - rw);
        double y0 = Math.Min(Math.Max(iy - _moveOff.Y, 0), h - rh);
        _rect = new[] { x0, y0, x0 + rw, y0 + rh };
    }

    private void DoResize(MouseEventArgs e)
    {
        int imgW = _image!.Width, imgH = _image.Height;
        var (fx, fy) = _fixed;
        var (mx, my) = ClampI(C2I(e.X, e.Y).X, C2I(e.X, e.Y).Y);
        int dirX = mx >= fx ? 1 : -1;
        int dirY = my >= fy ? 1 : -1;
        double w = Math.Abs(mx - fx);
        double h = Math.Abs(my - fy);
        double? aspect = CurrentAspect();
        if (aspect is double a)
        {
            if (w / a >= h)
                h = w / a;
            else
                w = h * a;
            double availX = dirX > 0 ? imgW - fx : fx;
            double availY = dirY > 0 ? imgH - fy : fy;
            double s = Math.Min(Math.Min(
                w > 0 ? availX / w : 1, h > 0 ? availY / h : 1), 1.0);
            w *= s;
            h *= s;
        }
        double nx = fx + dirX * w, ny = fy + dirY * h;
        double x0 = Math.Min(fx, nx), x1 = Math.Max(fx, nx);
        double y0 = Math.Min(fy, ny), y1 = Math.Max(fy, ny);
        if (x1 - x0 >= MinSizePx && y1 - y0 >= MinSizePx)
            _rect = new[] { x0, y0, x1, y1 };
    }

    // ---- 保存 ----

    private string? OutPath(string src)
    {
        string dir = _txtOutDir.Text.Trim();
        if (dir.Length == 0)
        {
            MessageBox.Show(this, "出力先フォルダを指定してください。",
                MainForm.AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return null;
        }
        Directory.CreateDirectory(dir);
        return Path.Combine(dir,
            Path.GetFileNameWithoutExtension(src) + "_crop" + Path.GetExtension(src));
    }

    private void SaveCurrent()
    {
        if (_image is null || _rect is null)
            return;
        string src = _paths[_index];
        string? dst = OutPath(src);
        if (dst is null)
            return;
        var box = Cropper.ClampBox(_rect[0], _rect[1], _rect[2], _rect[3],
                                   _image.Width, _image.Height);
        if (box.Width < 1 || box.Height < 1)
        {
            MessageBox.Show(this, "選択範囲が小さすぎます。",
                MainForm.AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        try
        {
            using var cropped = _image.Clone(x => x.Crop(box));
            Combiner.SaveByExtension(cropped, dst);
            MessageBox.Show(this, $"保存しました:\n{dst}",
                MainForm.AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"保存失敗:\n{ex.Message}",
                MainForm.AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SaveAllCenter()
    {
        double? aspect = CurrentAspect();
        if (aspect is null)
        {
            MessageBox.Show(this, "「自由」以外のアスペクト比を選択してください。",
                MainForm.AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (_paths.Count == 0)
            return;
        int ok = 0, err = 0;
        foreach (string src in _paths)
        {
            try
            {
                string? dst = OutPath(src);
                if (dst is null)
                    return;
                using var img = ISImage.Load<Rgba32>(src);
                img.Mutate(x => x.AutoOrient());
                var (cx, cy, cw, chh) = Cropper.CenterRect(
                    img.Width, img.Height, aspect.Value);
                var box = Cropper.ClampBox(cx, cy, cx + cw, cy + chh,
                                           img.Width, img.Height);
                img.Mutate(x => x.Crop(box));
                Combiner.SaveByExtension(img, dst);
                ok++;
            }
            catch
            {
                err++;
            }
        }
        MessageBox.Show(this, $"完了: 保存 {ok} / 失敗 {err}",
            MainForm.AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}
