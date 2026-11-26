// SPDX-License-Identifier: GPL-3.0-or-later
// The Settings search box's index and results view. Each page is built once into a private index (its rows are
// ordinary code-built controls bound through SettingsProxy, so they work wherever they are hosted); a query then
// shows the matching rows grouped under their page. A Windows addition, after upstream's SettingsSearchSupport.swift.
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Notchly.Core.Settings;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Notchly.App.Shell;

internal sealed class SettingsSearch(SettingsPages pages, Func<SettingsPage, string> pageTitle, Action<SettingsPage> openPage)
{
    /// <summary>One row (or caption, or button strip) with its folded text.</summary>
    private sealed record Item(UIElement Element, string Text);

    /// <summary>A section: its header (none for what comes before the first one) and the rows under it.</summary>
    private sealed record Block(UIElement? Header, string HeaderText, List<Item> Items);

    private sealed record PageIndex(SettingsPage Page, string Title, string FoldedTitle, List<Block> Blocks);

    private readonly StackPanel _results = new();
    private List<PageIndex>? _index;

    /// <summary>The element to show in place of the page while a query is typed.</summary>
    public FrameworkElement Results => _results;

    /// <summary>Fills the results for <paramref name="query"/>; false when it is blank (the caller shows the page instead).</summary>
    public bool Show(string query)
    {
        var terms = SettingsSearchSupport.Terms(query);
        Detach();
        if (terms.Count == 0) return false;

        var found = false;
        foreach (var page in Index())
        {
            var heading = false;
            foreach (var block in page.Blocks)
            {
                var matches = block.Items
                    .Where(item => SettingsSearchSupport.MatchesFolded(terms, item.Text + " " + block.HeaderText + " " + page.FoldedTitle))
                    .ToList();
                if (matches.Count == 0) continue;
                if (!heading) { _results.Children.Add(PageHeading(page)); heading = true; }
                if (block.Header is not null) _results.Children.Add(block.Header);
                foreach (var item in matches) _results.Children.Add(item.Element);
            }
            found |= heading;
        }
        if (!found)
            _results.Children.Add(new TextBlock { Text = $"No settings match \"{query.Trim()}\".", Opacity = 0.7, Margin = new Thickness(0, 16, 0, 0) });
        return true;
    }

    /// <summary>Takes the index's rows back out of the results, so each is hosted in one place only.</summary>
    public void Detach() => _results.Children.Clear();

    private FrameworkElement PageHeading(PageIndex page)
    {
        var button = new Button
        {
            Content = new TextBlock { Text = page.Title, FontSize = 20, FontWeight = FontWeights.SemiBold },
            Appearance = ControlAppearance.Transparent, Padding = new Thickness(4, 2, 8, 2),
            Margin = new Thickness(0, 12, 0, 4), HorizontalAlignment = HorizontalAlignment.Left, ToolTip = $"Open {page.Title}",
        };
        System.Windows.Automation.AutomationProperties.SetName(button, $"Open {page.Title}");
        button.Click += (_, _) => openPage(page.Page);
        return button;
    }

    // MARK: Index

    private List<PageIndex> Index() => _index ??= Enum.GetValues<SettingsPage>().Select(BuildIndex).ToList();

    private PageIndex BuildIndex(SettingsPage page)
    {
        var title = pageTitle(page);
        var blocks = new List<Block> { new(null, "", []) };
        if (pages.Build(page) is Panel panel)
        {
            var sectionStyle = Application.Current.Resources["SectionHeader"];
            // Taking the children out of the panel first: an element keeps one parent.
            var children = panel.Children.Cast<UIElement>().ToList();
            panel.Children.Clear();
            foreach (var child in children)
            {
                if (child is TextBlock { FontSize: 28 }) continue; // The page's own title; the results have their heading.
                if (child is TextBlock header && ReferenceEquals(header.Style, sectionStyle))
                {
                    blocks.Add(new Block(header, SettingsSearchSupport.Fold(header.Text), []));
                    continue;
                }
                var text = new StringBuilder();
                Collect(child, text);
                blocks[^1].Items.Add(new Item(child, SettingsSearchSupport.Fold(text.ToString())));
            }
        }
        return new PageIndex(page, title, SettingsSearchSupport.Fold(title), blocks.Where(b => b.Items.Count > 0).ToList());
    }

    /// <summary>Every piece of text a row shows: titles, descriptions, button labels.</summary>
    private static void Collect(object? node, StringBuilder text)
    {
        switch (node)
        {
            case null: return;
            case string s: text.Append(s).Append(' '); return;
            case TextBlock t: text.Append(t.Text).Append(' '); return;
            case CardControl card: Collect(card.Header, text); Collect(card.Content, text); return;
            case ContentControl control: Collect(control.Content, text); return;
            case Panel panel: foreach (UIElement child in panel.Children) Collect(child, text); return;
            case Decorator decorator: Collect(decorator.Child, text); return;
        }
    }
}
