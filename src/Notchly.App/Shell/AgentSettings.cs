// SPDX-License-Identifier: GPL-3.0-or-later
// The AI agents section of the island's settings: the Windows counterpart of upstream
// UI/Settings/NotchAgentsSettings.swift. A row per agent (found on this PC or not), the page's cards in the person's
// order with up and down buttons, and the choices whose values are numbers (finish minimum, threshold, budget).
// Codex accounts, Claude profiles and the strip sample are not ported.
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Notchly.App.Island.Agents;
using Notchly.App.Platform.Agents;
using Notchly.Core.Agents;
using Notchly.Core.Settings;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Notchly.App.Shell;

internal static class AgentSettings
{
    // Upstream NotchAgentStrings.enUS, with "this PC" for "this Mac" and Cursor added.
    public const string Description = "Reads the usage Claude Code, Codex, OpenCode and GitHub Copilot record on this PC, and Cursor's plan " +
        "usage from cursor.com. Prompts, replies and files are never kept.";
    public const string Privacy = "Only counters, model names, times and folder names are kept; prompts and replies are never stored. " +
        "Nothing is sent, except running each agent's own command for its limits and asking cursor.com for Cursor's.";
    public const string CursorNote = "Uses Cursor's own sign-in to read your plan's usage from cursor.com; the sign-in stays on this PC.";
    public const string PriceHint = "Downloads the public price list from GitHub once a day, so new models get an API value without an " +
        "app update. Nothing about your usage is sent.";
    public const string ValueNote = "API value is what the same work would cost at API list prices. Plans charge a fixed price instead.";

    /// <summary>A row laid out like every other, with <paramref name="icon"/> (the agent's mark) before its title.</summary>
    private static CardControl Row(string title, string? description, FrameworkElement icon, object content)
    {
        icon.Margin = new Thickness(0, 0, 14, 0);
        icon.VerticalAlignment = VerticalAlignment.Center;
        return new CardControl
        {
            Header = new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, Header(title, description) } },
            Content = content,
            Margin = new Thickness(0, 0, 0, 4),
        };
    }

    private static FrameworkElement Header(string title, string? description)
    {
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new TextBlock { Text = title, FontSize = 14 });
        if (!string.IsNullOrEmpty(description))
            stack.Children.Add(new TextBlock { Text = description, FontSize = 12, TextWrapping = TextWrapping.Wrap, Opacity = 0.7, Margin = new Thickness(0, 2, 0, 0), MaxWidth = 520 });
        return stack;
    }

    private static string Name(AgentProvider provider) => provider == AgentProvider.Claude ? "Claude Code" : provider.DisplayName();

    /// <summary>Whether the agent left anything on this PC to read, checked when the page opens.</summary>
    private static bool Found(AgentProvider provider)
    {
        try
        {
            return provider == AgentProvider.Cursor
                ? File.Exists(CursorUsageReader.StateDatabase)
                : AgentPaths.Roots(new HashSet<AgentProvider> { provider }).Any(r => r.Exists);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>A switch per agent, each laid out like every other row with the agent's mark for an icon.</summary>
    public static IEnumerable<UIElement> ProviderRows(SettingsStore settings)
    {
        foreach (var provider in AgentProviders.All)
        {
            var key = AgentsService.KeyFor(provider);
            var toggle = new ToggleSwitch { IsChecked = settings.GetBool(key) };
            toggle.Click += (_, _) => settings.Set(key, toggle.IsChecked == true);
            var found = Found(provider) ? "Found on this PC" : "Not found on this PC";
            var description = provider == AgentProvider.Cursor ? found + ". " + CursorNote : found;
            yield return Row(Name(provider), description, AgentLook.Mark(provider, null, 22), toggle);
        }
    }

    /// <summary>A choice among numbers kept as a double (seconds, percent, dollars).</summary>
    public static ComboBox NumberChoice(SettingsStore settings, string key, IReadOnlyList<double> values, double fallback, Func<double, string> label)
    {
        var combo = new ComboBox { MinWidth = 160 };
        foreach (var value in values) combo.Items.Add(new ComboBoxItem { Content = label(value), Tag = value });
        var current = settings.GetDouble(key, fallback);
        combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => Math.Abs((double)i.Tag - current) < 0.001)
                             ?? combo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => Math.Abs((double)i.Tag - fallback) < 0.001);
        combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is ComboBoxItem { Tag: double value }) settings.Set(key, value); };
        return combo;
    }

    public static string FinishLabel(double seconds) =>
        seconds == 0 ? "Any length" : seconds < 60 ? $"{seconds:0} seconds" : seconds == 60 ? "1 minute" : $"{seconds / 60:0} minutes";

    public static string ThresholdLabel(double percent) => AgentFormat.Percent(percent / 100) + " used";

    public static string BudgetLabel(double dollars) => dollars == 0 ? "Off" : AgentFormat.Cost(dollars);

    private static string CardTitle(NotchAgentCard card) => card switch
    {
        NotchAgentCard.Limits => "Limits", NotchAgentCard.Spend => "Spending", NotchAgentCard.Live => "Now",
        NotchAgentCard.Trend => "Trend", NotchAgentCard.Models => "Models", NotchAgentCard.Projects => "Projects",
        NotchAgentCard.Activity => "Activity", _ => "Resets",
    };

    private static string CardGlyph(NotchAgentCard card) => card switch
    {
        NotchAgentCard.Limits => "\uE9D9", NotchAgentCard.Spend => "\uE8C7", NotchAgentCard.Live => "\uE945",
        NotchAgentCard.Trend => "\uE9D2", NotchAgentCard.Models => "\uE8F1", NotchAgentCard.Projects => "\uE8B7",
        _ => "\uE787",
    };

    /// <summary>The page's cards in order, each with up and down buttons and a switch to show it (upstream drags them).
    /// Resets, which hold Codex's banked resets that Windows does not read, is left out.</summary>
    public static FrameworkElement CardList(SettingsStore settings)
    {
        var list = new StackPanel();
        void Build()
        {
            list.Children.Clear();
            var order = NotchAgentSupport.OrderedCards(settings.GetString(DefaultsKey.NotchAgentsCardOrder)).Where(c => c != NotchAgentCard.Resets).ToList();
            var hidden = NotchAgentSupport.HiddenCards(settings.GetString(DefaultsKey.NotchAgentsHiddenCards));
            for (var index = 0; index < order.Count; index++)
            {
                var card = order[index];
                var position = index;
                var row = new Grid { Margin = new Thickness(0, 0, 0, 2) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(new FontIcon { Glyph = CardGlyph(card), FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
                var title = new TextBlock { Text = CardTitle(card) + (card.FullWidth() ? "  (full width)" : ""), FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(title, 1);
                row.Children.Add(title);
                var controls = new StackPanel { Orientation = Orientation.Horizontal };
                var up = new Button { Content = Glyph("\uE70E"), IsEnabled = position > 0, ToolTip = "Move up", Margin = new Thickness(0, 0, 4, 0) };
                var down = new Button { Content = Glyph("\uE70D"), IsEnabled = position < order.Count - 1, ToolTip = "Move down", Margin = new Thickness(0, 0, 12, 0) };
                up.Click += (_, _) => Move(settings, order, position, -1);
                down.Click += (_, _) => Move(settings, order, position, 1);
                var shown = new ToggleSwitch { IsChecked = !hidden.Contains(card), ToolTip = "Show on the page" };
                shown.Click += (_, _) =>
                {
                    var set = NotchAgentSupport.HiddenCards(settings.GetString(DefaultsKey.NotchAgentsHiddenCards)).Select(c => c.RawValue()).ToHashSet(StringComparer.Ordinal);
                    if (shown.IsChecked == true) set.Remove(card.RawValue()); else set.Add(card.RawValue());
                    settings.Set(DefaultsKey.NotchAgentsHiddenCards, string.Join(",", set.Order(StringComparer.Ordinal)));
                };
                controls.Children.Add(up);
                controls.Children.Add(down);
                controls.Children.Add(shown);
                Grid.SetColumn(controls, 2);
                row.Children.Add(controls);
                list.Children.Add(row);
            }
        }
        Build();
        Action<string> changed = key =>
        {
            if (key is DefaultsKey.NotchAgentsCardOrder or DefaultsKey.NotchAgentsHiddenCards) list.Dispatcher.BeginInvoke(Build);
        };
        settings.Changed += changed;
        list.Unloaded += (_, _) => settings.Changed -= changed;
        list.Loaded += (_, _) => { settings.Changed -= changed; settings.Changed += changed; };
        var header = new StackPanel();
        header.Children.Add(Header("Cards", "The page's cards, in order. Charts take the full width of the island."));
        list.Margin = new Thickness(0, 10, 0, 0);
        header.Children.Add(list);
        return new CardControl
        {
            Header = header,
            Icon = new FontIcon { Glyph = "\uF0E2", FontFamily = new FontFamily("Segoe Fluent Icons") },
            Margin = new Thickness(0, 0, 0, 4),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
    }

    private static FontIcon Glyph(string glyph) => new() { Glyph = glyph, FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 12 };

    private static void Move(SettingsStore settings, List<NotchAgentCard> order, int index, int offset)
    {
        var target = index + offset;
        if (target < 0 || target >= order.Count) return;
        (order[index], order[target]) = (order[target], order[index]);
        settings.Set(DefaultsKey.NotchAgentsCardOrder, string.Join(",", order.Select(c => c.RawValue())));
    }
}
