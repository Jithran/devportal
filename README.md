# DevPortal

A small Windows service that shows every web instance running on this PC on one page at
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

The page refreshes every 5 seconds. It has a search box, a dark/light theme toggle and an Open and a Copy
button for each URL. A **Show system** toggle reveals listeners from Windows services and other system
processes, which are hidden by default. The same data is available as JSON at `/api/instances`; add
`?all=true` to include system listeners.

## Requirements

- Windows 10/11 (x64)
- .NET 10 SDK to build (the ASP.NET Core 10 runtime is enough to run a published build)
- Docker Desktop (optional; without it the page shows a notice and lists host processes only)

## Build and run for development

```powershell
dotnet build
dotnet test
dotnet run --project src/DevPortal
```

As a console app it runs with your own rights, so it cannot read the command line or working directory of
elevated processes or processes owned by other accounts. Those rows show without project mapping.

## Install as a Windows service

From an elevated PowerShell:

```powershell
.\scripts\install.ps1                 # publish to C:\Program Files\DevPortal, register, start
.\scripts\install.ps1 -Port 7900      # use another port
.\scripts\install.ps1 -NoBuild        # re-register the binaries already in the install directory
```

The script publishes a Release build and registers the service `DevPortal` with **Automatic (Delayed
Start)**. If the service crashes, Windows restarts it after 5 s, 5 s and then 60 s. Running the script again
upgrades the installation in place.

To remove it:

```powershell
.\scripts\uninstall.ps1               # stop, delete the service, remove the install directory
.\scripts\uninstall.ps1 -KeepFiles    # keep the binaries and local configuration
```

## Configuration

Settings live in the `DevPortal` section of `appsettings.json`. To keep your changes across upgrades, put
them in `appsettings.Production.json` next to the executable; publishing never overwrites that file.
Environment variables also work, for example `DevPortal__Port=7900`. `install.ps1 -Port` stores that as
an environment variable on the service.

| Key | Default | Meaning |
|---|---|---|
| `Port` | `7800` | Listening port (loopback only) |
| `CacheSeconds` | `3` | How long one scan is reused across requests |
| `ProbeTimeoutMs` | `1500` | Timeout per HTTP/HTTPS probe |
| `ProbeCacheSeconds` | `30` | How long a probe result is reused. After that, the old result is shown while a new probe runs in the background |
| `DockerPipe` | `docker_engine` | Docker Engine named pipe |
| `SystemProcesses` | svchost, lsass, … | Process names treated as system (hidden unless *Show system* is on). Executables under `%WINDIR%` and PIDs 0/4 always count as system |
| `NonHttpPorts` | 3306, 5432, 6379, … | Ports that are never probed (databases, brokers, SMTP) |
| `HiddenPorts` | `[]` | Ports left out entirely |

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
Docker backend owns those sockets.

The process type (dotnet/Kestrel, node, python, php, java) is detected from the process name and path. When
a page has no `<title>`, the probe falls back to `og:title`/`application-name` and then to the first `<h1>`.
If there is still nothing, the row shows the app name, which is the assembly or script for generic hosts
like `dotnet` and `node`. Blazor Server apps without prerendering set the title only on the client, so
they always fall into this case.

## Security

- Kestrel binds **only** to `127.0.0.1` and `[::1]`. Other machines cannot reach it.
- `AllowedHosts` accepts only `localhost`, `127.0.0.1` and `[::1]`, which blocks DNS-rebinding attempts from
  web pages. There is no CORS, and responses send `X-Frame-Options: DENY` and a strict CSP.
- Command lines are shown because they are what tells instances apart. Obvious secrets
  (`password=`, `token=`, `apikey=`, …) are masked, but treat the page as being as sensitive as Task
  Manager.
- Page titles from probed apps are inserted as text and never as HTML.

### Service account: LocalSystem (trade-off)

DevPortal runs as **LocalSystem**. Here is why, and what the alternatives cost:

| Account | Command line / working dir of your processes | Elevated & other users' processes | Docker pipe | Notes |
|---|---|---|---|---|
| LocalSystem | yes | yes | yes (pipe ACL grants SYSTEM) | No password to manage |
| Your own user account | yes | no | yes (owner + `docker-users`) | Stores your password in the SCM; breaks when the password changes |
| LocalService / NetworkService | no (process DACLs grant only the owner, SYSTEM and Administrators) | no | no (not in the pipe ACL) | Project mapping and Docker would not work |

Reading another process's working directory needs `PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ`, and
reading its command line through WMI needs the same rights. Only the owner, SYSTEM and Administrators have
those rights by default, so the low-privilege built-in accounts cannot do the main job.

The risk of running as LocalSystem is reduced as follows. The service only reads: process tables, git files
and the Docker list endpoint. It never starts processes (git metadata is parsed rather than run through
`git`, which also avoids git's *dubious ownership* check against user-owned repositories). It is reachable
only from loopback with a host allowlist. Its only outbound traffic is `GET /` to local ports.

## Troubleshooting

- **Port 7800 is in use**: `Get-NetTCPConnection -LocalPort 7800 -State Listen` shows the owner. Pick another
  port with `install.ps1 -Port <n>`.
- **The service does not start**: check *Event Viewer → Windows Logs → Application*, source `DevPortal`, or run
  `& "$env:ProgramFiles\DevPortal\DevPortal.exe"` from an elevated prompt to see the console output.
- **"Docker unavailable" notice**: Docker Desktop is not running, or the pipe has a different name (set
  `DockerPipe`).
- **A process shows under "Other processes"**: its working directory, command line and executable are all
  outside a git repository, or the process could not be read. Under the console app this happens for
  elevated processes.
- **A row has no title**: the page does not send a `<title>` in its server-rendered HTML (see above), or the
  port does not speak HTTP. The badge shows the status code or the probe error.
- **A dev server logs a request every 30 seconds**: that is the probe (`User-Agent: DevPortal/1.0`). Add the
  port to `NonHttpPorts` to stop it.
