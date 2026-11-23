# Security Policy

Cinecore Player is a free beta maintained by a single developer. Security reports are welcome and are taken seriously, even while the project is in beta.

## Supported Versions

Only the latest beta of each platform receives security fixes. The built-in updater installs them for you (with a SHA-256 check), so please update before reporting.

| Platform | Version | Supported |
| -------- | ------- | --------- |
| Windows  | Beta 4.1 (0.4.1) | :white_check_mark: |
| Windows  | Beta 4 (0.4.0) and earlier | :x: |
| macOS    | Beta 1.1 (0.1.1) | :white_check_mark: |
| macOS    | Beta 1 (0.1.0) | :x: |

Builds made from source with local changes are not supported.

## What's in scope

Problems in Cinecore Player's own code, for example:

- **Phone remote**: the built-in web server, QR/PIN pairing, PIN throttling, access from unpaired devices, anything that lets a device on the network control the player or read files without pairing
- **Updater**: anything that could make the player install an update or a Windows-engine file that doesn't match its SHA-256, or download from somewhere other than GitHub Releases
- **Stored secrets**: API keys, tokens and passwords (TMDb, OpenSubtitles, Subdl, Spotify, TIDAL, Trakt, Last.fm, ListenBrainz, Jellyfin, Emby) leaking in clear text, in logs or to a service they don't belong to
- **Network integrations**: home automations (HTTP/MQTT), AV receiver control (including its volume safety limits), WLED, Jellyfin/Emby/DLNA clients
- **File handling**: a crafted media file, playlist (`.wpl`), subtitle, disc image or metadata response that leads to code execution, writing outside the expected folders, or exposure of local paths
- **VideoHost** and the other helper processes, and the installer (file permissions, filter registration)

## What's out of scope

- Vulnerabilities in third-party components (FFmpeg, libmpv, madVR, MPC Video Renderer, LAV Filters, XySubFilter, yt-dlp, Wine, libaacs, …): please report them to their projects. If Cinecore ships an affected version, an issue asking to update it is welcome
- The macOS build not being notarized, or some bundled third-party installers being unsigned on Windows (known and documented)
- Problems that need an attacker who already has full access to your user account or computer
- Bugs in the third-party services the player talks to (TMDb, subtitle and lyrics sites, Trakt, Last.fm, …)

## Reporting a Vulnerability

**Please don't open a public issue for security problems.**

Report it privately through GitHub: open the repository's **Security** tab › **Report a vulnerability** ([direct link](https://github.com/CinecorePlayer/CinecorePlayer/security/advisories/new)).

Please include:

- Platform and version (*Settings › About*), and Windows / macOS version
- What the problem is and what an attacker could do with it
- Steps to reproduce, or a proof of concept
- Relevant log lines (Windows: `%LOCALAPPDATA%\Cinecore\Logs`; macOS: `~/Library/Logs/Cinecore`), **with your keys, tokens, personal paths and IP addresses removed**

## What to expect

- **Acknowledgement** within 7 days.
- **First assessment** (accepted, needs more information, or declined, with the reason) within 14 days.
- **Accepted reports:** the fix ships in the next beta, or in a dedicated update as soon as possible for serious issues; the updater delivers it automatically. A GitHub Security Advisory is published after the fix is available, and you are credited unless you prefer to stay anonymous.
- **Declined reports:** you get an explanation; if it's a normal bug, it may be moved to a public issue with your agreement.
- Updates at least every 14 days while the report is open.

Please give a reasonable amount of time for the fix to be released before disclosing details publicly (90 days at most from the report, or earlier once the fix is out). There is no bug bounty: Cinecore Player is a free, non-commercial project.

Thank you for helping keep Cinecore Player and its users safe.
