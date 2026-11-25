// SPDX-License-Identifier: GPL-3.0-or-later
// New on Windows (no upstream counterpart): the optional "run elevated at sign-in" task. Windows starts an app at sign-in
// without administrator rights through the Run key; a Task Scheduler logon task with the highest privileges the user's
// account has starts it elevated, so its hooks and key presses reach administrator windows too. The pure parts are
// here: the task's definition and which of the two startup entries should exist.
using System.Security;

namespace Notchly.Core.Tools;

public static class StartupTaskSupport
{
    public const string TaskName = "Notchly elevated start";

    /// <summary>
    /// The task: at this user's logon, run the app with the highest privileges, without the usual battery and
    /// time limits, and never twice at once.
    /// </summary>
    public static string TaskXml(string exePath, string arguments, string userId)
    {
        var user = SecurityElement.Escape(userId);
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Starts Notchly with administrator rights at sign-in, so its shortcuts and hooks reach administrator windows.</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{user}</UserId>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{user}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>true</StartWhenAvailable>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
                <Enabled>true</Enabled>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{SecurityElement.Escape(exePath)}</Command>
                  <Arguments>{SecurityElement.Escape(arguments)}</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    /// <summary>
    /// The Run-key entry is wanted when the user asked for launch at sign-in and the elevated task is not already
    /// doing it: both would start Notchly twice.
    /// </summary>
    public static bool RunKeyWanted(bool launchAtLogin, bool elevatedTaskPresent) => launchAtLogin && !elevatedTaskPresent;

    /// <summary>
    /// Asking for the elevated start also means launching at sign-in, so it is shown on whenever the task exists
    /// and the setting says so (a task removed by hand turns the switch off again).
    /// </summary>
    public static bool ElevatedStartShownOn(bool setting, bool taskPresent) => setting && taskPresent;
}
