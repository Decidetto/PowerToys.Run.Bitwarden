namespace PowerToys.Run.Bitwarden.Vault;

// JSON DTOs for bw serve responses. BitwardenClient.JsonOptions uses
// JsonNamingPolicy.CamelCase so PascalCase property names map cleanly to the
// camelCase JSON keys that bw emits, no per-property attributes needed.

internal sealed record Envelope<T>(bool Success, T? Data, string? Message);

internal sealed record StatusData(StatusTemplate? Template);

internal sealed record StatusTemplate(string? Status, string? ServerUrl, string? UserEmail, string? LastSync);

internal sealed record ListData<T>(string? Object, T[]? Data);

internal sealed record ItemSummary(
    string Id,
    string Name,
    int Type,
    string? DeletedDate,
    LoginPart? Login);

internal sealed record LoginPart(string? Username, string? Password, LoginUri[]? Uris);

internal sealed record LoginUri(string? Uri, int? Match);

internal sealed record ItemDetail(string Id, string Name, int Type, LoginPart? Login);
