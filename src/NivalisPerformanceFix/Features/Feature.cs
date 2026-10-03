using System;
using BepInEx.Configuration;

namespace NivalisPerformanceFix.Features;

/// <summary>
/// One optimization: its config section, installation (which may fail after a game update: the feature then
/// stays off and the startup summary says why) and an optional per-frame tick.
/// </summary>
internal abstract class Feature
{
    public abstract string Name { get; }
    protected abstract string Section { get; }
    protected abstract string Description { get; }

    public ConfigEntry<bool> Enabled { get; private set; }
    public bool Installed { get; private set; }
    public string Problem { get; private set; }

    /// <summary>Installed, switched on in its section and by the master switch.</summary>
    public bool Active => Installed && Enabled.Value && Plugin.MasterEnabled.Value;

    /// <summary>True for features that must wait until every plugin is loaded (see <see cref="Plugin"/>).</summary>
    public virtual bool InstallLate => false;

    public void Bind(ConfigFile config)
    {
        Enabled = config.Bind(Section, "Enabled", true, Description);
        BindSettings(config);
    }

    protected virtual void BindSettings(ConfigFile config) { }

    /// <summary>Returns null on success, else a short reason shown in the log.</summary>
    protected abstract string TryInstall();

    public void Install()
    {
        try
        {
            Problem = TryInstall();
        }
        catch (Exception e)
        {
            Problem = e.GetType().Name + ": " + e.Message;
            Plugin.Log.LogDebug(e);
        }
        Installed = Problem == null;
    }

    /// <summary>Turns the feature off until the game is restarted, without touching the config file.</summary>
    public void DisableForSession(string reason)
    {
        Problem = reason;
        Installed = false;
    }

    /// <summary>Called once per frame from the plugin behaviour (main thread).</summary>
    public virtual void Tick() { }

    public string StatusLine =>
        !Installed ? $"{Name}: UNAVAILABLE ({Problem})"
        : !Enabled.Value ? $"{Name}: off (config)"
        : $"{Name}: on";
}
