// 画像ツール（リサイズ・切り抜き・連結） - メイン画面
namespace ImageResizer;

public class MainForm : Form
{
    public const string AppTitle = "画像ツール（リサイズ・切り抜き・連結）";

    public MainForm()
    {
        Text = AppTitle;
        Font = new Font("Yu Gothic UI", 9F);
        ClientSize = new Size(980, 700);
        MinimumSize = new Size(860, 620);
        StartPosition = FormStartPosition.CenterScreen;

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(new ResizeTab());
        tabs.TabPages.Add(new CropTab());
        tabs.TabPages.Add(new CombineTab());
        Controls.Add(tabs);
    }
}
