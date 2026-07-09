// 連結タブ - 複数画像を横・縦・グリッドに1枚へ連結する
using System.Drawing.Drawing2D;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using ISImage = SixLabors.ImageSharp.Image;

namespace ImageResizer;

public class CombineTab : TabPage
{
    private static readonly string ImageFilter =
        "画像|*.jpg;*.jpeg;*.png;*.webp;*.bmp;*.gif|すべて|*.*";

    private readonly List<string> _paths = new();
    private Bitmap? _previewBmp;

    // UI
    private readonly ListBox _list = new()
    {
        Dock = DockStyle.Fill, SelectionMode = SelectionMode.MultiExtended,
        IntegralHeight = false,
    };
    private readonly RadioButton _rbHorizontal = new()
    {
        Text = "横", AutoSize = true, Checked = true,
    };
    private readonly RadioButton _rbVertical = new() { Text = "縦", AutoSize = true };
    private readonly RadioButton _rbGrid = new() { Text = "グリッド", AutoSize = true };
    private readonly NumericUpDown _numCols = new()
    {
        Minimum = 1, Maximum = 20, Value = 2, Width = 56, Enabled = false,
        TextAlign = HorizontalAlignment.Right,
    };
    private readonly ComboBox _cmbNorm = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList, Width = 150,
    };
    private readonly NumericUpDown _numTarget = new()
    {
        Minimum = 1, Maximum = 20000, Value = 600, Width = 70, Enabled = false,
        TextAlign = HorizontalAlignment.Right,
    };
    private readonly ComboBox _cmbAlign = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList, Width = 150,
    };
    private readonly NumericUpDown _numSpacing = new()
    {
        Minimum = 0, Maximum = 2000, Value = 0, Width = 70,
        TextAlign = HorizontalAlignment.Right,
    };
    private readonly NumericUpDown _numPadding = new()
    {
        Minimum = 0, Maximum = 2000, Value = 0, Width = 70,
        TextAlign = HorizontalAlignment.Right,
    };
    private readonly TextBox _txtBg = new() { Text = "#ffffff", Width = 70 };
    private readonly Panel _swatch = new()
    {
        Width = 22, Height = 22, BackColor = Color.White,
        BorderStyle = BorderStyle.FixedSingle, Cursor = Cursors.Hand,
    };
    private readonly CheckBox _chkTransparent = new()
    {
        Text = "背景を透過（PNG）", AutoSize = true,
    };
    private readonly RadioButton _rbPng = new()
    {
        Text = "PNG", AutoSize = true, Checked = true,
    };
    private readonly RadioButton _rbJpeg = new() { Text = "JPEG", AutoSize = true };
    private readonly RadioButton _rbWebp = new() { Text = "WEBP", AutoSize = true };
    private readonly DoubleBufferedPanel _preview = new()
    {
        Dock = DockStyle.Fill, BackColor = Color.FromArgb(43, 43, 43),
    };
    private readonly Label _lblInfo = new()
    {
        Text = "画像を追加してプレビューしてください。", AutoSize = true,
        Margin = new Padding(3, 6, 3, 3),
    };

    // 表示名 -> 内部コード（レイアウトにより張り替える）
    private Dictionary<string, string> _normMap = new();

    private class DoubleBufferedPanel : Panel
    {
        public DoubleBufferedPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer, true);
        }
    }

    public CombineTab()
    {
        Text = "連結";
        UseVisualStyleBackColor = true;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
            Padding = new Padding(8),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(root);

        // 左: リスト + 設定
        var left = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, AutoSize = true, Width = 300,
        };
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(left, 0, 0);

        left.Controls.Add(new Label
        {
            Text = "画像リスト（上から順に連結）", AutoSize = true,
        }, 0, 0);
        _list.Width = 280;
        _list.Height = 180;
        left.Controls.Add(_list, 0, 1);

        var btnRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        var btnAdd = new Button { Text = "追加", AutoSize = true };
        var btnRemove = new Button { Text = "削除", AutoSize = true };
        var btnUp = new Button { Text = "▲", Width = 34 };
        var btnDown = new Button { Text = "▼", Width = 34 };
        var btnClear = new Button { Text = "クリア", AutoSize = true };
        btnAdd.Click += (_, _) => AddImages();
        btnRemove.Click += (_, _) => RemoveSelected();
        btnUp.Click += (_, _) => MoveSelected(-1);
        btnDown.Click += (_, _) => MoveSelected(1);
        btnClear.Click += (_, _) => { _paths.Clear(); _list.Items.Clear(); };
        btnRow.Controls.Add(btnAdd);
        btnRow.Controls.Add(btnRemove);
        btnRow.Controls.Add(btnUp);
        btnRow.Controls.Add(btnDown);
        btnRow.Controls.Add(btnClear);
        left.Controls.Add(btnRow, 0, 2);

        left.Controls.Add(BuildOptionsGroup(), 0, 3);

        var actRow = new FlowLayoutPanel
        {
            AutoSize = true, WrapContents = false, Margin = new Padding(3, 8, 3, 3),
        };
        var btnPreview = new Button { Text = "プレビュー", AutoSize = true };
        var btnSave = new Button { Text = "連結して保存...", AutoSize = true };
        btnPreview.Click += (_, _) => Preview();
        btnSave.Click += (_, _) => SaveResult();
        actRow.Controls.Add(btnPreview);
        actRow.Controls.Add(btnSave);
        left.Controls.Add(actRow, 0, 4);

        // 右: プレビュー
        var right = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2,
            Margin = new Padding(8, 3, 3, 3),
        };
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _preview.Paint += Preview_Paint;
        _preview.Resize += (_, _) => _preview.Invalidate();
        right.Controls.Add(_preview, 0, 0);
        right.Controls.Add(_lblInfo, 0, 1);
        root.Controls.Add(right, 1, 0);

        _rbHorizontal.CheckedChanged += (_, _) => { if (_rbHorizontal.Checked) OnLayoutChanged(); };
        _rbVertical.CheckedChanged += (_, _) => { if (_rbVertical.Checked) OnLayoutChanged(); };
        _rbGrid.CheckedChanged += (_, _) => { if (_rbGrid.Checked) OnLayoutChanged(); };
        _cmbNorm.SelectedIndexChanged += (_, _) => OnNormChanged();
        _swatch.Click += (_, _) => PickColor();
        _txtBg.TextChanged += (_, _) => SyncSwatch();

        OnLayoutChanged();
    }

    private GroupBox BuildOptionsGroup()
    {
        var box = new GroupBox
        {
            Text = "設定", AutoSize = true, Padding = new Padding(8),
            Dock = DockStyle.Fill,
        };
        var grid = new TableLayoutPanel
        {
            ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        Label L(string t) => new()
        {
            Text = t, AutoSize = true, Anchor = AnchorStyles.Right,
            Margin = new Padding(3, 7, 3, 3),
        };

        var layoutRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        layoutRow.Controls.Add(_rbHorizontal);
        layoutRow.Controls.Add(_rbVertical);
        layoutRow.Controls.Add(_rbGrid);
        grid.Controls.Add(L("並べ方:"), 0, 0);
        grid.Controls.Add(layoutRow, 1, 0);

        grid.Controls.Add(L("列数:"), 0, 1);
        grid.Controls.Add(_numCols, 1, 1);

        grid.Controls.Add(L("サイズ揃え:"), 0, 2);
        grid.Controls.Add(_cmbNorm, 1, 2);

        grid.Controls.Add(L("指定px:"), 0, 3);
        grid.Controls.Add(_numTarget, 1, 3);

        grid.Controls.Add(L("整列:"), 0, 4);
        grid.Controls.Add(_cmbAlign, 1, 4);

        grid.Controls.Add(L("間隔px:"), 0, 5);
        grid.Controls.Add(_numSpacing, 1, 5);

        grid.Controls.Add(L("外余白px:"), 0, 6);
        grid.Controls.Add(_numPadding, 1, 6);

        var colorRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        colorRow.Controls.Add(_txtBg);
        colorRow.Controls.Add(_swatch);
        grid.Controls.Add(L("背景色:"), 0, 7);
        grid.Controls.Add(colorRow, 1, 7);

        grid.Controls.Add(_chkTransparent, 1, 8);

        var fmtRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        fmtRow.Controls.Add(_rbPng);
        fmtRow.Controls.Add(_rbJpeg);
        fmtRow.Controls.Add(_rbWebp);
        grid.Controls.Add(L("出力形式:"), 0, 9);
        grid.Controls.Add(fmtRow, 1, 9);

        box.Controls.Add(grid);
        return box;
    }

    // ---- リスト操作 ----

    private void AddImages()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "連結する画像を選択", Filter = ImageFilter, Multiselect = true,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;
        foreach (string f in dlg.FileNames)
        {
            _paths.Add(f);
            _list.Items.Add(Path.GetFileName(f));
        }
    }

    private void RemoveSelected()
    {
        var indices = _list.SelectedIndices.Cast<int>().OrderByDescending(i => i).ToList();
        foreach (int i in indices)
        {
            _list.Items.RemoveAt(i);
            _paths.RemoveAt(i);
        }
    }

    private void MoveSelected(int delta)
    {
        if (_list.SelectedIndices.Count != 1)
            return;
        int i = _list.SelectedIndex;
        int j = i + delta;
        if (j < 0 || j >= _paths.Count)
            return;
        (_paths[i], _paths[j]) = (_paths[j], _paths[i]);
        object item = _list.Items[i];
        _list.Items.RemoveAt(i);
        _list.Items.Insert(j, item);
        _list.SelectedIndex = j;
    }

    // ---- 設定連動 ----

    private void OnLayoutChanged()
    {
        bool isGrid = _rbGrid.Checked;
        _numCols.Enabled = isGrid;

        _cmbNorm.Items.Clear();
        if (isGrid)
        {
            _cmbNorm.Items.AddRange(new object[] { "元の最大に合わせる", "セルを指定px角に" });
            _normMap = new Dictionary<string, string>
            {
                ["元の最大に合わせる"] = "none",
                ["セルを指定px角に"] = "fixed",
            };
            _cmbAlign.Items.Clear();
            _cmbAlign.Enabled = false;
        }
        else
        {
            _cmbNorm.Items.AddRange(new object[]
            {
                "そのまま", "小さい方に揃える", "大きい方に揃える", "指定pxに揃える",
            });
            _normMap = new Dictionary<string, string>
            {
                ["そのまま"] = "none",
                ["小さい方に揃える"] = "min",
                ["大きい方に揃える"] = "max",
                ["指定pxに揃える"] = "fixed",
            };
            _cmbAlign.Enabled = true;
            _cmbAlign.Items.Clear();
            _cmbAlign.Items.AddRange(_rbHorizontal.Checked
                ? new object[] { "上", "中央", "下" }
                : new object[] { "左", "中央", "右" });
            _cmbAlign.SelectedIndex = 0;
        }
        _cmbNorm.SelectedIndex = 0;
        OnNormChanged();
    }

    private void OnNormChanged()
    {
        _numTarget.Enabled = NormCode() == "fixed";
    }

    private string NormCode() =>
        _cmbNorm.SelectedItem is string s && _normMap.TryGetValue(s, out string? c)
            ? c : "none";

    private string AlignCode() => _cmbAlign.SelectedIndex switch
    {
        1 => "center",
        2 => "end",
        _ => "start",
    };

    private void PickColor()
    {
        using var dlg = new ColorDialog { Color = _swatch.BackColor, FullOpen = true };
        if (dlg.ShowDialog(this) == DialogResult.OK)
            _txtBg.Text = $"#{dlg.Color.R:x2}{dlg.Color.G:x2}{dlg.Color.B:x2}";
    }

    private void SyncSwatch()
    {
        try
        {
            var c = Combiner.ParseColor(_txtBg.Text, false);
            _swatch.BackColor = Color.FromArgb(c.R, c.G, c.B);
        }
        catch (ArgumentException)
        {
            // 入力途中は無視
        }
    }

    // ---- 連結 ----

    private SixLabors.ImageSharp.Image<Rgba32> Render()
    {
        if (_paths.Count < 2)
            throw new InvalidOperationException("画像を2枚以上追加してください。");
        var imgs = new List<SixLabors.ImageSharp.Image<Rgba32>>();
        try
        {
            foreach (string p in _paths)
            {
                var im = ISImage.Load<Rgba32>(p);
                im.Mutate(x => x.AutoOrient());
                imgs.Add(im);
            }
            var bg = Combiner.ParseColor(_txtBg.Text, _chkTransparent.Checked);
            if (_rbGrid.Checked)
                return Combiner.CombineGrid(imgs, (int)_numCols.Value, NormCode(),
                    (int)_numTarget.Value, (int)_numSpacing.Value,
                    (int)_numPadding.Value, bg);
            return Combiner.CombineLinear(imgs, _rbHorizontal.Checked, NormCode(),
                (int)_numTarget.Value, AlignCode(), (int)_numSpacing.Value,
                (int)_numPadding.Value, bg);
        }
        finally
        {
            foreach (var im in imgs)
                im.Dispose();
        }
    }

    private void Preview()
    {
        try
        {
            using var result = Render();
            _previewBmp?.Dispose();
            _previewBmp = GdiBridge.ToBitmap(result);
            _lblInfo.Text = $"連結結果: {result.Width} × {result.Height} px";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, MainForm.AppTitle,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        _preview.Invalidate();
    }

    private void Preview_Paint(object? sender, PaintEventArgs e)
    {
        if (_previewBmp is null)
            return;
        var g = e.Graphics;
        int cw = _preview.ClientSize.Width, ch = _preview.ClientSize.Height;
        double s = Math.Min(Math.Min(
            (double)cw / _previewBmp.Width, (double)ch / _previewBmp.Height), 1.0);
        int w = Math.Max(1, (int)(_previewBmp.Width * s));
        int h = Math.Max(1, (int)(_previewBmp.Height * s));
        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
        g.DrawImage(_previewBmp, (cw - w) / 2, (ch - h) / 2, w, h);
    }

    private void SaveResult()
    {
        string ext = _rbJpeg.Checked ? ".jpg" : _rbWebp.Checked ? ".webp" : ".png";
        using var dlg = new SaveFileDialog
        {
            Title = "保存先",
            FileName = "combined" + ext,
            Filter = $"{ext.TrimStart('.').ToUpperInvariant()}|*{ext}",
            DefaultExt = ext.TrimStart('.'),
        };
        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;
        try
        {
            using var result = Render();
            Combiner.SaveByExtension(result, dlg.FileName);
            _lblInfo.Text = $"保存しました: {dlg.FileName}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"保存失敗:\n{ex.Message}", MainForm.AppTitle,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
