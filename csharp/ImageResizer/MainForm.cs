// JPG/PNG 一括リサイズツール - メイン画面
namespace ImageResizer;

public class MainForm : Form
{
    private const string AppTitle = "画像リサイズ変換（JPG / PNG）";

    private readonly TextBox _txtInDir = new() { Dock = DockStyle.Fill };
    private readonly TextBox _txtOutDir = new() { Dock = DockStyle.Fill };
    private readonly List<(RadioButton Rb, int Size)> _sizeRadios = new();
    private readonly List<(RadioButton Rb, string Name)> _algoRadios = new();
    private readonly CheckBox _chkNoUpscale = new()
    {
        Text = "長辺が指定サイズより小さい画像は拡大しない",
        Checked = true,
        AutoSize = true,
    };
    private readonly CheckBox _chkStripMeta = new()
    {
        Text = "EXIF・コメントなどのメタデータを削除する（ICCプロファイルは保持）",
        Checked = true,
        AutoSize = true,
    };
    private readonly CheckBox _chkLowercase = new()
    {
        Text = "ファイル名と拡張子を小文字化する",
        AutoSize = true,
    };
    private readonly TextBox _txtReplaceSearch = new() { Dock = DockStyle.Fill };
    private readonly TextBox _txtReplaceWith = new() { Dock = DockStyle.Fill };
    private readonly TextBox _txtSuffix = new() { Dock = DockStyle.Fill };
    private readonly RadioButton _rbSkip = new()
    {
        Text = "処理をスキップ", Checked = true, AutoSize = true,
    };
    private readonly RadioButton _rbOverwrite = new()
    {
        Text = "上書き", AutoSize = true,
    };
    private readonly Button _btnRun = new() { Text = "変換開始", AutoSize = true };
    private readonly Button _btnCancel = new()
    {
        Text = "中断", AutoSize = true, Enabled = false,
    };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Fill };
    private readonly Label _lblStatus = new()
    {
        Text = "待機中", AutoSize = true, Anchor = AnchorStyles.Right,
    };
    private readonly TextBox _txtLog = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        Dock = DockStyle.Fill,
        BackColor = SystemColors.Window,
    };

    private CancellationTokenSource? _cts;

    public MainForm()
    {
        Text = AppTitle;
        Font = new Font("Yu Gothic UI", 9F);
        ClientSize = new Size(760, 680);
        MinimumSize = new Size(640, 560);
        StartPosition = FormStartPosition.CenterScreen;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Padding = new Padding(10),
        };
        Controls.Add(root);

        root.Controls.Add(BuildFolderGroup());
        root.Controls.Add(BuildConvertGroup());
        root.Controls.Add(BuildNameGroup());
        root.Controls.Add(BuildDupGroup());
        root.Controls.Add(BuildRunRow());
        root.Controls.Add(_txtLog);
        // ログ欄だけ余った高さをすべて使う
        for (int i = 0; i < 5; i++)
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _btnRun.Click += BtnRun_Click;
        _btnCancel.Click += (_, _) =>
        {
            _cts?.Cancel();
            _btnCancel.Enabled = false;
        };
    }

    // ---- UI 構築 ----

    private GroupBox BuildFolderGroup()
    {
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 3, AutoSize = true,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var btnIn = new Button { Text = "参照...", AutoSize = true };
        var btnOut = new Button { Text = "参照...", AutoSize = true };
        btnIn.Click += (_, _) => BrowseInFolder();
        btnOut.Click += (_, _) => BrowseFolder(_txtOutDir, "出力フォルダを選択");

        grid.Controls.Add(MakeLabel("入力フォルダ:"), 0, 0);
        grid.Controls.Add(_txtInDir, 1, 0);
        grid.Controls.Add(btnIn, 2, 0);
        grid.Controls.Add(MakeLabel("出力フォルダ:"), 0, 1);
        grid.Controls.Add(_txtOutDir, 1, 1);
        grid.Controls.Add(btnOut, 2, 1);

        return WrapGroup("フォルダ", grid);
    }

    private GroupBox BuildConvertGroup()
    {
        var stack = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, AutoSize = true,
        };

        var sizeRow = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Dock = DockStyle.Fill };
        sizeRow.Controls.Add(MakeLabel("サイズ:"));
        foreach (int s in Converter.SizePresets)
        {
            var rb = new RadioButton
            {
                Text = $"長辺 {s}px", AutoSize = true,
                Checked = s == Converter.SizePresets[0],
                Margin = new Padding(6, 3, 6, 3),
            };
            _sizeRadios.Add((rb, s));
            sizeRow.Controls.Add(rb);
        }

        var algoRow = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Dock = DockStyle.Fill };
        algoRow.Controls.Add(MakeLabel("アルゴリズム:"));
        foreach (string name in Converter.Algorithms)
        {
            var rb = new RadioButton
            {
                Text = name, AutoSize = true,
                Checked = name == "Lanczos",
                Margin = new Padding(6, 3, 6, 3),
            };
            _algoRadios.Add((rb, name));
            algoRow.Controls.Add(rb);
        }

        stack.Controls.Add(sizeRow);
        stack.Controls.Add(algoRow);
        stack.Controls.Add(_chkNoUpscale);
        stack.Controls.Add(_chkStripMeta);

        return WrapGroup("変換設定", stack);
    }

    private GroupBox BuildNameGroup()
    {
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 4, AutoSize = true,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        grid.Controls.Add(_chkLowercase, 0, 0);
        grid.SetColumnSpan(_chkLowercase, 4);

        grid.Controls.Add(MakeLabel("置換元:"), 0, 1);
        grid.Controls.Add(_txtReplaceSearch, 1, 1);
        grid.Controls.Add(MakeLabel("置換先:"), 2, 1);
        grid.Controls.Add(_txtReplaceWith, 3, 1);

        grid.Controls.Add(MakeLabel("末尾に付与:"), 0, 2);
        grid.Controls.Add(_txtSuffix, 1, 2);
        var hint = MakeLabel("（例: _s → photo_s.jpg）");
        grid.Controls.Add(hint, 2, 2);
        grid.SetColumnSpan(hint, 2);

        return WrapGroup("ファイル名オプション", grid);
    }

    private GroupBox BuildDupGroup()
    {
        var row = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        _rbSkip.Margin = new Padding(6, 3, 6, 3);
        _rbOverwrite.Margin = new Padding(6, 3, 6, 3);
        row.Controls.Add(_rbSkip);
        row.Controls.Add(_rbOverwrite);
        return WrapGroup("出力先に同名ファイルがある場合", row);
    }

    private TableLayoutPanel BuildRunRow()
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 4, AutoSize = true,
            Margin = new Padding(3, 8, 3, 8),
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.Controls.Add(_btnRun, 0, 0);
        row.Controls.Add(_btnCancel, 1, 0);
        row.Controls.Add(_progress, 2, 0);
        row.Controls.Add(_lblStatus, 3, 0);
        return row;
    }

    private static Label MakeLabel(string text) => new()
    {
        Text = text, AutoSize = true, Anchor = AnchorStyles.Left,
        Margin = new Padding(3, 6, 3, 3),
    };

    private static GroupBox WrapGroup(string title, Control child)
    {
        var g = new GroupBox
        {
            Text = title, Dock = DockStyle.Fill, AutoSize = true,
            Padding = new Padding(8), Margin = new Padding(3, 3, 3, 6),
        };
        g.Controls.Add(child);
        return g;
    }

    // ---- イベント ----

    private void BrowseInFolder()
    {
        using var dlg = new FolderBrowserDialog { Description = "入力フォルダを選択" };
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _txtInDir.Text = dlg.SelectedPath;
            if (string.IsNullOrWhiteSpace(_txtOutDir.Text))
                _txtOutDir.Text = Path.Combine(dlg.SelectedPath, "resized");
        }
    }

    private void BrowseFolder(TextBox target, string title)
    {
        using var dlg = new FolderBrowserDialog { Description = title };
        if (dlg.ShowDialog(this) == DialogResult.OK)
            target.Text = dlg.SelectedPath;
    }

    private void Log(string text)
    {
        _txtLog.AppendText(text + Environment.NewLine);
    }

    private async void BtnRun_Click(object? sender, EventArgs e)
    {
        string inDir = _txtInDir.Text.Trim();
        string outDir = _txtOutDir.Text.Trim();

        if (inDir.Length == 0 || !Directory.Exists(inDir))
        {
            MessageBox.Show(this, "入力フォルダを正しく指定してください。",
                AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        if (outDir.Length == 0)
        {
            MessageBox.Show(this, "出力フォルダを指定してください。",
                AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        string algoName = _algoRadios.First(a => a.Rb.Checked).Name;
        var opts = new ConvertOptions
        {
            LongEdge = _sizeRadios.First(s => s.Rb.Checked).Size,
            Algorithm = algoName,
            Lowercase = _chkLowercase.Checked,
            ReplaceSearch = _txtReplaceSearch.Text,
            ReplaceWith = _txtReplaceWith.Text,
            Suffix = _txtSuffix.Text,
            Overwrite = _rbOverwrite.Checked,
            NoUpscale = _chkNoUpscale.Checked,
            StripMetadata = _chkStripMeta.Checked,
        };

        if (opts.Overwrite &&
            string.Equals(Path.GetFullPath(inDir).TrimEnd('\\'),
                          Path.GetFullPath(outDir).TrimEnd('\\'),
                          StringComparison.OrdinalIgnoreCase))
        {
            var ans = MessageBox.Show(this,
                "入力フォルダと出力フォルダが同じで「上書き」が選択されています。\n" +
                "元の画像ファイルが変換結果で置き換えられますが、続行しますか？",
                AppTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (ans != DialogResult.Yes)
                return;
        }

        _cts = new CancellationTokenSource();
        _btnRun.Enabled = false;
        _btnCancel.Enabled = true;
        _lblStatus.Text = "処理中...";
        Log($"=== 変換開始: 長辺{opts.LongEdge}px / {algoName} ===");

        try
        {
            var token = _cts.Token;
            var stats = await Task.Run(() => Converter.ProcessFolder(
                inDir, outDir, opts,
                msg => Invoke(() => Log(msg)),
                (cur, total) => Invoke(() =>
                {
                    _progress.Maximum = Math.Max(total, 1);
                    _progress.Value = Math.Min(cur, _progress.Maximum);
                    _lblStatus.Text = $"{cur} / {total}";
                }),
                token));
            Log($"=== 完了: 変換 {stats.Converted} / スキップ {stats.Skipped} / " +
                $"エラー {stats.Error} ===");
            _lblStatus.Text = "完了";
        }
        catch (Exception ex)
        {
            Log($"エラー: {ex.Message}");
            _lblStatus.Text = "エラー";
        }
        finally
        {
            _btnRun.Enabled = true;
            _btnCancel.Enabled = false;
            _cts.Dispose();
            _cts = null;
        }
    }
}
