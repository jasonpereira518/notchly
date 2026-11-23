// SPDX-License-Identifier: GPL-3.0-or-later
// Windows-only (upstream needs none): Notchly.exe's package identity, which Windows requires before an app may
// read notifications. A sparse package (Package\AppxManifest.xml beside the exe) is registered for the current
// user in Developer Mode with Add-AppxPackage, unsigned and without admin rights, and removed with
// Remove-AppxPackage. The exe gains the identity only when it starts again, so both steps relaunch Notchly.
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text;
using Microsoft.Win32;
using Notchly.Core;
using Serilog;

namespace Notchly.App.Platform.Notifications;

public static class PackageIdentity
{
    /// <summary>The package's name, as Package\AppxManifest.xml and app.manifest's msix element give it.</summary>
    public const string PackageName = "Notchly";
    public const string Publisher = "CN=Notchly";

    /// <summary>The folder Notchly.exe runs from; the package's external location.</summary>
    public static string AppFolder => Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    public static string ManifestPath => Path.Combine(AppFolder, "Package", "AppxManifest.xml");

    private static bool? _hasIdentity;

    /// <summary>Whether this process runs with Notchly's package identity (fixed for the process's life).</summary>
    public static bool HasIdentity => _hasIdentity ??=
        ReadIdentity()?.StartsWith(PackageName + "_", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>This process's package full name, or null without identity.</summary>
    public static string? FullName => HasIdentity ? ReadIdentity() : null;

    private static string? ReadIdentity()
    {
        try
        {
            var length = 0;
            if (Native.GetCurrentPackageFullName(ref length, null) == Native.APPMODEL_ERROR_NO_PACKAGE || length <= 0) return null;
            var buffer = new char[length];
            return Native.GetCurrentPackageFullName(ref length, buffer) == 0 ? new string(buffer, 0, Math.Max(0, length - 1)) : null;
        }
        catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException) { return null; }
    }

    /// <summary>Developer Mode, which lets Windows register an unsigned package (AppModelUnlock).</summary>
    public static bool DeveloperModeOn
    {
        get
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock");
                return key?.GetValue("AllowDevelopmentWithoutDevLicense") is int value && value == 1;
            }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return false; }
        }
    }

    /// <summary>Where the registered package lives for this user (its Package folder), or null when it isn't registered.</summary>
    public static string? RegisteredLocation()
    {
        try
        {
            var manager = new Windows.Management.Deployment.PackageManager();
            return manager.FindPackagesForUser("", PackageName, Publisher).Select(p => p.InstalledPath).FirstOrDefault();
        }
        catch (Exception e)
        {
            Log.Debug(e, "Could not look up the Notchly package");
            return null;
        }
    }

    /// <summary>Registered for this exe's folder, not a copy elsewhere.</summary>
    public static bool RegisteredHere() => RegisteredLocation() is { } location
        && string.Equals(Path.GetFullPath(location).TrimEnd('\\'), Path.GetFullPath(Path.Combine(AppFolder, "Package")).TrimEnd('\\'),
                         StringComparison.OrdinalIgnoreCase);

    private static string Quote(string value) => "'" + value.Replace("'", "''") + "'";

    private static string Encoded(string script) => Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

    /// <summary>
    /// Registers the package for this folder (a registration elsewhere is replaced). Returns null on success,
    /// otherwise Windows' message. Nothing running is affected: Notchly has no identity until it restarts.
    /// </summary>
    public static async Task<string?> RegisterAsync()
    {
        if (!File.Exists(ManifestPath)) return "Notchly's package manifest is missing beside Notchly.exe.";
        var here = Path.Combine(AppFolder, "Package");
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            try {
                Get-AppxPackage -Name {{PackageName}} | Where-Object { $_.InstallLocation -ne {{Quote(here)}} } | Remove-AppxPackage
                Add-AppxPackage -Register {{Quote(ManifestPath)}} -ExternalLocation {{Quote(AppFolder)}}
            } catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }
            """;
        var info = new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {Encoded(script)}")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true,
        };
        try
        {
            using var process = Process.Start(info)!;
            var error = process.StandardError.ReadToEndAsync();
            _ = process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            var message = (await error).Trim();
            if (process.ExitCode == 0) { Log.Information("Registered the Notchly package for {Folder}", AppFolder); return null; }
            Log.Warning("Registering the Notchly package failed: {Message}", message);
            return message.Length > 0 ? message : $"Windows refused the package (exit code {process.ExitCode}).";
        }
        catch (Exception e)
        {
            Log.Warning(e, "Could not run PowerShell to register the package");
            return e.Message;
        }
    }

    /// <summary>
    /// Starts Notchly again once this one has quit, after <paramref name="removePackage"/> if asked. The helper is
    /// created through WMI rather than as Notchly's child, so it keeps no package identity and survives both
    /// Notchly's exit and the package's removal. Returns false when it could not be started.
    /// </summary>
    public static bool Relaunch(bool removePackage, params string[] arguments)
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return false;
        var args = arguments.Length == 0 ? "" : " -ArgumentList " + string.Join(",", arguments.Select(Quote));
        var script = $$"""
            Wait-Process -Id {{Environment.ProcessId}} -Timeout 30 -ErrorAction SilentlyContinue
            {{(removePackage ? $"Get-AppxPackage -Name {PackageName} | Remove-AppxPackage -ErrorAction SilentlyContinue" : "")}}
            Start-Process -FilePath {{Quote(exe)}}{{args}}
            """;
        var command = $"powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand {Encoded(script)}";
        try
        {
            using var processClass = new ManagementClass("Win32_Process");
            using var startupClass = new ManagementClass("Win32_ProcessStartup");
            using var startup = startupClass.CreateInstance();
            startup["ShowWindow"] = (ushort)0; // SW_HIDE
            using var input = processClass.GetMethodParameters("Create");
            input["CommandLine"] = command;
            input["ProcessStartupInformation"] = startup;
            using var output = processClass.InvokeMethod("Create", input, null);
            if (output?["ReturnValue"] is uint result && result == 0) return true;
            Log.Warning("WMI could not start Notchly's relauncher ({Result})", output?["ReturnValue"]);
        }
        catch (Exception e) { Log.Warning(e, "WMI could not start Notchly's relauncher"); }
        try
        {
            // Without WMI, a plain child still restarts Notchly; only a removal could stop it.
            Process.Start(new ProcessStartInfo("powershell.exe", command["powershell.exe ".Length..]) { UseShellExecute = false, CreateNoWindow = true });
            return true;
        }
        catch (Exception e)
        {
            Log.Warning(e, "Could not start Notchly's relauncher");
            return false;
        }
    }
}
