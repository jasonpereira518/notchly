// SPDX-License-Identifier: GPL-3.0-or-later
// Whether the keyboard focus is in a password field, found with UI Automation (upstream asks macOS whether secure input is
// on). Asking UI Automation takes many milliseconds and crosses into another process, so the answer is worked out when the
// focus changes and kept in a volatile flag the keyboard hook reads; the hook itself never waits.
using System.Windows.Automation;
using Serilog;

namespace Notchly.App.Platform.Input;

public sealed class PasswordFieldProbe : IDisposable
{
    private readonly AutomationFocusChangedEventHandler _handler;
    private volatile bool _inPassword;
    private bool _running;

    public PasswordFieldProbe() => _handler = (_, _) => Check();

    /// <summary>The focused control is a password field. Read from the hook thread.</summary>
    public bool InPasswordField => _inPassword;

    public void Start()
    {
        if (_running) return;
        _running = true;
        try { Automation.AddAutomationFocusChangedEventHandler(_handler); }
        catch (Exception e) { Log.Warning(e, "Focus changes could not be watched"); _running = false; }
        Task.Run(Check);
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        try { Automation.RemoveAutomationFocusChangedEventHandler(_handler); }
        catch (Exception e) { Log.Debug(e, "Focus watching could not be stopped"); }
        _inPassword = false;
    }

    private void Check()
    {
        try { _inPassword = AutomationElement.FocusedElement?.Current.IsPassword == true; }
        catch (Exception e) when (e is ElementNotAvailableException or InvalidOperationException or System.Runtime.InteropServices.COMException) { _inPassword = false; }
    }

    public void Dispose() => Stop();
}
