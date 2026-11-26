// SPDX-License-Identifier: GPL-3.0-or-later
// The Music page's two Spotify-backed views, built in code like the other island pages: "Up next" (the songs queued
// after the current one) and, when nothing is playing, the playlist picker. The player itself (cover, timeline,
// shuffle, previous, play, next, loop, star) stays in NotchWindow.xaml. A Windows addition.
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Notchly.App.Platform.Net.Spotify;
using Notchly.Core.Notch.Music;

namespace Notchly.App.Island.Music;

public sealed class MusicPage
{
    /// <summary>Songs listed under the player: a couple ahead, as many as fit the extras slot.</summary>
    public const int UpcomingCount = 3;
    private const double ArtSize = 36;

    private static readonly SolidColorBrush Dim = Frozen(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush Tile = Frozen(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush Pink = Frozen(Color.FromRgb(0xFF, 0x5D, 0x8F));

    private readonly NotchWindow _window;
    private readonly MusicService _music;
    private readonly Action _openSettings;
    private readonly Func<string, Task> _play;
    private readonly Dictionary<string, ImageSource> _art = new(StringComparer.Ordinal);
    private string _status = "";

    /// <param name="play">Starts a playlist and reports; the page shows what it answers.</param>
    public MusicPage(NotchWindow window, MusicService music, Action openSettings, Func<string, Task> play)
    {
        _window = window;
        _music = music;
        _openSettings = openSettings;
        _play = play;
    }

    /// <summary>What the idle view says about the last start attempt (a failure's reason, or that it is starting).</summary>
    public string Status
    {
        get => _status;
        set { _status = value; RenderIdle(); }
    }

    // MARK: Up next

    public void RenderQueue()
    {
        var rows = _window.QueueRows;
        rows.Children.Clear();
        var upcoming = _music.Queue.Take(UpcomingCount).ToList();
        foreach (var item in upcoming) rows.Children.Add(QueueRow(item));
        var message = upcoming.Count > 0 ? "" : _music.Player is null ? "Reading Spotify's queue…" : "Nothing is queued after this song.";
        _window.QueueMessage.Text = message;
        _window.QueueMessage.Visibility = message.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private FrameworkElement QueueRow(NotchQueueItem item)
    {
        var grid = new Grid { Height = 44, Margin = new Thickness(0, 0, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ArtSize + 10) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(Cover(item.ArtUrl, null));
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = item.Title, FontSize = 13, FontWeight = FontWeights.Medium });
        text.Children.Add(new TextBlock { Text = item.Artist, FontSize = 11, Foreground = Dim, Margin = new Thickness(0, 1, 0, 0) });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        if (item.DurationSeconds > 0)
        {
            var time = TimeSpan.FromSeconds(Math.Round(item.DurationSeconds));
            var length = new TextBlock
            {
                Text = $"{(int)time.TotalMinutes}:{time.Seconds:00}", FontSize = 11, Foreground = Dim,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 2, 0),
            };
            Grid.SetColumn(length, 2);
            grid.Children.Add(length);
        }
        return grid;
    }

    // MARK: Idle (nothing playing)

    /// <summary>Fills the idle view: the playlists, or the reason there are none.</summary>
    public void RenderIdle()
    {
        var list = _window.MusicIdleList;
        list.Children.Clear();
        if (!SpotifyAccount.IsSignedIn)
        {
            _window.MusicIdleStatus.Text = "Nothing playing";
            var hint = new TextBlock
            {
                Text = SpotifyAccount.ClientId.Length == 0
                    ? "Connect Spotify to start your playlists from here. Add your Client ID in Settings, under Music."
                    : "Connect Spotify in Settings to start your playlists from here.",
                FontSize = 12, Foreground = Dim, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None, Margin = new Thickness(2, 0, 0, 10),
            };
            list.Children.Add(hint);
            var open = new Button { Style = (Style)_window.Resources["ChipButton"], Content = "Open settings", HorizontalAlignment = HorizontalAlignment.Left };
            open.Click += (_, _) => _openSettings();
            list.Children.Add(open);
            return;
        }
        _window.MusicIdleStatus.Text = _status.Length > 0 ? _status : "Nothing playing. Pick something to play.";
        if (_music.Playlists is not { } playlists)
        {
            list.Children.Add(new TextBlock { Text = "Loading your playlists…", FontSize = 12, Foreground = Dim, Margin = new Thickness(2, 4, 0, 0) });
            return;
        }
        if (_music.LikedSongsUri is { } liked) list.Children.Add(PlaylistRow("Liked Songs", "Your starred songs", null, liked, heart: true));
        foreach (var playlist in playlists)
            list.Children.Add(PlaylistRow(playlist.Name, playlist.TrackCount > 0 ? $"{playlist.TrackCount} songs" : playlist.Owner, playlist.ArtUrl, playlist.Uri, heart: false));
        if (playlists.Count == 0 && _music.LikedSongsUri is null)
            list.Children.Add(new TextBlock { Text = "No playlists found in your Spotify account.", FontSize = 12, Foreground = Dim, Margin = new Thickness(2, 4, 0, 0) });
    }

    private FrameworkElement PlaylistRow(string name, string detail, string? artUrl, string uri, bool heart)
    {
        var grid = new Grid { Height = 44 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ArtSize + 10) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(Cover(artUrl, heart ? "" : ""));
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = name, FontSize = 13, FontWeight = FontWeights.Medium });
        if (detail.Length > 0) text.Children.Add(new TextBlock { Text = detail, FontSize = 11, Foreground = Dim, Margin = new Thickness(0, 1, 0, 0) });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var play = new TextBlock
        {
            Text = "", FontFamily = (FontFamily)_window.Resources["IconFont"], FontSize = 14, Foreground = Dim,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 6, 0),
        };
        Grid.SetColumn(play, 2);
        grid.Children.Add(play);
        var button = new Button
        {
            Style = (Style)_window.Resources["PlainButton"], Content = grid, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            ToolTip = $"Play {name}",
        };
        System.Windows.Automation.AutomationProperties.SetName(button, $"Play {name}");
        button.Click += async (_, _) => await _play(uri);
        return button;
    }

    // MARK: Pieces

    /// <summary>A square cover: the picture when there is one (loaded by Windows in the background), else a tile with a glyph.</summary>
    private FrameworkElement Cover(string? url, string? glyph)
    {
        var tile = new Border { Width = ArtSize, Height = ArtSize, CornerRadius = new CornerRadius(6), Background = Tile, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        if (!string.IsNullOrEmpty(url) && Image(url) is { } image)
            tile.Background = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
        else if (glyph is not null)
            tile.Child = new TextBlock
            {
                Text = glyph, FontFamily = (FontFamily)_window.Resources["IconFont"], FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center, Foreground = glyph == "" ? Pink : Dim,
            };
        return tile;
    }

    private ImageSource? Image(string url)
    {
        if (_art.TryGetValue(url, out var cached)) return cached;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var address) || address.Scheme != Uri.UriSchemeHttps) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = address;
            image.DecodePixelWidth = (int)ArtSize * 2;
            // The default cache option lets Windows fetch a web picture in the background instead of holding up the island.
            image.EndInit();
            if (_art.Count > 200) _art.Clear();
            _art[url] = image;
            return image;
        }
        catch (Exception e) when (e is IOException or NotSupportedException or UriFormatException)
        {
            return null;
        }
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
