# OrbitLab — Installer & Updates (Phase 6A foundation)

This document describes the Commercial Beta installer/updater foundation added in
Phase 6A. **No public distribution exists yet** — there is no domain, no HTTPS, and
no public update feed. Everything below is prepared so that turning those on later
is a configuration change, not a code change.

---

## 1. Components

| Piece | Where | Status |
|---|---|---|
| App config layer | `AppConfig.cs` | done — backend/update URLs are optional, never hardcoded |
| Update policy client | `UpdatePolicy.cs` | done — talks to `GET /api/app/version` + `/api/public/bootstrap` |
| Forced-update gate | `UpdateRequiredWindow.xaml(.cs)` | done — blocking window, user-driven download, SHA256 verification |
| Updater plumbing | `UpdateService.cs` + Velopack package | done — inert until an update feed URL is configured |
| Packaging script | `tools/package-windows.ps1` | done — publish + `vpk pack` (or zip fallback) |
| Backend release admin | ResearchLab-Backend `/admin/releases` | done — version/minimum/forced/URL/SHA256 metadata |

## 2. Configuration (no secrets, all optional)

The app reads, in priority order:

1. Environment variables `ORBITLAB_BACKEND_URL` / `ORBITLAB_UPDATE_FEED`
2. `orbitlab.settings.json` next to the exe
3. `orbitlab.settings.json` in `%APPDATA%\AIFlashcardMaker\`

```json
{
  "backendBaseUrl": "http://127.0.0.1:5000",
  "updateFeedUrl": ""
}
```

Rules:
- **https is required**; plain `http` is accepted only for `localhost` / `127.0.0.1`
  (staging via SSH tunnel). A plaintext public URL is silently rejected.
- When nothing is configured the app runs fully offline exactly as before.
- The VPS IP is deliberately **not** hardcoded anywhere in the app.

## 3. Startup update check (fail-open)

On startup the app calls `GET {backend}/api/app/version?channel=beta&platform=windows`
with a hard 5-second budget.

- **Backend unreachable / not configured** → the app opens normally; the only trace
  is a quiet "Update check unavailable" line under Settings → Updates & Telegram.
  A missing server can never lock users out.
- **Optional update** (`forced=false`, newer version exists) → non-blocking toast +
  status line. Nothing else.
- **Forced update** (`forced=true` AND current version < `minimumSupportedVersion`)
  → the blocking `UpdateRequiredWindow` appears: release notes, "Download and
  Update", "Join Telegram Channel", support email, "Close OrbitLab". There is no
  path into the app except updating.

Download safety in the forced window:
- non-HTTPS URLs (except loopback for staging tests) are refused outright;
- nothing ever runs silently — the user clicks download, then explicitly opens the installer;
- if the backend published a SHA256, the file must match or it is **deleted** with a warning;
- if no SHA256 was published, the app never launches the file itself — it opens the
  folder and tells the user to verify against the official Telegram announcement.

## 4. Building an installer locally

```powershell
# optional, one-time:
dotnet tool install -g vpk

powershell -ExecutionPolicy Bypass -File tools\package-windows.ps1
# or with an explicit version:
powershell -ExecutionPolicy Bypass -File tools\package-windows.ps1 -Version 0.9.1
```

Output lands in `artifacts\installer\` (gitignored):
- with `vpk`: a Velopack Setup.exe + update packages (`*.nupkg`, `RELEASES`);
- without `vpk`: a plain zip for manual testing only.

The script prints SHA256 hashes — paste the installer hash into the admin
**Releases / Updates** page so clients can verify downloads.

The packaged app contains **only** the dotnet publish output. Local databases,
`orbitlab.settings.json`, and anything in AppData (accounts, decks, research
projects) are never included; the script actively strips lookalike files.

## 5. Code signing / SmartScreen caveat

Builds are **not code-signed yet**. Windows SmartScreen will show "Windows
protected your PC" for downloaded installers; testers must click *More info →
Run anyway*. Communicate this in the Telegram channel. Before public launch,
buy a code-signing certificate (OV ~ cheaper, EV = instant SmartScreen
reputation) and add signing to `vpk pack` (`--signParams`).

## 6. Update feed — later, after domain + HTTPS

Nothing is served publicly today. The plan:

1. Buy domain, set up HTTPS (Caddy/nginx on the VPS or a CDN).
2. Host the Velopack output directory at e.g.
   `https://downloads.<domain>/orbitlab/windows/beta/`
   (`/opt/orbitlab/releases/` on the VPS is reserved for this; it is not exposed).
3. Set `updateFeedUrl` in the app config to that URL, and the installer URL +
   SHA256 in the admin Releases page.
4. `UpdateServiceFactory` then returns the real `VelopackUpdateService` and
   in-app delta updates start working; until then it reports `NotConfigured`.

## 7. Privacy

The update system exchanges **version metadata only**. No research projects, CSV
files, participant rows, results, reports, or exports are ever sent — the update
endpoints are GET-only and unauthenticated, and the backend stores only
account/license metadata by design.
