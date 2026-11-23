// SPDX-License-Identifier: GPL-3.0-or-later
// The Notifications section of the island's settings (upstream NotchNotificationStrings and the
// Accessibility permission row). Windows-specific: instead of macOS Accessibility, Notchly needs package
// identity, registered once in Developer Mode, and Windows' permission to read notifications; this card says
// which step is missing and offers it. Setting up and removing restart Notchly, which gains or loses the
// identity only when it starts.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Notchly.App.Platform.Notifications;
using Notchly.App.Platform.Shell;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Notchly.App.Shell;

internal static class NotificationSettings
{
    public const string Description = "New Windows notifications in the Dynamic Island: a banner beside the camera, then the island's Notifications page with Open, Dismiss and Clear All.";
    public const string HideBannerHint = "Once the island shows a notification, Notchly removes the original from Windows, so it also leaves the notification centre. " +
                                         "To see an app's notifications here, keep its notifications on in Windows settings.";
    public const string Privacy = "Shows new notifications only. Messages stay in memory and are cleared when you lock this PC or turn this off.";

    /// <summary>The command-line flag a restart after setup or removal starts with: it reopens these settings.</summary>
    public const string SetupFlag = "--notification-setup";

    private enum Step { Ready, AccessOff, NeedsPermission, NeedsRestart, DeveloperModeOff, NeedsSetup }

    private static Step Current()
    {
        if (PackageIdentity.HasIdentity)
            return NotificationListener.Access switch
            {
                NotificationAccess.Allowed => Step.Ready,
                NotificationAccess.Denied => Step.AccessOff,
                _ => Step.NeedsPermission,
            };
        if (PackageIdentity.RegisteredHere()) return Step.NeedsRestart;
        return PackageIdentity.DeveloperModeOn ? Step.NeedsSetup : Step.DeveloperModeOff;
    }

    /// <summary>The status line and the next step, refreshed when Windows' answer changes or the page shows again.</summary>
    public static CardControl StatusCard()
    {
        var status = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Opacity = 0.7, Margin = new Thickness(0, 2, 0, 0), MaxWidth = 460 };
        var problem = new TextBlock
        {
            FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), MaxWidth = 460,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0x7A, 0x00)), Visibility = Visibility.Collapsed,
        };
        var title = new TextBlock { FontSize = 14 };
        var header = new StackPanel();
        header.Children.Add(title);
        header.Children.Add(status);
        header.Children.Add(problem);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var card = new CardControl
        {
            Header = header, Content = buttons, Margin = new Thickness(0, 0, 0, 4),
            Icon = new FontIcon { Glyph = "", FontFamily = new FontFamily("Segoe Fluent Icons") },
        };

        Button Action(string text, Action action, bool primary = false)
        {
            var button = new Button { Content = text, Margin = new Thickness(8, 0, 0, 0), Appearance = primary ? ControlAppearance.Primary : ControlAppearance.Secondary };
            button.Click += (_, _) => action();
            return button;
        }

        void Build()
        {
            buttons.Children.Clear();
            var step = Current();
            (title.Text, status.Text) = step switch
            {
                Step.Ready => ("Ready", "Windows lets Notchly read new notifications."),
                Step.AccessOff => ("Windows access off", "Notification access for Notchly is off in Windows settings. Turn it on there, then come back."),
                Step.NeedsPermission => ("Needs your permission", "Windows asks once whether Notchly may read your notifications."),
                Step.NeedsRestart => ("Restart to finish", "Notchly is set up with Windows and needs to start again to use it."),
                Step.DeveloperModeOff => ("Developer Mode off", "Notchly registers itself with Windows through Developer Mode, which lets it skip a signed installer. " +
                                                                 "Turn on Developer Mode in Windows settings, then come back."),
                _ => ("Needs setup", "Notchly registers itself with Windows for this user, without admin rights, and restarts once. " +
                                     "Then Windows asks whether Notchly may read your notifications."),
            };
            switch (step)
            {
                case Step.Ready:
                    buttons.Children.Add(Action("Remove notification access", Remove));
                    break;
                case Step.AccessOff:
                    buttons.Children.Add(Action("Open Windows settings", () => ShellActions.Open("ms-settings:privacy-notifications"), primary: true));
                    buttons.Children.Add(Action("Check again", Build));
                    buttons.Children.Add(Action("Remove notification access", Remove));
                    break;
                case Step.NeedsPermission:
                    buttons.Children.Add(Action("Allow access", async () => { await NotificationListener.RequestAccessAsync(); Build(); }, primary: true));
                    buttons.Children.Add(Action("Remove notification access", Remove));
                    break;
                case Step.NeedsRestart:
                    buttons.Children.Add(Action("Restart Notchly", Restart, primary: true));
                    break;
                case Step.DeveloperModeOff:
                    buttons.Children.Add(Action("Open developer settings", () => ShellActions.Open("ms-settings:developers"), primary: true));
                    buttons.Children.Add(Action("Check again", Build));
                    break;
                default:
                    buttons.Children.Add(Action("Set up notifications", SetUp, primary: true));
                    break;
            }
        }

        async void SetUp()
        {
            buttons.IsEnabled = false;
            problem.Visibility = Visibility.Collapsed;
            status.Text = "Setting up…";
            var error = await PackageIdentity.RegisterAsync();
            buttons.IsEnabled = true;
            if (error is null) { Restart(); return; }
            problem.Text = error;
            problem.Visibility = Visibility.Visible;
            Build();
        }

        void Restart()
        {
            if (PackageIdentity.Relaunch(removePackage: false, "--background", SetupFlag)) App.Current.Quit();
            else ShowProblem("Notchly could not restart itself. Quit it from the tray and start it again.");
        }

        void Remove()
        {
            if (System.Windows.MessageBox.Show(Window.GetWindow(card)!,
                    "Remove Notchly's notification access? Notchly restarts without it, and the island's Notifications page asks to set it up again.",
                    "Remove notification access", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Question)
                != System.Windows.MessageBoxResult.OK) return;
            // The package goes once this Notchly has quit: removing it from here would end Notchly mid-way.
            if (PackageIdentity.Relaunch(removePackage: true, "--background", SetupFlag)) App.Current.Quit();
            else ShowProblem("Notchly could not remove its registration. Run “Get-AppxPackage Notchly | Remove-AppxPackage” in PowerShell.");
        }

        void ShowProblem(string text)
        {
            problem.Text = text;
            problem.Visibility = Visibility.Visible;
        }

        Build();
        Action changed = () => card.Dispatcher.BeginInvoke(Build);
        card.Loaded += (_, _) => { NotificationListener.AccessChanged -= changed; NotificationListener.AccessChanged += changed; Build(); };
        card.Unloaded += (_, _) => NotificationListener.AccessChanged -= changed;
        return card;
    }
}
