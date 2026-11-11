# Changelog

All notable changes to DevPortal are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
and the project uses [Semantic Versioning](https://semver.org/). Release notes for each version are cut from its section.

## [Unreleased]

- **Added:** a right rail on the dashboard with *Needs attention* (failing health checks, databases and other listeners bound to all interfaces, projects that stopped, slow responses, high CPU or memory, Docker unreachable), *Ports by type* and the median response time of all web endpoints. Each item opens its project or port; everything except a failing health check can be dismissed for the session. The health ring and the rail always agree on warnings and failures (DP-10).

- **Changed:** the portal page has a new layout: a summary strip (health, projects, web endpoints, containers, listening ports, last scan), tabs for *Projects* and *All ports*, and status and type filters next to search (DP-2). The *All ports* tab is a placeholder for now.
- **Changed:** the *Projects* tab is now a table with one row per project (status, branch, compose project, endpoint ports coloured by probe result, uptime). Expand a row to see its web endpoints with Open and Copy URL, and its other ports (DP-3).
- **Added:** the *Other ports* table in an expanded project row names the service behind each non-web port (MariaDB/MySQL, PostgreSQL, Redis/Dragonfly, MongoDB, Kafka, RabbitMQ, Mailpit/SMTP, debuggers and more) from its container image or process name, with a port-number guess marked with a question mark. It shows what the port is bound to, flags listeners that are open to the whole network, and has a button that copies a connection string without credentials (DP-4).
- **Added:** the *All ports* tab lists every listening port in one table (port, service, project, process or container, bound to, status, connect). Click a column header, or press Enter or Space on it, to sort; the status and type filters (now with *Database*) and the search box apply to both tabs and are remembered across reloads. Ports that belong to no project are marked *No project*. The *on 0.0.0.0* count in the summary strip now includes Docker ports published on all interfaces (DP-5).
- **Added:** every web endpoint shows its last response time and a sparkline of its recent probes (a red tick marks a probe that got no response); the summary strip shows the median (p50) response time and the duration of recent scans. History is in memory only (60 samples per endpoint, one per probe, about every 30 s) and is dropped when a port goes away or a new process takes it (DP-6).
- **Added:** CPU and memory per project, as sparklines with the latest value in the *Projects* table, and per process or container in a new *Processes & containers* table in the expanded row. CPU is percent of one core (like `docker stats`), sampled on every scan from Docker's one-shot stats and from Windows process counters; memory is the working set for processes and memory without page cache for containers. A value shows a dash until a second sample exists, and for processes DevPortal may not read (DP-7).

## v1.0.3 — 2026-10-13

- **Fixed:** an update or restart could keep the portal unreachable for about 30 seconds while DevPortal waited for a hanging request to finish. It now stops within 2 seconds.
- **Added:** an open portal page shows *DevPortal is restarting or stopped. Reconnecting…* while it cannot reach DevPortal, checks every second, and reloads by itself once DevPortal is back.

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
