// SPDX-License-Identifier: GPL-3.0-or-later
// Lets XAML bind straight to a settings key: {Binding [notchEnabled]}. Reads give CLR values
// (bool, double, string); writes go through SettingsStore, which notifies everyone else.
using System.ComponentModel;
using System.Text.Json;
using System.Windows.Threading;
using Islet.Core.Settings;

namespace Islet.App.Shell;

public sealed class SettingsProxy : INotifyPropertyChanged
{
    private readonly SettingsStore _store;

    public event PropertyChangedEventHandler? PropertyChanged;

    public SettingsProxy(SettingsStore store, Dispatcher dispatcher)
    {
        _store = store;
        store.Changed += key => dispatcher.BeginInvoke(() =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]")));
    }

    public object? this[string key]
    {
        get
        {
            var node = _store.Node(key);
            if (node is null) return null;
            return node.GetValueKind() switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => node.GetValue<double>(),
                JsonValueKind.String => node.GetValue<string>(),
                _ => node.ToJsonString(),
            };
        }
        set
        {
            switch (value)
            {
                case bool b: _store.Set(key, b); break;
                case double d: _store.Set(key, d); break;
                case int i: _store.Set(key, i); break;
                case string s: _store.Set(key, s); break;
                case null: _store.Remove(key); break;
                default: _store.Set(key, value.ToString() ?? ""); break;
            }
        }
    }
}
