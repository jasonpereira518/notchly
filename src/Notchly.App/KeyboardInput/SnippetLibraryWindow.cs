// SPDX-License-Identifier: GPL-3.0-or-later
// The snippet library: a small window to search your snippets (name, trigger, text and folder, ignoring case and accents), grouped by
// folder with the loose ones last. Up and Down choose, Return sends, Esc closes; typing always goes to the search box.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Notchly.Core.Input;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace Notchly.App.KeyboardInput;

internal sealed class SnippetLibraryWindow : FluentWindow
{
    private readonly IReadOnlyList<TextSnippet> _snippets;
    private readonly TextBox _search = new() { PlaceholderText = "Search snippets", Margin = new Thickness(0, 0, 0, 10) };
    private readonly ListBox _list = new() { MaxHeight = 360, BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent };

    public event Action<TextSnippet>? Picked;

    public SnippetLibraryWindow(IReadOnlyList<TextSnippet> snippets)
    {
        _snippets = snippets;
        Title = "Snippets";
        Width = 440;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        ExtendsContentIntoTitleBar = true;
        WindowBackdropType = WindowBackdropType.Acrylic;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var root = new StackPanel { Margin = new Thickness(18, 32, 18, 16) };
        root.Children.Add(new TextBlock { Text = "Snippets", FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
        root.Children.Add(_search);
        root.Children.Add(_list);
        Content = root;
        _search.TextChanged += (_, _) => Rebuild();
        _list.MouseDoubleClick += (_, _) => Pick();
        PreviewKeyDown += OnKey;
        Loaded += (_, _) => { Rebuild(); _search.Focus(); };
        Deactivated += (_, _) => { if (IsVisible) Close(); };
    }

    private void Rebuild()
    {
        _list.Items.Clear();
        var shown = _snippets.Where(s => SnippetSupport.MatchesLibrary(s, _search.Text)).ToList();
        if (shown.Count == 0)
        {
            _list.Items.Add(new ListBoxItem { Content = new TextBlock { Text = _snippets.Count == 0 ? "No snippets yet. Add some in Settings → Keyboard." : "Nothing matches.", Opacity = 0.7 }, IsEnabled = false });
            return;
        }
        foreach (var (folder, items) in SnippetSupport.Grouped(shown))
        {
            if (folder.Length > 0) _list.Items.Add(new ListBoxItem { Content = new TextBlock { Text = folder, FontSize = 11, Opacity = 0.6, Margin = new Thickness(0, 6, 0, 0) }, IsEnabled = false });
            foreach (var snippet in items)
            {
                var row = new StackPanel { Margin = new Thickness(2, 4, 2, 4) };
                row.Children.Add(new TextBlock { Text = snippet.Name.Length > 0 ? snippet.Name : snippet.Trigger, FontSize = 14 });
                var preview = snippet.Replacement.ReplaceLineEndings(" ");
                row.Children.Add(new TextBlock { Text = snippet.Trigger + "   " + (preview.Length > 60 ? preview[..60] + "…" : preview), FontSize = 11, Opacity = 0.65, TextTrimming = TextTrimming.CharacterEllipsis });
                _list.Items.Add(new ListBoxItem { Content = row, Tag = snippet });
            }
        }
        _list.SelectedItem = _list.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag is not null);
    }

    private void Pick()
    {
        if (_list.SelectedItem is ListBoxItem { Tag: TextSnippet snippet }) Picked?.Invoke(snippet);
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: Close(); e.Handled = true; break;
            case Key.Enter: Pick(); e.Handled = true; break;
            case Key.Down or Key.Up:
            {
                var items = _list.Items.OfType<ListBoxItem>().Where(i => i.Tag is not null).ToList();
                if (items.Count == 0) break;
                var at = Math.Max(0, items.IndexOf((ListBoxItem)_list.SelectedItem));
                var next = Math.Clamp(at + (e.Key == Key.Down ? 1 : -1), 0, items.Count - 1);
                _list.SelectedItem = items[next];
                _list.ScrollIntoView(items[next]);
                e.Handled = true;
                break;
            }
        }
    }
}
