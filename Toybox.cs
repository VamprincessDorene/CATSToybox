using System;
using System.IO;
using Newtonsoft.Json;

namespace CATSToybox;

/// <summary>What the toys use to reach the shared Toybox window and settings.</summary>
public static class Toybox
{
    internal static Plugin? Host { get; set; }

    /// <summary>Open the Toybox window on a tab.</summary>
    public static void Open(string tab)
    {
        if (Host?.Window is { } w) w.Show(tab);
    }

    /// <summary>Open on a tab, or close the window if it's already showing that tab.</summary>
    public static void Toggle(string tab)
    {
        if (Host?.Window is not { } w) return;
        if (w.IsOpen && w.CurrentTab == tab) w.IsOpen = false;
        else w.Show(tab);
    }
}

/// <summary>
/// Each toy keeps its own settings file in pluginConfigs\CATSToybox\. The first time a toy runs
/// inside the Toybox, its settings are copied over from the old standalone plugin, if there was one.
/// </summary>
public static class ModuleConfig
{
    private static readonly JsonSerializerSettings Settings = new()
    {
        Formatting = Formatting.Indented,
        MetadataPropertyHandling = MetadataPropertyHandling.Ignore,   // old Dalamud configs carry "$type"
        ObjectCreationHandling = ObjectCreationHandling.Replace,     // don't append to default lists
        TypeNameHandling = TypeNameHandling.None,
    };

    private static DirectoryInfo Dir => Plugin.PluginInterface.ConfigDirectory;

    public static T Load<T>(string name, string? legacyInternalName = null) where T : new()
    {
        try
        {
            var path = Path.Combine(Dir.FullName, name + ".json");
            if (File.Exists(path))
                return JsonConvert.DeserializeObject<T>(File.ReadAllText(path), Settings) ?? new T();

            if (legacyInternalName != null && Dir.Parent is { } parent)
            {
                var legacy = Path.Combine(parent.FullName, legacyInternalName + ".json");
                if (File.Exists(legacy))
                {
                    var cfg = JsonConvert.DeserializeObject<T>(File.ReadAllText(legacy), Settings) ?? new T();
                    Plugin.Log.Information($"Copied {legacyInternalName} settings into the CATS Toybox.");
                    Save(name, cfg);
                    return cfg;
                }
            }
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, $"Couldn't read {name} settings; using defaults.");
        }
        return new T();
    }

    public static void Save<T>(string name, T config)
    {
        try
        {
            Dir.Create();
            var path = Path.Combine(Dir.FullName, name + ".json");
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(config, Settings));
            File.Move(tmp, path, true);
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, $"Couldn't save {name} settings.");
        }
    }
}
