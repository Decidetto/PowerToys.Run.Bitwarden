using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Wox.Plugin.Logger;

namespace PowerToys.Run.Bitwarden.Settings;

internal static class PowerToysRunSettingsCleaner
{
    private const int MaxAttempts = 3;

    public static void ClearPlaintextApiCredentials()
    {
        try
        {
            ClearPlaintextApiCredentials(GetPowerToysRunSettingsPath(), Main.PluginID);
        }
        catch (Exception ex)
        {
            Log.Exception("ClearPlaintextApiCredentials failed", ex, typeof(PowerToysRunSettingsCleaner));
        }
    }

    internal static bool ClearPlaintextApiCredentials(string settingsPath, string pluginId)
    {
        if (!File.Exists(settingsPath)) return false;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                var root = JsonNode.Parse(File.ReadAllText(settingsPath)) as JsonObject;
                var options = FindPluginAdditionalOptions(root, pluginId);
                if (options is null) return false;

                var changed = false;
                foreach (var option in options.OfType<JsonObject>())
                {
                    var key = option["Key"]?.GetValue<string>();
                    if (key is not (nameof(PluginSettings.ClientId) or nameof(PluginSettings.ClientSecret))) continue;
                    if (option["TextValue"]?.GetValue<string>() is not { Length: > 0 }) continue;

                    option["TextValue"] = string.Empty;
                    changed = true;
                }

                if (!changed) return false;

                File.WriteAllText(settingsPath, root!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                return true;
            }
            catch (IOException) when (attempt < MaxAttempts)
            {
                Thread.Sleep(250);
            }
        }

        return false;
    }

    internal static JsonArray? FindPluginAdditionalOptions(JsonObject? root, string pluginId)
    {
        var plugins = root?["plugins"] as JsonArray ?? root?["Plugins"] as JsonArray;
        if (plugins is null) return null;

        foreach (var pluginNode in plugins.OfType<JsonObject>())
        {
            var id = pluginNode["Id"]?.GetValue<string>() ?? pluginNode["id"]?.GetValue<string>();
            if (!string.Equals(id, pluginId, StringComparison.OrdinalIgnoreCase)) continue;
            return pluginNode["AdditionalOptions"] as JsonArray ?? pluginNode["additional_options"] as JsonArray;
        }

        return null;
    }

    internal static string GetPowerToysRunSettingsPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "PowerToys", "PowerToys Run", "settings.json");
    }
}
