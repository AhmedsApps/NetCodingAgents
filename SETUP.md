# CodingAgents — Setup

This copy has been stripped of personal data, credentials and private URLs.
Every value you need to supply is marked with a `REPLACE WITH:` comment in the
relevant file. This page lists them all in one place.

## 1. Required settings

### `src/CodingAgents.Server/appsettings.json`
| Setting | What to put there |
|---|---|
| `ConnectionStrings:DefaultConnection` | Your SQL Server connection string. The database is created automatically on first run. |
| `WorkerKey` | A long random string (e.g. a GUID). Must match the worker's `WorkerKey`. |
| `AllowedOrigins` | Comma-separated URLs of your web client, e.g. `https://chat.example.com`. Leave empty to allow localhost only. |

### `src/CodingAgents.Worker/appsettings.json`
| Setting | What to put there |
|---|---|
| `ServerUrl` | Base URL of the server, e.g. `https://localhost:7150/`. |
| `WorkerKey` | **The same** random string you set on the server. |
| `OllamaUrl` *(optional)* | Local Ollama URL. Defaults to `http://localhost:11434/`. |
| `WorkspaceRoot` *(optional)* | Where per-task working folders are created. Defaults to `%LocalAppData%\CodingAgents\Workspaces`. |

### `src/CodingAgents.WebClient/appsettings.json`
| Setting | What to put there |
|---|---|
| `ServerUrl` | Base URL of the server. Must also appear in the server's `AllowedOrigins`. |

### `src/CodingAgents.MauiClient/Components/Pages/Home.razor`
Set `ServerUrl` for both the `DEBUG` and release branches.

## 2. Optional settings

Notifications are **disabled by default**. Enable them only after filling in the
credentials in `src/CodingAgents.Server/appsettings.json`:

- **WhatsApp** — set `EnableWhatsApp: true`, then fill `WhatsApp:Phone` (international
  format, digits only) and `WhatsApp:ApiKey` from <https://www.callmebot.com/>.
- **Email** — set `EnableEmail: true`, then fill the `Email` section with your
  SMTP/IMAP host, port, mailbox user name, password and the recipient address.

## 3. First run

1. Configure the server connection string and `WorkerKey`, then start the server.
2. Start the worker **as a normal app in your logged-in desktop session**
   (not as a Windows Service — a service cannot capture screenshots).
3. Open the web client and sign in.

### Default password

The app creates a single access password on first run:

```
admin
```

**Change it immediately** in Settings → App Password. A warning banner is shown
while the default is still in use.

## 4. Before publishing or committing

- Never commit real values for `WorkerKey`, connection strings, the `Email` section
  or publish profiles.
- Prefer environment variables or user-secrets for secrets, e.g.
  `ConnectionStrings__DefaultConnection`, `Email__Password`.
- Recommended `.gitignore` entries:

```gitignore
**/Properties/PublishProfiles/
*.pubxml
*.pubxml.user
*.PublishSettings
*.csproj.user
appsettings.Production.json
**/artifacts/
*.db
```
