using UwPower.Core;
using UwPower.Core.Ipc;

namespace UwPower.Tray;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        using var singleInstance = new Mutex(true, TrayPipe.InstanceMutexName, out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show("Uniwill Power Switch is already running in the notification area.",
                "Uniwill Power Switch", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Application.ThreadException += (_, e) => Log.Write($"UI error: {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write($"Fatal error: {e.ExceptionObject}");

        ApplicationConfiguration.Initialize();

        // Development aid: UwPowerTray.exe --screenshot settings.png renders the settings window and exits.
        if (args.Length == 2 && args[0] == "--screenshot")
        {
            Screenshot(args[1]);
            return;
        }

        Application.Run(new TrayContext());
    }

    private static void Screenshot(string path)
    {
        var tray = new TrayContext();
        var deadline = DateTime.Now.AddSeconds(8);
        while (!tray.Controller.Connected && DateTime.Now < deadline)
        {
            Application.DoEvents();
            Thread.Sleep(50);
        }
        Thread.Sleep(500);
        Application.DoEvents();

        using var form = new SettingsForm(tray) { StartPosition = FormStartPosition.Manual, Location = new Point(-5000, -5000) };
        form.Show();
        form.RefreshState();
        Application.DoEvents();
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        form.Close();
        tray.ExitThread();
    }
}
