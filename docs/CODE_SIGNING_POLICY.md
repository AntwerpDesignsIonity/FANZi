# Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org).

> Status: applied for. Until SignPath Foundation approves the project, Windows releases are **not** signed and Windows shows an
> "unknown publisher" prompt. This page describes how signing works once it is in place.

## What is signed

The Windows release binaries of FANZi IO-nity: `Fanzi.FanControl.exe` (the application, also shipped as
`FANZi-IO-nity-vX.Y.Z-Portable-win-x64.exe`) and `FANZI-Installer.exe`.

## How a signed release is made

1. Source: the public repository [AntwerpDesignsIonity/FANZi](https://github.com/AntwerpDesignsIonity/FANZi), branch `Root-Main`, at a `vX.Y.Z` tag.
2. Build: the GitHub Actions workflow [`release-windows.yml`](../.github/workflows/release-windows.yml) on GitHub-hosted runners.
   No binary built on a developer PC is signed.
3. The workflow submits the unsigned binaries to SignPath. Every signing request is approved by hand.
4. The signed binaries are packaged and attached to the release.

## Team roles

| Role | Member |
|---|---|
| Committers and reviewers | [Johan Wilhelm van Antwerp](https://github.com/AntwerpDesignsIonity) |
| Approvers (release signing) | [Johan Wilhelm van Antwerp](https://github.com/AntwerpDesignsIonity) |

Changes from other contributors arrive as pull requests and are reviewed by a committer before they are merged.
All team members use multi-factor authentication for GitHub and SignPath.

## Privacy policy

This program will not transfer any information to other networked systems unless specifically requested by the user or the
person installing or operating it.

FANZi has no telemetry, analytics or account requirement. Sensor readings, settings, logs and AEDi learning data stay on the
PC (`%APPDATA%\FANZI`). It connects to the network only for these functions; the update check (which can be switched off) and the OpenRGB
download are the two that start without a click, and neither sends data about the user or the PC:

| Function | Connects to | When |
|---|---|---|
| Update check | `api.github.com` (latest release of the FANZi repository; a plain request, nothing about the PC is sent) | Automatically 30 s after start and every 12 hours — can be switched off in Settings → Updates — and on *Check now* |
| Update download | `github.com` release assets (the Windows program file and `SHA256SUMS.txt`) | Only when the user presses *Update now* |
| OpenRGB download | `openrgb.org` / `codeberg.org` | Automatically when RGB is switched on and OpenRGB is not installed, and on *Install OpenRGB* |
| PawnIO driver download | `api.github.com` / `github.com` (namazso/PawnIO.Setup) | When the user presses *Install PawnIO* |
| OpenRGB SDK, MAXSUN Sync | `127.0.0.1`, or the host the user enters | While RGB is on |
| Google sign-in, profile backup | Google OAuth and Google Drive | Only after the user signs in |
| Network tab (port scan, ping) | The hosts the user enters | On the user's request |

## Reporting a problem

A signed FANZi binary that behaves maliciously, or a signature on a file that is not from this project: open an issue at
https://github.com/AntwerpDesignsIonity/FANZi/issues or write to info@ionity.today.
