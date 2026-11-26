// SPDX-License-Identifier: GPL-3.0-or-later
// Windows stand-in for upstream Services/SelectedTextReader.swift (Accessibility's AXSelectedText): the text selected in the control
// that has the focus, read with UI Automation's TextPattern. Nothing is copied and no key is pressed. The read runs on a thread of its
// own with a 350 ms budget, since an app that does not answer must never hold the palette; it is skipped in password fields and when
// the focus is in Notchly itself.
using System.Windows.Automation;
using Serilog;

namespace Notchly.App.Platform.Apps;

public static class SelectedTextReader
{
    /// <summary>Upstream's cap on what is read.</summary>
    public const int MaximumLength = 20_000;
    public static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(350);

    /// <summary>The selected text of the focused control, or null: nothing selected, no text pattern, a password field, or too slow.</summary>
    public static string? Read()
    {
        string? result = null;
        var thread = new Thread(() =>
        {
            try
            {
                var focused = AutomationElement.FocusedElement;
                if (focused is null || focused.Current.ProcessId == Environment.ProcessId || focused.Current.IsPassword) return;
                if (!focused.TryGetCurrentPattern(TextPattern.Pattern, out var pattern) || pattern is not TextPattern text) return;
                var ranges = text.GetSelection();
                if (ranges.Length == 0) return;
                var builder = new System.Text.StringBuilder();
                foreach (var range in ranges)
                {
                    builder.Append(range.GetText(MaximumLength - builder.Length));
                    if (builder.Length >= MaximumLength) break;
                }
                var value = builder.ToString();
                result = value.Length > 0 ? value : null;
            }
            catch (Exception e) when (e is ElementNotAvailableException or InvalidOperationException or System.ComponentModel.Win32Exception
                                      or System.Runtime.InteropServices.COMException or NotSupportedException)
            {
                Log.Debug("Selected text unavailable: {Message}", e.Message);
            }
        }) { IsBackground = true, Name = "Notchly selection" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        return thread.Join(Budget) ? result : null;
    }
}
