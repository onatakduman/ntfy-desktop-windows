# AGENTS.md

Guide for coding agents working on **Notidesk**, a WinUI 3 / Windows App SDK desktop
client for [ntfy](https://ntfy.sh), shipped through the Microsoft Store
(Store ID `9PJDD0JKR719`). It covers how to fix things and how to publish a release.

This repository is public: never write secrets, tokens, private hostnames or personal
data into files, commits, issues or logs.

## Stack

- .NET 9, WinUI 3, Windows App SDK 2.x (framework-dependent), CommunityToolkit.Mvvm,
  H.NotifyIcon for the tray.
- Packaged (MSIX) app. Package identity `ONATAKDUMAN.ntfy`, publisher
  `CN=DF35B028-AD72-4A00-A6F0-EEC2A01EE870`. These values are fixed by Partner Center:
  **do not change anything in the `<Identity>` element except `Version`**, or Store
  uploads fail.
- Builds need Windows. On macOS/Linux you can edit, commit and let CI build.

## Layout

| Path | What lives there |
|---|---|
| `Program.cs` | Entry point; single-instance redirect (a second launch activates the running one) |
| `App.xaml.cs` | Startup, activation kinds (normal / Start with Windows / toast click), toast routing |
| `MainWindow.xaml(.cs)` | Window, sidebar, tray icon (created up front so a hidden start still has a tray) |
| `Services/SubscriptionManager.cs` | ntfy streaming, reconnect watchdog, `since=` catch-up, own-message detection |
| `Services/NotificationService.cs` | Windows toasts |
| `Services/PersistenceService.cs` | JSON state in `%LOCALAPPDATA%\Packages\ONATAKDUMAN.ntfy_7pe54xh56ptzt\LocalState` (`subscriptions.json`, `messages.json`, `settings.json`) |
| `Services/UrlLauncher.cs` | Only http/https/mailto are opened from message content |
| `Services/AppIcon.cs` | Tray/window icon, embedded and loaded synchronously |
| `ViewModels/`, `Views/` | MVVM view models and XAML; `SettingsPageViewModel` owns the Start with Windows toggle (`StartupTask` id `NotideskStartup`) |
| `Package.appxmanifest` | Package identity, capabilities, startup task, toast activation |
| `.github/workflows/ci.yml` | Release builds for x64 and ARM64 on every push/PR to `main` |
| `.github/workflows/release.yml` | Tag `v*` → packages → GitHub Release → Microsoft Store |

## Fixing something

1. **Reproduce first.** Read the issue, find the code path, and if you can, reproduce it
   on Windows before changing anything.
2. **Build on Windows:**
   ```powershell
   ./BuildAndRun.ps1 NtfyDesktop.csproj -Detach      # build Debug and launch
   ./BuildAndRun.ps1 NtfyDesktop.csproj -SkipRun     # build only
   dotnet build NtfyDesktop.csproj -c Release -p:Platform=x64 -r win-x64     # what CI runs
   dotnet build NtfyDesktop.csproj -c Release -p:Platform=ARM64 -r win-arm64
   ```
   Keep the build at 0 errors and 0 warnings. Always pass `-r` with a non-native
   platform: the project otherwise picks the runtime from the build machine
   (error NETSDK1032).
3. **Verify in the running app**, not just the build. Useful checks:
   - Publish a test message from outside the app (`Invoke-RestMethod -Method Post
     https://ntfy.sh/<test-topic> -Body hi`) and confirm the card and the toast.
   - Messages sent from the app itself must appear in history **without** a toast.
   - Closing the window keeps the tray icon; launching again brings the window back.
4. **Commit** with an imperative subject and a body that explains why
   (see `git log`). Do **not** add `Co-Authored-By` or `Claude-Session` trailers.
   The git identity comes from the workspace config; don't override it.
   Reference issues as `owner/repo#N`; add `Fixes #N` only if the issue should close
   on merge.

### Pitfalls we already hit

- **ContentDialog + NavigationView:** never change the sidebar's items or selection
  inside a ContentDialog button handler. Validate in `PrimaryButtonClick`, do the work
  in the dialog's `Closed` event (see `Views/SubscribeDialog.xaml.cs`). Otherwise the
  compositor can crash natively (not catchable).
- **UI automation crashes the app on GPU-less machines (WARP renderer):** UIA
  `invoke`/`search` (for example `winapp ui invoke`) can kill the process. Verify with
  passive screenshots and real ntfy traffic instead. Launch with `-Detach`: without it
  `winapp run` attaches a debugger that treats a harmless first-chance access
  violation as fatal.
- **Packaged apps start in `System32`:** use absolute paths (`AppContext.BaseDirectory`)
  for files.
- **Async `void` handlers swallow failures:** an exception there ends up in
  `App.UnhandledException` and the feature silently does nothing (this was the blank
  tray icon bug).
- **Remote Windows over SSH:** the SSH session is not the interactive desktop. To start
  the GUI or take a screenshot, run the command through a one-shot scheduled task with
  `LogonType Interactive`. With a cmd default shell, put PowerShell into a `.ps1` and
  run it with `-File` instead of fighting quoting.
- **Testing Start with Windows needs a real sign-in.** A Hyper-V VM with auto-logon
  works: install the Windows App Runtime framework MSIX (from the NuGet cache,
  `microsoft.windowsappsdk.runtime/<ver>/tools/MSIX`), register the app, flip the toggle
  in Settings, restart the VM, and check the app runs hidden with a tray icon.

## Publishing a release

Publishing is automated. **Only publish when the user asks for it.**

1. Make sure `main` contains the changes, is pushed, and the latest CI run on `main`
   is green:
   ```bash
   gh run list --workflow ci.yml --branch main --limit 1
   ```
2. Pick the next version (`vMAJOR.MINOR.PATCH`), higher than the last tag
   (`git tag --sort=-v:refname | head -1`). The workflow writes it into the package; you
   don't need to edit `Package.appxmanifest`.
3. Tag and push:
   ```bash
   git tag -a v1.0.3 -m "Notidesk 1.0.3"
   git push origin v1.0.3
   ```
4. Watch the run until it finishes and report the result:
   ```bash
   gh run list --workflow release.yml --limit 1
   gh run watch <run-id> --exit-status
   ```
   On success the last step prints `Submission Status - Certification` and the package
   list. Certification then takes from a few hours to a few days; the update reaches
   users after that. Xbox warnings about `windows.toastNotificationActivation` are
   expected (the app is not offered on Xbox).

What `release.yml` does: sets the Identity `Version` from the tag → builds x64 and
ARM64 → `winapp package` makes two unsigned `.msix` → `makeappx bundle` joins them into
one `.msixbundle` → GitHub Release with all three files → `msstore publish <bundle>
--appId 9PJDD0JKR719` submits to the Store (the Store signs the package).

### When the release run fails

- **Fix, then retry.** If the failure was outside the repo (credentials, Store side),
  rerun only the failed job: `gh run rerun <run-id> --failed`. The release step uploads
  to an existing GitHub Release with `--clobber`, so reruns are safe.
- **If the workflow or code had to change:** commit the fix to `main`. If nothing reached
  the Store yet, you may delete the GitHub Release and tag and tag the new commit with
  the same version (`gh release delete vX.Y.Z --cleanup-tag --yes`). Once a version has
  been submitted to the Store, never reuse it: use the next patch version.
- Known errors:

| Error | Cause / fix |
|---|---|
| `NETSDK1032 ... 'win-x64' and the PlatformTarget 'arm64'` | Missing `-r win-arm64` on the ARM64 build |
| `makeappx ... Non appx extensions are not allowed ... older platforms` | `TargetDeviceFamily MinVersion` got overwritten; only the Identity `Version` may change |
| `We could not find a project publisher for the project at '...'` | `msstore publish` was given a folder; give it the `.msixbundle` file |
| `Could not retrieve your application` | The Partner Center Entra app needs the **Manager(Windows)** role (both the app and its Service Principal row). Partner Center can't edit an app's role: remove it and add the existing app again with Manager; the Entra app and its key survive |
| Authentication fails in `msstore reconfigure` | `MSSTORE_CLIENT_SECRET` expired or wrong (current key expires **2028-11-14**) |
| Publish fails while a submission is in certification | Wait for certification to finish, or ask the user to cancel it in Partner Center |

### Credentials

The Store step uses four repository secrets: `MSSTORE_TENANT_ID`, `MSSTORE_SELLER_ID`,
`MSSTORE_CLIENT_ID`, `MSSTORE_CLIENT_SECRET` (Entra app "Notidesk - Store Release" in
the Partner Center–linked tenant).

- Agents must **never** read, print or type the client secret. To rotate it, the user
  creates a new key in Partner Center (Account settings → User management → Microsoft
  Entra applications → Notidesk - Store Release → Add new key) and runs:
  ```bash
  gh secret set MSSTORE_CLIENT_SECRET --repo onatakduman/ntfy-desktop-windows
  ```
- `devcert.pfx` is a local signing certificate for sideload testing; it is gitignored
  and must stay out of commits.

### Not done by the pipeline

Store listing text, screenshots, pricing and markets are edited by the user in Partner
Center. `msstore publish` keeps the existing listing and only replaces packages.
