using System.Diagnostics;
using System.Security;
using System.Text;

namespace UwPower.Core;

/// <summary>
/// Starts the tray app at logon through a Task Scheduler task with "run with highest privileges",
/// so it runs elevated (needed to see power requests) without a UAC prompt at every logon.
/// Creating or deleting the task needs administrator rights once.
/// </summary>
public static class Autostart
{
    public const string TaskName = "UwPower Tray";

    public static bool IsEnabled() => RunSchtasks($"/Query /TN \"{TaskName}\"", out _) == 0;

    public static void Enable(string trayExePath)
    {
        var user = $"{Environment.UserDomainName}\\{Environment.UserName}";
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Starts Uniwill Power Switch (power mode tray app) at logon.</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{SecurityElement.Escape(user)}</UserId>
                  <Delay>PT5S</Delay>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{SecurityElement.Escape(user)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{SecurityElement.Escape(trayExePath)}</Command>
                </Exec>
              </Actions>
            </Task>
            """;

        var xmlPath = Path.Combine(Path.GetTempPath(), $"uwpower-task-{Guid.NewGuid():N}.xml");
        File.WriteAllText(xmlPath, xml, Encoding.Unicode);
        try
        {
            if (RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{xmlPath}\" /F", out var output) != 0)
                throw new InvalidOperationException($"Could not create the logon task: {output.Trim()}");
        }
        finally
        {
            File.Delete(xmlPath);
        }
    }

    public static void Disable()
    {
        if (IsEnabled() && RunSchtasks($"/Delete /TN \"{TaskName}\" /F", out var output) != 0)
            throw new InvalidOperationException($"Could not delete the logon task: {output.Trim()}");
    }

    /// <summary>Runs this program again elevated (UAC prompt) with the given arguments and waits.</summary>
    public static int RunElevated(string exePath, string arguments)
    {
        var psi = new ProcessStartInfo(exePath, arguments) { UseShellExecute = true, Verb = "runas" };
        try
        {
            using var process = Process.Start(psi)!;
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new OperationCanceledException("Administrator permission was not granted.");
        }
    }

    private static int RunSchtasks(string arguments, out string output)
    {
        var psi = new ProcessStartInfo("schtasks.exe", arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        output = stdout.Result + stderr.Result;
        return process.ExitCode;
    }
}
