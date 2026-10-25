# Bitwarden plugin for PowerToys Run

Search your Bitwarden (or compatible) vault from PowerToys Run and copy passwords to the clipboard, without ever opening the app.

Type the action keyword (default: `bw`) and start typing to find your desired website/account. Pick an item to copy its password to your clipboard. Auto-clears from the clipboard after 30 seconds.

## Features

- **Fast local search**: type `bw <query>` to fuzzy-match by item name, username, or URL. Results filter as you type.
- **Enter copies the password**, hidden the entire time, never displayed on screen.
- **Sub-actions** on each result:
  - `Ctrl+C` - copy the item's username
  - `Ctrl+O` - open the item's URL in your default browser
- **Commands**:
  - `bw :sync` - manually sync with the Bitwarden server
  - `bw :lock` - lock the vault immediately
- **Unlock** vault via a discreet dialog popup that obscures the characters of your master password.
- **Auto-lock** after a configurable idle timeout (default 30 minutes; set to `0` to stay unlocked until PowerToys closes).
  - Also locks vault automatically when Windows locks the session or you disconnect from an RDP session.
- **Pick-frequency ranking** (optional): items you copy often float to the top of results. Toggle in settings.
- **Bitwarden US/EU / Self-hosted / Vaultwarden** all supported. Type your server URL in the settings.

## Security

- **Master password is never stored.** Entered on demand into a `PasswordBox` (no plaintext in `settings.json`, no DPAPI sidecar), used once, then zeroed.
- **API credentials** (client ID + secret) authenticate to the server but do not decrypt anything; the vault stays locked until you enter the master password.
- **API credentials are migrated to encrypted local storage** after you enter both values in settings. The plugin stores them in a DPAPI-protected sidecar file for your Windows user and clears the plaintext settings fields.
- **Copied passwords auto-clear from the clipboard after 30 seconds.**
- **Passwords are excluded from Win+V clipboard history and cloud-clipboard sync**
- **Vault re-locks** on Windows session lock, console disconnect, or RDP disconnect.

## Requirements

- Windows 10/11
- [PowerToys](https://github.com/microsoft/PowerToys) with PowerToys Run enabled
- [Bitwarden CLI](https://bitwarden.com/help/cli/) (`bw.exe` or npm's `bw.cmd` on `PATH`, or set a custom path in plugin settings). Must be installed separately
- API credentials for your vault

## Installation

1. Download `Bitwarden-v<version>-x64.zip` (or `-arm64.zip` for ARM64 Windows) from the [Releases](../../releases) page.
2. Extract the `Bitwarden\` folder into:
   ```
   %LOCALAPPDATA%\Microsoft\PowerToys\PowerToys Run\Plugins\
   ```
3. Restart PowerToys.
4. Open PowerToys Settings → PowerToys Run → Bitwarden, and fill in the settings below. Once both API credential fields are entered, they are saved securely when settings are applied or the plugin is next queried.

## Setting up your API key

Bitwarden lets you authenticate the CLI without your master password by using a personal API key:

1. Sign in to the web vault at [vault.bitwarden.com](https://vault.bitwarden.com) (or your self-hosted address).
2. Go to **Account Settings → Security → Keys**.
3. Click **View API key**, confirm with your master password.
4. Copy the `client_id` and `client_secret` values.
5. Paste them into the plugin's settings in PowerToys Run.

The master password still controls *unlocking* the vault. You'll be prompted for it the first time you search after a restart or auto-lock.

## Settings

| Setting | Default | Notes |
|---|---|---|
| **Server URL** | empty | Leave empty for `bitwarden.com`. Set to your Vaultwarden / self-hosted URL otherwise. Accepts bare hosts like `bitwarden.eu`. |
| **API client_id** | - | From web vault → Account Settings → Security → Keys. The plugin migrates this to encrypted local storage once both credential fields are present. |
| **API client_secret** | - | The matching secret. The plugin migrates this to encrypted local storage once both credential fields are present. |
| **Auto-lock after (minutes)** | 30 | `0` = stay unlocked until PowerToys closes. Range 0-1440. |
| **Rank results by pick frequency** | on | Items you've copied more often float to the top. |
| **Bitwarden CLI path (advanced)** | empty | Optional override. Leave empty to resolve `bw.exe` / `bw.cmd` from `PATH`. |

## CLI login and troubleshooting

The plugin uses its own CLI profile under
`%LOCALAPPDATA%\Microsoft\PowerToys\PowerToys Run\Settings\Plugins\PowerToys.Run.Bitwarden\cli`.
Your normal terminal CLI profile and login are not reused or modified. An inherited
`BITWARDENCLI_APPDATA_DIR` or `BW_SESSION` does not override the plugin settings.

On startup and whenever authentication settings change, the plugin replaces its own
CLI login, applies the configured server, and logs in using the API credentials
entered in the plugin (including credentials already saved in its encrypted store).
Clearing the server selects Bitwarden US explicitly. Enter both fields when replacing
an API credential pair; a partial replacement will not reuse the previous pair.
Startup and authentication reconfiguration require network access.

Unlock errors identify the failed operation and include an HTTP status or CLI exit
code when available. Recognized CLI explanations distinguish an incorrect password,
missing login, decryption failure, rejected API credentials, and connection or TLS
problems. Unknown response text is withheld because it may contain secrets. Raw CLI
stdout/stderr and response bodies are not written to plugin logs.

Unlock, sync, and item loading run in order. If a later step fails, the plugin re-locks
the vault; if re-locking fails, it stops its daemon. A sync or item-loading failure
therefore no longer appears as an incorrect-password error.

## Usage

### First run

Open PowerToys Run, type `bw`. You'll see the plugin starting up briefly while it warms up the Bitwarden CLI in the background. Once ready, you'll be prompted for your master password the first time you actually search.

### Searching

```
bw github       → items matching "github" by name or username
bw              → top results by pick frequency (most-used items)
```

Press Enter on a result → password copied, launcher closes.

### Commands

```
bw :sync        → force-refresh the local cache from the server
bw :lock        → lock the vault immediately
```

### Context actions

Hover or arrow-key to a result, then:

- `Ctrl+C` → copy the username instead of the password
- `Ctrl+O` → open the item's first URL in your default browser

## Scope

Currently supported:

- ✅ Login items (passwords + usernames + URLs)
- ❌ TOTP codes
- ❌ Secure notes, identities, payment cards
- ❌ Item creation / editing / deletion
- ❌ Biometric / Windows Hello unlock

## Acknowledgements

- The [Bitwarden CLI](https://bitwarden.com/help/cli/) does all the heavy lifting. This plugin is a thin search-and-clipboard layer over it.
- Built on the [Community.PowerToys.Run.Plugin.Dependencies](https://www.nuget.org/packages/Community.PowerToys.Run.Plugin.Dependencies) package.
