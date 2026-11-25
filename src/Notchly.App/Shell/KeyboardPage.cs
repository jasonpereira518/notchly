// SPDX-License-Identifier: GPL-3.0-or-later
// Settings → Keyboard: key debounce, the super key, text snippets and quit protection.
using System.Windows;

namespace Notchly.App.Shell;

internal sealed partial class SettingsPages
{
    private FrameworkElement KeyboardPage() => Page(PageTitle("Keyboard"));
}
