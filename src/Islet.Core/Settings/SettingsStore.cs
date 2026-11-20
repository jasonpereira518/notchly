// SPDX-License-Identifier: GPL-3.0-or-later
// Windows stand-in for macOS UserDefaults: a flat key/value store with registered defaults,
// change notifications and a debounced, atomic JSON file.
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Islet.Core.Settings;

public sealed class SettingsStore : IDisposable
{
    public const int SchemaVersion = 1;

    private readonly object _gate = new();
    private readonly Dictionary<string, JsonNode?> _values = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonNode?> _defaults = new(StringComparer.Ordinal);
    private readonly string? _path;
    private readonly Timer? _saveTimer;
    private bool _dirty;

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>Raised after a value changes, with the key that changed. May fire on any thread.</summary>
    public event Action<string>? Changed;

    /// <param name="path">File to load from and save to; null keeps everything in memory (tests).</param>
    public SettingsStore(string? path)
    {
        _path = path;
        if (path is not null)
        {
            _saveTimer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
            Load();
        }
    }

    public static SettingsStore InMemory() => new(null);

    // MARK: Defaults

    public void RegisterDefaults(IEnumerable<KeyValuePair<string, object?>> defaults)
    {
        lock (_gate)
        {
            foreach (var (key, value) in defaults) _defaults[key] = ToNode(value);
        }
    }

    public IReadOnlyCollection<string> RegisteredKeys
    {
        get { lock (_gate) return _defaults.Keys.ToArray(); }
    }

    public bool IsRegistered(string key)
    {
        lock (_gate) return _defaults.ContainsKey(key);
    }

    /// <summary>The registered default as JSON, or null.</summary>
    public JsonNode? DefaultNode(string key)
    {
        lock (_gate) return _defaults.TryGetValue(key, out var node) ? node?.DeepClone() : null;
    }

    // MARK: Reading

    /// <summary>The saved value, else the registered default, as JSON; null when neither exists.</summary>
    public JsonNode? Node(string key)
    {
        lock (_gate)
        {
            if (_values.TryGetValue(key, out var node) || _defaults.TryGetValue(key, out node)) return node?.DeepClone();
            return null;
        }
    }

    /// <summary>Whether the user saved a value, as opposed to reading the registered default.</summary>
    public bool IsSaved(string key)
    {
        lock (_gate) return _values.ContainsKey(key);
    }

    public T Get<T>(string key, T fallback = default!)
    {
        JsonNode? node;
        lock (_gate)
        {
            if (!_values.TryGetValue(key, out node) && !_defaults.TryGetValue(key, out node)) return fallback;
        }
        return TryConvert(node, out T value) ? value : fallback;
    }

    public bool GetBool(string key) => Get(key, false);
    public int GetInt(string key, int fallback = 0) => Get(key, fallback);
    public double GetDouble(string key, double fallback = 0) => Get(key, fallback);
    public string GetString(string key, string fallback = "") => Get(key, fallback) ?? fallback;

    /// <summary>Upstream stores lists as comma-joined strings ("music,timer").</summary>
    public IReadOnlyList<string> GetList(string key) =>
        GetString(key).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // MARK: Writing

    public void Set<T>(string key, T value)
    {
        var node = ToNode(value);
        lock (_gate)
        {
            if (_values.TryGetValue(key, out var old) && JsonNode.DeepEquals(old, node)) return;
            _values[key] = node;
            MarkDirty();
        }
        Changed?.Invoke(key);
    }

    public void SetList(string key, IEnumerable<string> items) => Set(key, string.Join(",", items));

    public void Remove(string key)
    {
        bool removed;
        lock (_gate)
        {
            removed = _values.Remove(key);
            if (removed) MarkDirty();
        }
        if (removed) Changed?.Invoke(key);
    }

    /// <summary>Saved values only, for export.</summary>
    public IReadOnlyDictionary<string, JsonNode?> SavedValues()
    {
        lock (_gate) return _values.ToDictionary(kv => kv.Key, kv => kv.Value?.DeepClone(), StringComparer.Ordinal);
    }

    /// <summary>Writes several values and raises one Changed per key afterwards.</summary>
    public void SetMany(IEnumerable<KeyValuePair<string, JsonNode?>> values)
    {
        var changed = new List<string>();
        lock (_gate)
        {
            foreach (var (key, value) in values)
            {
                if (_values.TryGetValue(key, out var old) && JsonNode.DeepEquals(old, value)) continue;
                _values[key] = value?.DeepClone();
                changed.Add(key);
            }
            if (changed.Count > 0) MarkDirty();
        }
        foreach (var key in changed) Changed?.Invoke(key);
    }

    // MARK: Persistence

    private void MarkDirty()
    {
        _dirty = true;
        _saveTimer?.Change(400, Timeout.Infinite);
    }

    /// <summary>Writes pending changes now: temp file, then an atomic replace.</summary>
    public void Flush()
    {
        if (_path is null) return;
        string json;
        lock (_gate)
        {
            if (!_dirty) return;
            var settings = new JsonObject();
            foreach (var (key, value) in _values.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                settings[key] = value?.DeepClone();
            var root = new JsonObject { ["schemaVersion"] = SchemaVersion, ["settings"] = settings };
            json = root.ToJsonString(WriteOptions);
            _dirty = false;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, _path, overwrite: true);
    }

    private void Load()
    {
        if (_path is null || !File.Exists(_path)) return;
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(_path)) as JsonObject;
            if (root?["settings"] is not JsonObject settings) return;
            lock (_gate)
            {
                foreach (var (key, value) in settings) _values[key] = value?.DeepClone();
            }
        }
        catch (JsonException)
        {
            // A damaged file is kept aside rather than overwritten, so nothing is lost silently.
            File.Copy(_path, _path + ".damaged", overwrite: true);
        }
    }

    public void Dispose()
    {
        Flush();
        _saveTimer?.Dispose();
    }

    // MARK: Conversion

    internal static JsonNode? ToNode<T>(T value) => value switch
    {
        null => null,
        JsonNode node => node.DeepClone(),
        _ => JsonSerializer.SerializeToNode(value, value.GetType()),
    };

    internal static bool TryConvert<T>(JsonNode? node, out T value)
    {
        value = default!;
        if (node is null) return default(T) is null;
        try
        {
            var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
            if (node is JsonValue scalar)
            {
                if (!scalar.TryGetValue<JsonElement>(out var element))
                {
                    // A value created in memory rather than parsed: re-read it as JSON.
                    element = JsonDocument.Parse(scalar.ToJsonString()).RootElement.Clone();
                }
                object? converted = element.ValueKind switch
                {
                    JsonValueKind.True or JsonValueKind.False when target == typeof(bool) => element.GetBoolean(),
                    JsonValueKind.Number when target == typeof(int) => (int)Math.Round(element.GetDouble()),
                    JsonValueKind.Number when target == typeof(long) => (long)Math.Round(element.GetDouble()),
                    JsonValueKind.Number when target == typeof(double) => element.GetDouble(),
                    JsonValueKind.String when target == typeof(string) => element.GetString(),
                    _ => element.Deserialize(target),
                };
                if (converted is T typed) { value = typed; return true; }
                return false;
            }
            var result = node.Deserialize<T>();
            if (result is null) return false;
            value = result;
            return true;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or NotSupportedException)
        {
            return false;
        }
    }
}
