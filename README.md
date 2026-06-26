# ntfy desktop (Windows)

A native Windows desktop client for [ntfy](https://ntfy.sh) — subscribe to topics,
receive native Windows notifications, and publish messages. Built with WinUI 3 and
the Windows App SDK.

## Features

- **Subscribe** to topics on `ntfy.sh` or any self-hosted server, with per-account
  authentication (access token or username/password).
- **Native Windows notifications** for incoming messages, with selectable sound and
  a minimum-priority filter.
- **Message history** per topic, with markdown, tags/emoji, attachments, priority,
  click actions, and action buttons.
- **Publish** notifications with the full ntfy feature set: title, priority, tags,
  markdown, click URL, attachments (URL or local file), email, delayed delivery,
  and message templating.
- **System tray**: closing the window keeps the app streaming in the background.
- **Accounts**: save credentials per server and pick them when subscribing/publishing.
- Light / Dark / System themes, launch on startup, and start minimized to tray.

## Requirements

- Windows 10 19041+ / Windows 11
- .NET 9 SDK (for building)

## Build & run

```powershell
./BuildAndRun.ps1 NtfyDesktop.csproj
```

This builds (Debug, auto-detected platform) and launches the app via `winapp run`.
Use `-SkipRun` to build only.

## Project layout

- `Models/` — data models (subscriptions, messages, settings, credentials)
- `Services/` — streaming/subscription manager, notifications, persistence
- `ViewModels/` — MVVM view models (CommunityToolkit.Mvvm)
- `Views/` — XAML pages and dialogs
- `Assets/` — app icons (ntfy glyph, teal `#338574`)

## License

Private. © Onat Akduman.
