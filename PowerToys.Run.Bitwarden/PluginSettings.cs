using Microsoft.PowerToys.Settings.UI.Library;

namespace PowerToys.Run.Bitwarden;

internal sealed record PluginSettings(
    string? ServerUrl,
    string ClientId,
    string ClientSecret,
    int AutoLockMinutes,
    string? BwExePathOverride,
    bool UsageBoostEnabled)
{
    public static readonly PluginSettings Empty = new(null, "", "", 30, null, UsageBoostEnabled: true);

    public IEnumerable<PluginAdditionalOption> BuildAdditionalOptions() => new[]
    {
        new PluginAdditionalOption
        {
            Key = nameof(ServerUrl),
            DisplayLabel = "Server URL",
            DisplayDescription = "Leave empty for bitwarden.com. Set to your Vaultwarden / self-hosted URL otherwise.",
            PluginOptionType = PluginAdditionalOption.AdditionalOptionType.Textbox,
            TextValue = ServerUrl ?? string.Empty,
        },
        new PluginAdditionalOption
        {
            Key = nameof(ClientId),
            DisplayLabel = "API client_id",
            DisplayDescription = "From vault.bitwarden.com → Account Settings → Security → Keys (the user.<...> value).",
            PluginOptionType = PluginAdditionalOption.AdditionalOptionType.Textbox,
            TextValue = ClientId,
        },
        new PluginAdditionalOption
        {
            Key = nameof(ClientSecret),
            DisplayLabel = "API client_secret",
            DisplayDescription = "The matching client_secret value.",
            PluginOptionType = PluginAdditionalOption.AdditionalOptionType.Textbox,
            TextValue = ClientSecret,
        },
        new PluginAdditionalOption
        {
            Key = nameof(AutoLockMinutes),
            DisplayLabel = "Auto-lock after (minutes)",
            DisplayDescription = "Idle minutes before the vault re-locks and prompts for the master password again. 0 = stay unlocked until PowerToys closes.",
            PluginOptionType = PluginAdditionalOption.AdditionalOptionType.Numberbox,
            NumberValue = AutoLockMinutes,
            // Without explicit bounds PT's Numberbox UI defaults to 0 and won't accept input.
            NumberBoxMin = 0,
            NumberBoxMax = 1440,
            NumberBoxSmallChange = 1,
            NumberBoxLargeChange = 5,
        },
        new PluginAdditionalOption
        {
            Key = nameof(UsageBoostEnabled),
            DisplayLabel = "Rank results by pick frequency",
            DisplayDescription = "Items that are selected more often float to the top.",
            PluginOptionType = PluginAdditionalOption.AdditionalOptionType.Checkbox,
            Value = UsageBoostEnabled,
        },
        new PluginAdditionalOption
        {
            Key = nameof(BwExePathOverride),
            DisplayLabel = "Bitwarden CLI path (advanced)",
            DisplayDescription = "Optional override. Leave empty to resolve 'bw' / 'bw.exe' from PATH.",
            PluginOptionType = PluginAdditionalOption.AdditionalOptionType.Textbox,
            TextValue = BwExePathOverride ?? string.Empty,
        },
    };

    public static PluginSettings From(IEnumerable<PluginAdditionalOption>? raw)
    {
        if (raw is null) return Empty;
        var map = raw.Where(o => !string.IsNullOrEmpty(o.Key)).ToDictionary(o => o.Key);
        return new PluginSettings(
            ServerUrl:           StringOrNull(map, nameof(ServerUrl)),
            ClientId:            StringOrEmpty(map, nameof(ClientId)),
            ClientSecret:        StringOrEmpty(map, nameof(ClientSecret)),
            AutoLockMinutes:     NumberOr(map, nameof(AutoLockMinutes), 30),
            BwExePathOverride:   StringOrNull(map, nameof(BwExePathOverride)),
            UsageBoostEnabled:   BoolOr(map, nameof(UsageBoostEnabled), dflt: true));
    }

    public readonly record struct Difference(bool AuthChanged, bool AutoLockChanged, bool UsageBoostChanged);

    public static Difference Diff(PluginSettings prev, PluginSettings next)
    {
        // Record value-equality fast path: PT echoes UpdateSettings on ChangeQuery and
        // other internal events, so we must not flag any change when nothing changed.
        if (prev == next) return default;
        var authChanged =
            prev.ServerUrl != next.ServerUrl ||
            prev.ClientId != next.ClientId ||
            prev.ClientSecret != next.ClientSecret ||
            prev.BwExePathOverride != next.BwExePathOverride;
        var autoLockChanged = prev.AutoLockMinutes != next.AutoLockMinutes;
        var usageBoostChanged = prev.UsageBoostEnabled != next.UsageBoostEnabled;
        return new Difference(authChanged, autoLockChanged, usageBoostChanged);
    }

    private static string? StringOrNull(Dictionary<string, PluginAdditionalOption> m, string k)
        => m.TryGetValue(k, out var v) && !string.IsNullOrEmpty(v.TextValue) ? v.TextValue : null;

    private static string StringOrEmpty(Dictionary<string, PluginAdditionalOption> m, string k)
        => m.TryGetValue(k, out var v) ? v.TextValue ?? string.Empty : string.Empty;

    private static int NumberOr(Dictionary<string, PluginAdditionalOption> m, string k, int dflt)
        => m.TryGetValue(k, out var v) ? (int)v.NumberValue : dflt;

    private static bool BoolOr(Dictionary<string, PluginAdditionalOption> m, string k, bool dflt)
        => m.TryGetValue(k, out var v) ? v.Value : dflt;
}
