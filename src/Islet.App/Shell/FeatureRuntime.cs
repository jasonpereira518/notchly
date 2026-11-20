// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/App/FeatureRuntime.swift: one lazy binding per feature.
// A service exists only while its feature is installed; Sync() is idempotent and starts or stops
// the service from its own settings, like upstream's syncWithPreferences().
using Islet.Core.Features;
using Islet.Core.Settings;
using Serilog;

namespace Islet.App.Shell;

public interface IFeatureService : IDisposable
{
    void Sync();
}

public sealed class FeatureRuntime(SettingsStore settings) : IDisposable
{
    private readonly Dictionary<AppFeature, Func<IFeatureService>> _bindings = [];
    private readonly Dictionary<AppFeature, IFeatureService> _live = [];

    public void Bind(AppFeature feature, Func<IFeatureService> factory) => _bindings[feature] = factory;

    public T? Get<T>(AppFeature feature) where T : class, IFeatureService =>
        _live.TryGetValue(feature, out var service) ? service as T : null;

    public void SyncAll()
    {
        foreach (var (feature, factory) in _bindings)
        {
            var available = settings.IsAvailable(feature);
            if (available)
            {
                if (!_live.TryGetValue(feature, out var service))
                {
                    service = factory();
                    _live[feature] = service;
                    Log.Information("Feature {Feature} loaded", FeatureCatalog.Id(feature));
                }
                try { service.Sync(); }
                catch (Exception e) { Log.Error(e, "Feature {Feature} failed to sync", FeatureCatalog.Id(feature)); }
            }
            else if (_live.Remove(feature, out var service))
            {
                service.Dispose();
                Log.Information("Feature {Feature} unloaded", FeatureCatalog.Id(feature));
            }
        }
    }

    public void Dispose()
    {
        foreach (var service in _live.Values) service.Dispose();
        _live.Clear();
    }
}
