# Developing viddl

## Project structure

```
viddl.sln
src/
  viddl.Core/         Models, HLS playlist parsing, block lists and browser protection rules,
                      settings, favorites and history, address-bar suggestions, stream detection
  viddl.Downloads/    Download engines (HLS, DASH, files, yt-dlp), ffmpeg runner, page scanner,
                      tool installer and the download queue
  viddl.App/          WinUI 3 desktop app (unpackaged) built on the two libraries
installer/            Per-user MSI installer (WiX)
build/                package.ps1 (zip + MSI per platform), sign.ps1, gitea-release.ps1, make-icon.ps1
tests/
  viddl.Core.Tests/       Unit tests for viddl.Core
  viddl.Downloads.Tests/  Unit tests for viddl.Downloads
  coverage.ps1            Runs both test projects and prints a coverage summary
```

The libraries have no UI dependencies. Network access, external programs, tool locations and delays reach the
download engines through `DownloadEnvironment`, so tests run against an in-memory HTTP server and scripted
stand-ins for ffmpeg and yt-dlp, with no network access or real waiting.

To build it yourself you need the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0); open
`viddl.sln` in Visual Studio 2022 or later (set **viddl.App** as the startup project), or run
`dotnet run --project src/viddl.App`. `build/package.ps1 -Platform x64 -Version 2.0.0` produces the zip and installer.

## Code signing

Windows shows "Windows protected your PC" (Microsoft Defender SmartScreen) for apps and installers that aren't
code-signed, and unsigned apps are more likely to be flagged by antivirus scanners. A self-signed certificate doesn't
help: the certificate has to come from an authority Windows trusts. Set up one of these, then add its values as
repository secrets (on GitHub and, if used, on Gitea):

**Option 1 – Azure Trusted Signing (recommended).** Microsoft's own signing service, about US$10 a month, with no
certificate file to look after. Its certificates chain to a Microsoft root, so SmartScreen trusts them quickly.

1. In the Azure portal, create a [Trusted Signing account](https://learn.microsoft.com/azure/trusted-signing/quickstart),
   complete identity validation, and create a **Public Trust** certificate profile.
2. Create an app registration (Microsoft Entra ID → App registrations), add a client secret, and give it the
   **Trusted Signing Certificate Profile Signer** role on the account.
3. Add these secrets:

   | Secret | Value |
   |---|---|
   | `AZURE_TENANT_ID` | The directory (tenant) ID |
   | `AZURE_CLIENT_ID` | The app registration's application (client) ID |
   | `AZURE_CLIENT_SECRET` | The client secret |
   | `TRUSTED_SIGNING_ENDPOINT` | The account's endpoint, e.g. `https://weu.codesigning.azure.net` |
   | `TRUSTED_SIGNING_ACCOUNT` | The account name |
   | `TRUSTED_SIGNING_PROFILE` | The certificate profile name |

**Option 2 – a certificate from a certificate authority** (DigiCert, Sectigo, ...). Export it as a `.pfx` file and add:

- `SIGNING_CERT_BASE64` – the file, base64-encoded:
  `[Convert]::ToBase64String([IO.File]::ReadAllBytes('cert.pfx'))`
- `SIGNING_CERT_PASSWORD` – its password

With either set up, every build signs `viddl.exe`, viddl's DLLs and each `.msi` installer (with a timestamp, so
signatures stay valid after the certificate expires), and Windows shows **viddl** as the verified publisher. Without
them the build still succeeds but adds an "Unsigned build" warning to the run. SmartScreen can still warn for a short
while after the first signed release, until the certificate has built up reputation. If a scanner flags a release,
submit it to Microsoft as a false positive at
[microsoft.com/wdsi/filesubmission](https://www.microsoft.com/wdsi/filesubmission).

## Builds and releases

[`.github/workflows/build.yml`](../.github/workflows/build.yml) runs on every push and pull request to `main`
(and can be started by hand from the Actions tab):

1. **Build and test** – builds the solution in Release, runs the unit tests and adds a coverage table to the run
   summary. Test results are kept as a `test-results` artifact.
2. **Package** – for `win-x64`, `win-x86` (32-bit Windows) and `win-arm64`, builds a self-contained app (the .NET
   runtime and the Windows App SDK are included, so nothing needs installing) as both
   `viddl-<version>-<platform>.msi` and `viddl-<version>-<platform>.zip`, signed when a certificate is configured
   (see [Code signing](#code-signing)).
3. **Release** – on pushes to `main` only, tags the commit `v<version>` and creates a GitHub release with the
   installers and zips attached and notes generated from the changes since the previous release.

Versions are `major.minor.run`: the major and minor numbers come from `Directory.Build.props` and the last part is
the workflow run number, so every build of `main` gets a new tag and release (for example `v2.0.57`). To start a
new series, change `<Version>` in `Directory.Build.props` (for example to `2.1.0`). Local builds use the version
from that file as-is.

### Gitea

[`.gitea/workflows/build.yml`](../.gitea/workflows/build.yml) does the same build, test and publish for a copy of the
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
