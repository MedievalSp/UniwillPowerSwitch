using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using UwPower.Core;

namespace UwPower.Tray;

/// <summary>Draws the notification-area icons: a colored tile with a power level 1-3 (or "?").</summary>
internal sealed partial class ModeIcons : IDisposable
{
    private readonly Dictionary<PowerMode, Icon> _icons = [];
    private readonly Icon _unknown;

    public ModeIcons()
    {
        foreach (var mode in PowerModes.All)
            _icons[mode] = Draw(mode);
        _unknown = Draw(null);
    }

    public static Color Color(PowerMode mode) => mode switch
    {
        PowerMode.Balanced => System.Drawing.Color.FromArgb(46, 160, 67),
        PowerMode.Enthusiast => System.Drawing.Color.FromArgb(219, 137, 0),
        _ => System.Drawing.Color.FromArgb(207, 34, 46),
    };

    public Icon For(PowerMode? mode) => mode is { } m ? _icons[m] : _unknown;

    public void Dispose()
    {
        foreach (var icon in _icons.Values)
            Destroy(icon);
        Destroy(_unknown);
    }

    /// <summary>Draws the mode tile at any size (used for the icons and the shortcut popup).</summary>
    public static void DrawTile(Graphics g, Rectangle bounds, PowerMode? mode)
    {
        var background = mode is { } m ? Color(m) : System.Drawing.Color.FromArgb(120, 120, 120);
        var text = mode is { } n ? ((int)n + 1).ToString() : "?";
        var inset = Math.Max(1, bounds.Width / 32);
        var tile = Rectangle.Inflate(bounds, -inset, -inset);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var path = RoundedRect(tile, Math.Max(2, bounds.Width * 7 / 32));
        using var brush = new SolidBrush(background);
        g.FillPath(brush, path);
        using var font = new Font("Segoe UI", bounds.Height * 19f / 32, FontStyle.Bold, GraphicsUnit.Pixel);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(text, font, Brushes.White, new RectangleF(bounds.X, bounds.Y + bounds.Height / 32f, bounds.Width, bounds.Height), format);
    }

    private static Icon Draw(PowerMode? mode)
    {
        const int size = 32;
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
            DrawTile(g, new Rectangle(0, 0, size, size), mode);
        return Icon.FromHandle(bitmap.GetHicon());
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static void Destroy(Icon icon)
    {
        DestroyIcon(icon.Handle);
        icon.Dispose();
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(IntPtr handle);
}
