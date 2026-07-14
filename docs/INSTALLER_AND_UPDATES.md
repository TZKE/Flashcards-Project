# OrbitLab — Installer & Updates (Phase 6A / 6A.1)

This document describes the Commercial Beta installer/updater foundation added in
Phase 6A and finalized for packaging in Phase 6A.1. **No public distribution exists
yet** — there is no domain, no HTTPS, and no public update feed. Everything below is
prepared so that turning those on later is a configuration change, not a code change.

---

## 0. Versioning scheme (Phase 6A.1)

| Field | Value | Where |
|---|---|---|
| User-visible label | **`0.1.0-beta.1`** | `InformationalVersion` — shown in Settings → Updates & Telegram and the update dialog |
| AssemblyVersion | `0.1.0.0` | update-gate comparisons (`AppConfig.CurrentVersion`) |
| FileVersion | `0.1.0.0` | Windows file properties |
| Velopack package version | `0.1.0-beta.1` | SemVer 2 prerelease — fully supported by Velopack |
| Backend release metadata | `0.1.0` | the admin Releases page stores numeric `System.Version` values; the `-beta.N` tag is client-side display metadata |

Rules going forward: bump `beta.N` for each beta package; bump `0.1.x` for fixes;
`1.0.0` at commercial launch. All four csproj fields live together at the top of
`FlashcardMaker.csproj`. The `AssemblyName` is still `AI-Flashcard-Maker` and the
namespace `AIFlashcardMaker` — deliberately unchanged (see `Branding.cs`); the
installer's display name is **OrbitLab by StarshipAI** regardless.

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

The Velopack CLI is pinned as a **repo-local dotnet tool** (`.config/dotnet-tools.json`,
`vpk` 1.2.0 — same version as the Velopack library reference). One-time on a fresh clone:

```powershell
dotnet tool restore
```

Then:

```powershell
powershell -ExecutionPolicy Bypass -File tools\package-windows.ps1
# or with an explicit version:
powershell -ExecutionPolicy Bypass -File tools\package-windows.ps1 -Version 0.1.0-beta.1
```

Output lands in `artifacts\installer\` (gitignored — installers are never committed):
- with `vpk` (local tool preferred, global fallback): `OrbitLab-win-Setup.exe`,
  the full `.nupkg` update package, `releases.win.json` + `RELEASES` feed manifests,
  and a `-Portable.zip`;
- without any `vpk`: a plain zip fallback for manual testing only (**not** an installer).

The script hard-fails if anything private (settings, databases, research files,
key/secret-named files) appears in the publish output, and prints SHA256 hashes
for every artifact.

Install location (per-user, no admin rights): `%LocalAppData%\OrbitLab`.
Uninstall: Windows *Settings → Apps → Installed apps → OrbitLab by StarshipAI*
(or `%LocalAppData%\OrbitLab\Update.exe --uninstall`).

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
