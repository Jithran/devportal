# Changelog

All notable changes to DevPortal are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
and the project uses [Semantic Versioning](https://semver.org/). Release notes for each version are cut from its section.

## [Unreleased]

## v1.0.2 — 2026-10-13

- **Fixed:** the instance list stayed empty when a local server sent an HTML response without an end and kept the connection open (JetBrains Toolbox does this). Each probe now stops at its timeout, including while reading the page title.

## v1.0.1 — 2026-10-13

- **Fixed:** the portal no longer lists its own port under *Other processes*.

## v1.0.0 — 2026-10-13

First release.

- **Added:** one page at http://localhost:7800 with every web instance on this PC: host processes and Docker containers, grouped by project, branch and worktree, with search, Open/Copy buttons, a system-listener toggle and a dark/light theme.
- **Added:** system tray app. Its menu shows the status and the instance count, and has Open portal, Restart, Check for updates, Release notes, Settings, Start with Windows, About and Quit.
- **Added:** in-app updates through GitHub releases (Velopack). DevPortal checks every 6 hours, shows the release notes and only installs an update when you click Install.
- **Added:** settings dialog for the port, the refresh interval, showing system listeners by default, the theme and automatic update checks. The settings are stored in `%LOCALAPPDATA%\DevPortal\settings.json` and are kept across updates.
- **Added:** logo, tray and app icons, a favicon, a page header with summary counts, and a footer with the version and links.
- **Changed:** DevPortal now runs as a per-user tray app instead of a LocalSystem Windows service. It installs without admin rights and starts when you sign in. Command lines of SYSTEM-owned processes are no longer shown; your own dev processes and Docker are unaffected.
- **Security:** binds to 127.0.0.1 and ::1 only, which is not configurable. The web page is read-only, with a Host allowlist against DNS rebinding, a same-origin API, security headers and masked secrets in command lines. Server control and settings are only available from the tray app and never over HTTP.
