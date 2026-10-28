using System.Runtime.InteropServices;
using UwPower.Core;

namespace UwPower.Tray;

/// <summary>A small popup near the bottom of the screen confirming a shortcut press. Never takes focus.</summary>
internal sealed partial class ModeOsd : Form
{
    private readonly Font _font = new("Segoe UI", 15, FontStyle.Bold);
    private readonly System.Windows.Forms.Timer _hideTimer = new() { Interval = 1400 };
    private PowerMode? _mode;
    private string _message = "";

    public ModeOsd()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(32, 32, 32);
        Opacity = 0.94;
        DoubleBuffered = true;
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            Hide();
        };
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int WsExNoActivate = 0x08000000, WsExToolWindow = 0x80, WsExTopmost = 0x8;
            var cp = base.CreateParams;
            cp.ExStyle |= WsExNoActivate | WsExToolWindow | WsExTopmost;
            return cp;
        }
    }

    public void ShowMessage(PowerMode? mode, string message)
    {
        _mode = mode;
        _message = message;

        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        var (padding, tile, gap) = Metrics();
        var text = TextRenderer.MeasureText(message, _font);
        var size = new Size(padding * 2 + tile + gap + text.Width, padding * 2 + Math.Max(tile, text.Height));
        Bounds = new Rectangle(
            area.Left + (area.Width - size.Width) / 2,
            area.Bottom - size.Height - area.Height / 10,
            size.Width, size.Height);

        Invalidate();
        if (!Visible)
            Show();
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var (padding, tile, gap) = Metrics();
        ModeIcons.DrawTile(e.Graphics, new Rectangle(padding, (Height - tile) / 2, tile, tile), _mode);
        TextRenderer.DrawText(e.Graphics, _message, _font,
            new Rectangle(padding + tile + gap, 0, Width - padding - tile - gap, Height),
            Color.White, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Rounded corners on Windows 11; harmless elsewhere.
        var round = 2;
        _ = DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hideTimer.Dispose();
            _font.Dispose();
        }
        base.Dispose(disposing);
    }

    private (int Padding, int Tile, int Gap) Metrics()
    {
        var scale = DeviceDpi / 96f;
        return ((int)(16 * scale), (int)(32 * scale), (int)(12 * scale));
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
