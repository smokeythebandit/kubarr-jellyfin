# kubarr-jellyfin

Jellyfin plugin that makes [Kubarr](https://github.com/smokeythebandit/kubarr) Jellyfin's identity provider and sets the server up without the startup wizard. Targets Jellyfin 12.3 (`targetAbi` 12.3.0.0).

## What it does

**Sign-in**
- **Browser single sign-on.** Kubarr's gateway attaches a short-lived signed token (`X-Kubarr-Identity`, RS256, verified against Kubarr's `/auth/jwks`) to requests from a signed-in Kubarr user. Kubarr sends them to `/Kubarr/sso`, which exchanges the token for a Jellyfin session and opens the web client. The login page also gets a "Sign in with Kubarr" link.
- **Native apps.** A Jellyfin sign-in provider checks username and password against Kubarr (`POST /api/integrations/jellyfin/verify`). Users with two-factor authentication use a Kubarr app password.
- **Accounts.** An account is created the first time someone signs in. Kubarr admins (`settings.manage`) become Jellyfin admins.
- **Access changes.** Kubarr pushes its user list to `POST /Kubarr/users/sync` when users or roles change, and the plugin also polls every 5 minutes. An account whose user lost access is disabled and signed out.
- **Accounts the plugin leaves alone.** It never touches an account it did not create or adopt, so a local break-glass admin keeps working. An existing local account whose name matches a Kubarr user is adopted.

**Server setup** (idempotent, repeated every 5 minutes)
- Registers the API key Sonarr and Radarr use. This replaces the old SQLite-writing sidecar.
- Completes the startup wizard: server name and languages. Any passwordless setup account gets a random password and is hidden.
- Creates each media library once, and only if its folder exists. A library someone deleted is not recreated.
- Turns on hardware transcoding for the GPU vendor Kubarr assigned (intel → QSV, amd → VAAPI, nvidia → NVENC). It only changes this when the assignment changes.

## Configuration (set by Kubarr's Jellyfin chart)

| Source | Default |
|---|---|
| `KUBARR_API_URL` | `http://kubarr-backend.kubarr-backend.svc.cluster.local:8000` |
| `KUBARR_IDENTITY_KEY_FILE` | `/kubarr-secrets/jellyfin-identity/api-key` |
| `KUBARR_LIBRARY_KEY_FILE` | `/kubarr-secrets/jellyfin/api-key` |
| `KUBARR_BOOTSTRAP_FILE` | `/kubarr-config/bootstrap.json` |
| `KUBARR_GPU_VENDOR` | empty |

Without the identity key the sign-in features stay off.

## Jellyfin 12 notes

- Sign-in providers are resolved from the DI container, so the plugin registers its provider there.
- When no account matches the name, the provider must create the account itself.
- During sign-in Jellyfin holds the account's lock, so policy updates for an existing account are applied right after sign-in, not during it.

## Build and test

```sh
dotnet test
docker build --build-arg VERSION=1.0.0 -t kubarr-jellyfin:1.0.0 .
```

The image runs `install.sh` as an init container. It copies the plugin to `$PLUGIN_DIR/Kubarr_<version>` (default `/config/data/plugins`) and removes other versions.
