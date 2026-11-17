# viddl

[![Build](https://github.com/edgeofsanity76/viddl/actions/workflows/build.yml/badge.svg?branch=main)](https://github.com/edgeofsanity76/viddl/actions/workflows/build.yml)
[![Latest release](https://img.shields.io/github/v/release/edgeofsanity76/viddl?label=release)](https://github.com/edgeofsanity76/viddl/releases/latest)

viddl is a Windows desktop app for saving online videos as MP4 files. It has a built-in browser that
watches network traffic while a video plays, lists the streams it finds, and downloads the one you pick.
Downloads are converted to MP4 without re-encoding.

## Features

- **Built-in browser** (Microsoft Edge WebView2) with back/forward/reload, an address bar that suggests
  favorites and recently visited sites, and a favorites menu.
- **Stream detection** while a video plays: HLS (`.m3u8`), DASH (`.mpd`) and whole video files. HLS entries
  show the available qualities or the running time; per-quality playlists are grouped under their master
  playlist so each video appears once.
- **Download engines**
  - HLS: picks a quality, downloads segments in parallel, decrypts standard AES-128, merges separate audio
    tracks and supports fragmented MP4. Live streams are recorded until stopped.
  - DASH: fetched and muxed by ffmpeg.
  - Whole files: saved directly (non-MP4 containers are remuxed).
  - Web pages: handed to yt-dlp, with a fallback that scans the page HTML.
- **Download queue** with steady progress, speed and time remaining, retry, and stop/resume.
- **Browser protection**: blocks known malware, phishing and cryptomining domains, suppresses page dialogs and
  pop-ups, cancels downloads started by pages, denies permission prompts, and stops pages from launching other
  programs.
- **Strict mode, when it's needed**: viddl watches what each site tries to do (redirecting you elsewhere,
  loading known malicious content, starting downloads or other programs, pop-ups, dialog and permission spam,
  heavy advertising). When a site crosses the line, strict mode switches on for that site only, blocking
  third-party scripts and automatic cross-site redirects. A notice offers to reload the page or undo, and a
  **Strict** badge in the address bar shows whenever strict mode is active. Strict mode can also be turned on
  for every site.
- **Ad and tracker blocking** using a built-in list, optionally extended with a community hosts list.

DRM-protected streams (Widevine, PlayReady, FairPlay, SAMPLE-AES) are detected, shown greyed out and never
downloaded. Only download videos you have the right to save.

## Requirements

- Windows 10 version 1809 or later, or Windows 11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Visual Studio 2022 (17.8 or later) with the **.NET desktop development** workload and the
  **Windows App SDK C# Templates** component, or the .NET CLI on its own
- Microsoft Edge WebView2 Runtime (included with Windows 11 and current Windows 10 builds)
- ffmpeg and yt-dlp (viddl can install both for you, see below)

## Getting started

### Visual Studio

1. Open `viddl.sln`.
2. Set **viddl.App** as the startup project and choose the **x64**, **x86** or **ARM64** platform.
3. Press **F5**.

### Command line

```powershell
dotnet build viddl.sln
dotnet run --project src/viddl.App
```

### First run

Open **Settings → Install / update ffmpeg & yt-dlp**. viddl downloads ffmpeg from
[BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds) and yt-dlp from
[yt-dlp/yt-dlp](https://github.com/yt-dlp/yt-dlp) into its data folder. Copies already on your `PATH` are
used as well.

## Using viddl

1. Type an address or search terms in the address bar and press Enter.
2. Play the video. Its streams appear under **Detected streams**, newest first.
3. Select one or more streams, adjust the file name if you like, and click **Download selected**
   (or double-click a stream, or press Enter).
4. Follow progress under **Downloads**. Right-click an entry to play it, show it in its folder, retry it or
   remove it.

To download a link directly (an `.m3u8`, `.mpd` or `.mp4` URL, or a page supported by yt-dlp), paste it in the
address bar and click **Download URL**.

### Keyboard shortcuts

| Keys | Action |
|---|---|
| F5 | Reload |
| Alt+← / Alt+→ | Back / forward |
| Ctrl+L | Go to the address bar |
| Ctrl+D | Add or remove the current page from favorites |
| Enter (stream list) | Download the selected streams |
| Delete | Remove the selected streams or downloads |
| Esc | Stop the current download |

### Protection settings

The shield button in the address bar shows how many threats and ads were blocked. Its menu turns protection,
strict-mode detection, strict mode for every site and ad blocking on or off.

Strict mode is off by default. When a site behaves suspiciously, viddl switches it on for that site and shows a
notice; the **Strict · detected** badge then appears in the address bar while you're on that site. Hover over the
badge to see why, or click it to turn strict mode off for the site (viddl then won't switch it on there again).
When strict mode is on for every site, the badge reads **Strict**, and the menu can allow third-party scripts on
the current site if its video player doesn't work. **Settings → Update block lists** downloads the
[StevenBlack hosts](https://github.com/StevenBlack/hosts), [URLhaus](https://urlhaus.abuse.ch/) and
[NoCoin](https://github.com/hoshsadiq/adblock-nocoin-list) lists.

You can add your own domains, one per line, to `adblock-custom.txt` or `malware-custom.txt` in the data folder.

## Where viddl keeps its files

| Location | Contents |
|---|---|
| `%AppData%\viddl\settings.json` | Settings, favorites, history, sites where strict mode was switched on, and exceptions |
| `%LocalAppData%\viddl\tools` | ffmpeg and yt-dlp |
| `%LocalAppData%\viddl\WebView2` | The browser profile (cookies, cache, sign-ins) |
| `%LocalAppData%\viddl\*.txt` | Downloaded and custom block lists |

Data from versions before 2.0 (stored under `StreamVideoDownloader`) is copied to these folders on first start.

## Project structure

```
viddl.sln
src/
  viddl.Core/         Models, HLS playlist parsing, block lists and browser protection rules,
                      settings, favorites and history, address-bar suggestions, stream detection
  viddl.Downloads/    Download engines (HLS, DASH, files, yt-dlp), ffmpeg runner, page scanner,
                      tool installer and the download queue
  viddl.App/          WinUI 3 desktop app (unpackaged) built on the two libraries
tests/
  viddl.Core.Tests/       Unit tests for viddl.Core
  viddl.Downloads.Tests/  Unit tests for viddl.Downloads
  coverage.ps1            Runs both test projects and prints a coverage summary
```

The libraries have no UI dependencies. Network access, external programs, tool locations and delays reach the
download engines through `DownloadEnvironment`, so tests run against an in-memory HTTP server and scripted
stand-ins for ffmpeg and yt-dlp, with no network access or real waiting.

## Builds and releases

[`.github/workflows/build.yml`](.github/workflows/build.yml) runs on every push and pull request to `main`
(and can be started by hand from the Actions tab):

1. **Build and test** – builds the solution in Release, runs the unit tests and adds a coverage table to the run
   summary. Test results are kept as a `test-results` artifact.
2. **Publish** – produces self-contained builds for `win-x64`, `win-x86` (32-bit Windows) and `win-arm64`
   (the .NET runtime and the Windows App SDK are included, so nothing needs installing) and zips them as
   `viddl-<version>-<platform>.zip`.
3. **Release** – on pushes to `main` only, tags the commit `v<version>` and creates a GitHub release with the
   three zips attached and notes generated from the changes since the previous release.

Versions are `major.minor.run`: the major and minor numbers come from `Directory.Build.props` and the last part is
the workflow run number, so every build of `main` gets a new tag and release (for example `v2.0.57`). To start a
new series, change `<Version>` in `Directory.Build.props` (for example to `2.1.0`). Local builds use the version
from that file as-is.

### Gitea

[`.gitea/workflows/build.yml`](.gitea/workflows/build.yml) does the same build, test and publish for a copy of the
repository on a Gitea server (Gitea uses the `.gitea` folder and GitHub ignores it, so each server runs its own
workflow).

**Setup**

1. Enable Actions on the Gitea server and in the repository settings.
2. Register a **Windows** [act_runner](https://docs.gitea.com/usage/actions/act-runner) with the label `windows`
   (WinUI apps can only be built on Windows) on a machine with Git installed. The workflow installs the .NET SDK
   itself.
3. Optional: if the workflow's automatic token isn't allowed to create releases on your server, add a personal access
   token with repository write access as the repository secret `RELEASE_TOKEN`.

**How releases work there**

- When a `v*` tag arrives (for example mirrored from GitHub, where every build of `main` creates one), the workflow
  builds exactly that version and attaches the three zips to a Gitea release for the same tag, with notes listing
  the commits since the previous tag.
- Pushes to `main` only build and test, so the Gitea copy never creates tags that clash with mirrored ones.
- If Gitea is your primary server instead, set the repository variable `CREATE_TAGS` to `true`: every build of `main`
  then gets its own `v<major>.<minor>.<run>` tag and release, as on GitHub.

Mirroring notes: a push mirror (GitHub or your machine pushing to Gitea) behaves like normal pushes. With a pull mirror
(Gitea fetching from GitHub), whether a sync starts workflows and whether releases can be added to the mirror depend
on your Gitea version and settings; you can always start the workflow by hand from the repository's Actions tab.

## Tests

```powershell
dotnet test viddl.sln
```

To see coverage for the libraries:

```powershell
powershell -ExecutionPolicy Bypass -File tests/coverage.ps1
```

The WinUI app project contains only view code and is not covered by unit tests.
