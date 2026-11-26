// SPDX-License-Identifier: GPL-3.0-or-later
// The Spotify card in the island's Music settings: the Client ID of the user's own developer app, the address to
// register for it, and sign in or out. Spotify's queue, playlists and Liked Songs need it; shuffle, loop and the
// local star work without it.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Notchly.App.Platform.Net.Spotify;
using Notchly.Core.Settings;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace Notchly.App.Shell;

internal static class SpotifySettings
{
    public const string Description =
        "Shows the next songs, your playlists and Liked Songs on the Music page, and starts a playlist when nothing is playing. " +
        "Needs Spotify Premium and a free developer app of your own: at developer.spotify.com/dashboard create an app, add the redirect " +
        "address below, and paste its Client ID here. Spotify limits a new developer app to five people, which is plenty for one PC.";

    public static FrameworkElement Card(SettingsStore settings)
    {
        var clientId = new TextBox { PlaceholderText = "Client ID", MinWidth = 280, Text = settings.GetString(DefaultsKey.NotchSpotifyClientId) ?? "" };
        var redirect = new TextBox { Text = SpotifyAccount.RedirectUri, IsReadOnly = true, MinWidth = 280, Margin = new Thickness(0, 6, 0, 0) };
        var status = new TextBlock { FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), MaxWidth = 420 };
        var action = new Button { MinWidth = 110, Margin = new Thickness(8, 0, 0, 0), Appearance = ControlAppearance.Primary };
        CancellationTokenSource? signingIn = null;

        void Refresh()
        {
            var signedIn = SpotifyAccount.IsSignedIn;
            clientId.IsEnabled = !signedIn && signingIn is null;
            action.Content = signedIn ? "Disconnect" : signingIn is null ? "Connect" : "Cancel";
            action.Appearance = signedIn ? ControlAppearance.Secondary : ControlAppearance.Primary;
            if (signingIn is not null) status.Text = "Finish signing in in your browser…";
            else if (signedIn) status.Text = SpotifyAccount.DisplayName.Length > 0 ? $"Connected as {SpotifyAccount.DisplayName}." : "Connected.";
            else if (status.Text is null or "" || status.Text.StartsWith("Connected", StringComparison.Ordinal) || status.Text.StartsWith("Finish", StringComparison.Ordinal))
                status.Text = "Not connected.";
        }

        clientId.TextChanged += (_, _) => settings.Set(DefaultsKey.NotchSpotifyClientId, clientId.Text.Trim());
        action.Click += async (_, _) =>
        {
            if (signingIn is not null) { signingIn.Cancel(); return; }
            if (SpotifyAccount.IsSignedIn) { SpotifyAccount.SignOut(); Refresh(); return; }
            signingIn = new CancellationTokenSource();
            Refresh();
            var problem = await SpotifyAccount.SignInAsync(clientId.Text, signingIn.Token);
            signingIn.Dispose();
            signingIn = null;
            status.Text = problem ?? "";
            Refresh();
        };
        Action changed = () => action.Dispatcher.BeginInvoke(Refresh);
        action.Loaded += (_, _) => { SpotifyAccount.Changed -= changed; SpotifyAccount.Changed += changed; Refresh(); };
        action.Unloaded += (_, _) => SpotifyAccount.Changed -= changed;
        Refresh();

        var header = new StackPanel();
        header.Children.Add(new TextBlock { Text = "Spotify", FontSize = 14 });
        header.Children.Add(new TextBlock { Text = Description, FontSize = 12, TextWrapping = TextWrapping.Wrap, Opacity = 0.7, Margin = new Thickness(0, 2, 0, 0), MaxWidth = 520 });
        header.Children.Add(status);
        var content = new StackPanel();
        var first = new StackPanel { Orientation = Orientation.Horizontal };
        first.Children.Add(clientId);
        first.Children.Add(action);
        content.Children.Add(first);
        content.Children.Add(redirect);
        return new CardControl
        {
            Header = header,
            Icon = new FontIcon { Glyph = "", FontFamily = new FontFamily("Segoe Fluent Icons") },
            Content = content,
            Margin = new Thickness(0, 0, 0, 4),
        };
    }
}
