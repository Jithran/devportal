# DevPortal

A small Windows tray app that shows every web instance running on this PC on one page at
**http://localhost:7800**, grouped by project and branch, with links to open them.

It combines three sources:

1. **Host processes.** Every listening TCP port (IPv4 and IPv6) and the process that owns it: name, PID,
   command line, working directory, executable path and start time. Each process is mapped to the git
   repository it runs from.
2. **Docker.** All running containers, read from the Docker Engine API on the named pipe
   `\\.\pipe\docker_engine`: name, image, published ports, status and compose project/service. Containers
   are grouped by compose project.
3. **HTTP probing.** Each port gets a quick `GET /` with a short timeout. The status code and the page title
   show what is running there.

The page refreshes every 5 seconds by default. It has a search box, a dark/light theme toggle, a summary of
instances, projects and containers, and an Open and a Copy button for each URL. A **Show system** toggle
reveals listeners from Windows services and other system processes, which are hidden by default. The same
data is available as JSON at `/api/instances`; add `?all=true` to include system listeners.

## Requirements

- Windows 10/11 (x64)
- Docker Desktop (optional; without it the page shows a notice and lists host processes only)
- To build: the .NET 10 SDK. Releases are self-contained, so running one needs no .NET install.

## Install

Download `DevPortal-win-x64-stable-Setup.exe` from the
[latest release](https://github.com/Jithran/devportal/releases/latest) and run it. It installs for the current
user into `%LOCALAPPDATA%\DevPortal` and needs no administrator rights. It turns on **Start with Windows** and
starts DevPortal; the tray icon appears and the portal is on http://localhost:7800.

To uninstall, use *Settings → Apps → Installed apps → DevPortal*. This also removes the Start with Windows
entry. The uninstaller deletes the whole `%LOCALAPPDATA%\DevPortal` folder, including your settings.

### Moving from the old Windows service (before v1.0.0)

Versions before 1.0.0 ran as a LocalSystem Windows service that was installed with `scripts\install.ps1`. Remove
it once from an elevated PowerShell, then run the Setup.exe:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\remove-legacy-service.ps1
```

The script stops and deletes the `DevPortal` service, removes the `DevPortal-Block-Inbound` firewall rule and
deletes `C:\Program Files\DevPortal`.

## The tray icon

The teal icon means the portal is running. A grey icon means it is stopped, for example because its port is taken;
the tooltip and the menu then say why. Double-click the icon to open the portal. The right-click menu has:

| Item | What it does |
|---|---|
| *DevPortal vX.Y.Z*, status, counts | Version, `Running on localhost:<port>` or `Stopped: <reason>`, and the number of instances and projects |
| **Open portal** | Opens the portal in your browser (also on double-click) |
| Restart server / Start server | Stops and restarts the web server inside the app |
| Check for updates… | Checks GitHub. If there is an update, shows its release notes with *Install and restart* |
| Install update vX and restart… | Replaces *Check for updates* once an update has been found |
| Release notes | What's new in the running version |
| Settings… | See [Settings](#settings) |
| Start with Windows | Starts DevPortal when you sign in (per-user Run entry, no admin rights) |
| About DevPortal | Version and the program, settings and log paths |
| Quit DevPortal | Stops the server and removes the tray icon |

Starting DevPortal a second time does not start a second copy. It opens the portal of the copy that is
already running.

## Settings

*Settings…* opens a small native dialog. It is deliberately not a web page, so the HTTP side stays read-only:
there is nothing on the portal that can change anything.

| Setting | Default | Notes |
|---|---|---|
| Port | `7800` | 1024–65535. Saving checks that the port is free on both loopback addresses and refuses a port that is taken. The server then restarts on the new port right away. *Open portal* and the tooltip follow it, and Start with Windows needs no change because the port is read from the settings at startup |
| Refresh every | `5` s | 2–300 s, how often the page reloads its data |
| Theme | System | System, Dark or Light. This is the default for the page. The page's Theme button still overrides it for that browser, until you change this setting again |
| Show system listeners by default | off | The default for the page's *Show system* checkbox |
| Check for updates automatically | on | Checks at startup and every 6 hours. It notifies you and never installs anything on its own |

Settings are stored per user in `%LOCALAPPDATA%\DevPortal\settings.json`. Velopack updates replace only the
`current` folder below it, so the file is kept across updates. A file that cannot be read or has values out of
range is ignored and the defaults are used; the log says so.

**The listen address is not a setting.** DevPortal always binds to `127.0.0.1` and `[::1]` and nothing else,
because that is hard-coded. There is no way to expose it on the network through configuration.

### Advanced options

The lists below live in `appsettings.json` in the program folder. Updates overwrite that file.

| Key | Default | Meaning |
|---|---|---|
| `CacheSeconds` | `3` | How long one scan is reused across requests |
| `ProbeTimeoutMs` | `1500` | Timeout per HTTP/HTTPS probe |
| `ProbeCacheSeconds` | `30` | How long a probe result is reused. After that, the old result is shown while a new probe runs in the background |
| `DockerPipe` | `docker_engine` | Docker Engine named pipe |
| `SystemProcesses` | svchost, lsass, … | Process names treated as system (hidden unless *Show system* is on). Processes in session 0 (services), executables under `%WINDIR%` and PIDs 0/4 always count as system |
| `NonHttpPorts` | 3306, 5432, 6379, … | Ports that are never probed (databases, brokers, SMTP) |
| `HiddenPorts` | `[]` | Ports left out entirely |

## Updates and releases

DevPortal updates itself with [Velopack](https://velopack.io) from the
[GitHub releases](https://github.com/Jithran/devportal/releases) of this public repository, on the channel
`win-x64-stable`. An update is downloaded and applied only after you click *Install and restart*. Updates are
per-user and need no administrator rights. A copy you run from a build folder (`dotnet run`) is not
installed, so it says it cannot update itself.

Versions follow [Semantic Versioning](https://semver.org). To cut a release:

1. Move the entries under `## [Unreleased]` in `CHANGELOG.md` to a new `## vX.Y.Z — YYYY-MM-DD` section and commit.
2. `git tag vX.Y.Z && git push origin vX.Y.Z`

The **Release** workflow (`.github/workflows/release.yml`) then:

- takes the version from the tag and the release notes from that CHANGELOG section (it fails if the section is empty);
- runs the tests;
- publishes a self-contained win-x64 build;
- runs `vpk pack`, with a delta against the previous release;
- publishes the GitHub release with the Setup.exe, the full and delta packages and the `releases.win-x64-stable.json` feed.

## Build and run for development

```powershell
dotnet build
dotnet test
dotnet run --project src/DevPortal
```

It shares the settings and log in `%LOCALAPPDATA%\DevPortal` with an installed copy. Only one copy can run at a
time, so quit the installed one first, or give the dev copy another port in Settings.

`scripts\New-Icons.ps1` renders the logo into `src/DevPortal/Assets/devportal.ico`, `devportal-stopped.ico` and
`wwwroot/favicon.ico` (16–256 px). `wwwroot/favicon.svg` is the vector master.

## How project mapping works

For each listening process, DevPortal collects candidate paths in this order:

1. the process's **working directory** (read from its PEB with `NtQueryInformationProcess`),
2. paths named by project flags in the command line (`--project`, `-p`, `--app-dir`, `--chdir`, `--cwd`,
   `--prefix`, `-t`, `--root`),
3. other arguments that look like paths (`vite.js`, `app.py`, `Api.dll`, …), with relative ones resolved
   against the working directory,
4. the **executable path** (this covers `bin\Debug\net10.0\App.exe` started from Visual Studio or Rider).

From the first candidate that has one, it walks up to the nearest `.git`. DevPortal reads git metadata
straight from the `.git` files and does not run `git`:

- **Repo name** comes from the `origin` remote URL, or the folder name if there is no `origin`.
- **Branch and commit** come from `HEAD`, the loose refs and `packed-refs`.
- **Worktrees**: a `.git` *file* that points to `<main>/.git/worktrees/<name>` is recognised. The instance
  is grouped under the main repository, and its version row shows the branch and the worktree name.

Compose containers are grouped by `com.docker.compose.project`. The version row shows the branch of the
compose working directory. Host listeners on a port that Docker publishes are not shown twice, because the
Docker backend owns those sockets. DevPortal leaves out its own port.

The process type (dotnet/Kestrel, node, python, php, java) is detected from the process name and path. When
a page has no `<title>`, the probe falls back to `og:title`/`application-name` and then to the first `<h1>`.
If there is still nothing, the row shows the app name, which is the assembly or script for generic hosts
like `dotnet` and `node`. Blazor Server apps without prerendering set the title only on the client, so
they always fall into this case.

### What a per-user app can see

DevPortal runs with your own, non-elevated rights. That covers what it is for: your own dev processes (command
line, working directory, executable) and Docker (the pipe allows the `docker-users` group). Measured against the
old LocalSystem service on the same machine, all repo-mapped dev instances and all containers were identical.
What it cannot read:

- the command line and paths of **services and other users' processes** (svchost, VPN and vendor services). They
  run in session 0, so they are classed as system and hidden unless *Show system* is on.
- processes you started **as administrator** (an elevated IDE or terminal). They show under *Other processes*
  without project mapping. Start dev servers non-elevated to get them mapped.

## Security

DevPortal is meant to be viewable **only on this machine**. Each layer below is enforced in code and covered by
tests or checked against the running app.

1. **Loopback-only bind.** Kestrel listens on `127.0.0.1:<port>` and `[::1]:<port>` explicitly. It never
   listens on `0.0.0.0`, `[::]`, `*` or `+`. Only the port is configurable. Check with
   `Get-NetTCPConnection -LocalPort 7800 -State Listen`: only the two loopback addresses are listed.
2. **DNS-rebinding protection.** Every request must carry a `Host` header of exactly `localhost:<port>`,
   `127.0.0.1:<port>` or `[::1]:<port>`. Anything else gets **403**. Without this, a malicious website could point
   its own domain at 127.0.0.1 and read `/api/instances` through your browser.
3. **No CORS, same-origin API.** No CORS headers are ever sent. On `/api/*`, an `Origin` header that is not one
   of the three loopback origins gets **403**, and so does a `Sec-Fetch-Site` other than `same-origin`/`none`.
4. **Security headers** on every response: `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`,
   `Content-Security-Policy: default-src 'self'; img-src 'self' data:; object-src 'none'; base-uri 'none';
   form-action 'none'; frame-ancestors 'none'`, `Referrer-Policy: no-referrer`,
   `Cross-Origin-Resource-Policy: same-origin` and `Cross-Origin-Opener-Policy: same-origin`. The `Server`
   header is removed.
5. **Read-only by design.** The only endpoints are `GET /api/instances`, `GET /api/config` (the version and the
   page's display defaults), `GET /health` and the static page. Any method other than GET/HEAD gets **405**.
   There is no kill-process, no Docker action and no configuration endpoint, and none should ever be added: the
   Docker client only calls `GET /containers/json`. Page titles from probed apps are inserted as text and never
   as HTML.
6. **Control stays in the tray.** Start, stop, restart, quit, settings and updates are menu actions inside the
   tray process. They are never HTTP endpoints. The only inter-process signal is a named event that a second
   launch uses to say "open the portal". It carries no data.
7. **Secrets in command lines are masked** before they leave the server, in the API and in the UI alike.
   This covers `key=value`/`key: value` and `--flag value` where the key contains password, passwd, pwd,
   secret, token, api-key, access-key, private-key, credential or auth (so `set DB_PWD=…&&` and
   `Password=` inside connection strings are caught too). It also covers `Bearer`/`Basic` credentials and
   `user:password@` in URLs. Masking errs on the side of hiding too much. **The raw command line is not
   available at all**, because no "show raw" switch exists. A raw endpoint would be the one thing worth
   attacking, and the masked form is enough to tell instances apart.
8. **Least privilege.** DevPortal runs as you, not as LocalSystem, and needs no admin rights to install, update
   or run. It runs nothing user-supplied: git metadata is parsed from files instead of running `git`, and the
   only processes it starts are your browser (Open portal and links) and Velopack's updater. Its outbound
   traffic is a `GET /` to local listening ports (`User-Agent: DevPortal/1.0`) and, for updates, the GitHub
   releases API.

There is no Windows Firewall rule any more. The old service added an inbound block rule as defence in depth, but a
per-user app cannot manage firewall rules without admin rights. Loopback-only binding is the control that
matters: nothing listens on a network interface, and Windows does not apply firewall filtering to loopback traffic.

## Troubleshooting

- **The icon is grey / "Stopped: port 7800 is in use"**: another program holds the port.
  `Get-NetTCPConnection -LocalPort 7800 -State Listen` shows the owner. Pick another port in *Settings…*.
- **Something went wrong**: see `%LOCALAPPDATA%\DevPortal\devportal.log` (warnings, errors, start/stop and update
  checks).
- **"Docker unavailable"**: Docker Desktop is not running, or the pipe has a different name (set `DockerPipe`).
- **A process shows under "Other processes"**: its working directory, command line and executable are all
  outside a git repository, or the process runs elevated and could not be read (see above).
- **A row has no title**: the page does not send a `<title>` in its server-rendered HTML (see above), or the
  port does not speak HTTP. The badge shows the status code or the probe error.
- **A dev server logs a request every 30 seconds**: that is the probe (`User-Agent: DevPortal/1.0`). Add the
  port to `NonHttpPorts` to stop it.
