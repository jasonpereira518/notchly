// SPDX-License-Identifier: GPL-3.0-or-later
// The "run elevated at sign-in" task and the two startup entries (the Run key and the task), kept so that exactly one of
// them starts Notchly. Creating or removing a highest-privilege task takes one administrator prompt; asking whether it
// exists does not.
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using Notchly.Core;
using Notchly.Core.Tools;
using Serilog;

namespace Notchly.App.Platform.Shell;

public static class ElevatedStartup
{
    /// <summary>True when the task exists (asking needs no administrator rights).</summary>
    public static bool Exists()
    {
        try
        {
            using var query = Process.Start(new ProcessStartInfo("schtasks.exe", $"/Query /TN \"{StartupTaskSupport.TaskName}\"")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            });
            if (query is null) return false;
            query.WaitForExit(5000);
            return query.HasExited && query.ExitCode == 0;
        }
        catch (Exception e) { Log.Debug(e, "Could not ask about the startup task"); return false; }
    }

    /// <summary>The task's XML for this executable and this user.</summary>
    public static string Xml()
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Unknown executable path");
        return StartupTaskSupport.TaskXml(exe, "--background", WindowsIdentity.GetCurrent().Name);
    }

    /// <summary>Creates the task, behind one administrator prompt. False when the prompt was declined or Windows refused.</summary>
    public static bool Create()
    {
        var file = Path.Combine(Path.GetTempPath(), $"notchly-task-{Guid.NewGuid():N}.xml");
        try
        {
            // schtasks reads the definition as UTF-16 with a byte-order mark.
            File.WriteAllText(file, Xml(), new UnicodeEncoding(false, true));
            return RunElevated($"/Create /TN \"{StartupTaskSupport.TaskName}\" /XML \"{file}\" /F");
        }
        finally { try { File.Delete(file); } catch (IOException) { /* a stray file in Temp */ } }
    }

    /// <summary>Removes the task, behind one administrator prompt.</summary>
    public static bool Delete() => RunElevated($"/Delete /TN \"{StartupTaskSupport.TaskName}\" /F");

    private static bool RunElevated(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("schtasks.exe", arguments)
            {
                Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (process is null) return false;
            process.WaitForExit(30000);
            return process.HasExited && process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            // The prompt was declined.
            Log.Information("Elevated startup change declined: {Message}", e.Message);
            return false;
        }
    }

    /// <summary>
    /// Makes the Run key agree with the settings: present when launch at sign-in is on and the task is not
    /// already starting Notchly, absent otherwise.
    /// </summary>
    public static void SyncRunKey(bool launchAtLogin, bool taskPresent)
    {
        var wanted = StartupTaskSupport.RunKeyWanted(launchAtLogin, taskPresent);
        if (LaunchAtLogin.IsEnabled != wanted) LaunchAtLogin.Set(wanted);
    }

    public static bool RunningElevated => Environment.IsPrivilegedProcess;

    public static string Describe() => $"{AppIdentity.Name} {(RunningElevated ? "is running as administrator" : "is running without administrator rights")}.";
}
