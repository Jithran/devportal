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

If script execution is disabled (the Windows default), run it with a process-scoped bypass:
`powershell -ExecutionPolicy Bypass -File .\scripts\install.ps1`.

The script publishes a Release build and registers the service `DevPortal` with **Automatic (Delayed
Start)**. If the service crashes, Windows restarts it after 5 s, 5 s and then 60 s. Running the script again
upgrades the installation in place.

To remove it:

```powershell
.\scripts\uninstall.ps1               # stop, delete the service and firewall rule, remove the install directory
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

DevPortal is meant to be viewable **only on this machine**. Each layer below is enforced in code or by the
installer, and was checked against the running service.

1. **Loopback-only bind.** Kestrel listens on `127.0.0.1:7800` and `[::1]:7800` explicitly. It never
   listens on `0.0.0.0`, `[::]`, `*` or `+`. Check with
   `Get-NetTCPConnection -LocalPort 7800 -State Listen`: only the two loopback addresses are listed.
2. **DNS-rebinding protection.** Every request must carry a `Host` header of exactly `localhost:7800`,
   `127.0.0.1:7800` or `[::1]:7800`. Anything else gets **403**. Without this, a malicious website could point
   its own domain at 127.0.0.1 and read `/api/instances` through your browser.
3. **No CORS, same-origin API.** No CORS headers are ever sent. On `/api/*`, an `Origin` header that is not one
   of the three loopback origins gets **403**, and so does a `Sec-Fetch-Site` other than `same-origin`/`none`.
4. **Security headers** on every response: `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`,
   `Content-Security-Policy: default-src 'self'; img-src 'self' data:; object-src 'none'; base-uri 'none';
   form-action 'none'; frame-ancestors 'none'`, `Referrer-Policy: no-referrer`,
   `Cross-Origin-Resource-Policy: same-origin` and `Cross-Origin-Opener-Policy: same-origin`. The `Server`
   header is removed.
5. **Read-only by design.** The only endpoints are `GET /api/instances`, `GET /health` and the static page.
   Any method other than GET/HEAD gets **405**. There is no kill-process, no Docker action and no
   configuration endpoint, and none should ever be added: the Docker client only calls
   `GET /containers/json`. Page titles from probed apps are inserted as text and never as HTML.
6. **Windows Firewall.** The installer never adds an allow rule. It adds an explicit inbound **block** rule,
   `DevPortal-Block-Inbound` (TCP 7800, all profiles), and `uninstall.ps1` removes it. Loopback traffic is
   exempt from firewall filtering, so local access is unaffected.
7. **Secrets in command lines are masked** before they leave the service, in the API and in the UI alike.
   This covers `key=value`/`key: value` and `--flag value` where the key contains password, passwd, pwd,
   secret, token, api-key, access-key, private-key, credential or auth (so `set DB_PWD=…&&` and
   `Password=` inside connection strings are caught too). It also covers `Bearer`/`Basic` credentials and
   `user:password@` in URLs. Masking errs on the side of hiding too much. **The raw command line is not
   available at all**, because no "show raw" switch exists. A raw endpoint would be the one thing worth
   attacking, and the masked form is enough to tell instances apart.
8. **Service account: LocalSystem.** See the trade-off below. The service runs nothing user-supplied:
   there is no `Process.Start` anywhere, git metadata is parsed from files instead of running `git`, and
   configuration is read only from the install directory (`C:\Program Files\DevPortal`, writable only by
   administrators) and the service's own registry environment. Its only outbound traffic is a `GET /` to
   local listening ports (`User-Agent: DevPortal/1.0`) to read status codes and titles.

### Service account: LocalSystem (trade-off)

| Account | Command line / working dir of your processes | Elevated & other users' processes | Docker pipe | Notes |
|---|---|---|---|---|
| LocalSystem | yes | yes | yes (pipe ACL grants SYSTEM) | No password to manage |
| Your own user account | yes | no | yes (owner + `docker-users`) | Stores your password in the SCM; breaks when the password changes |
| LocalService / NetworkService | no | no | no (not in the pipe ACL) | Project mapping and Docker would not work |

Reading another process's working directory needs `PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ`, and
reading its command line through WMI needs the same rights. Only the owner, SYSTEM and Administrators have
those rights by default, so the low-privilege built-in accounts cannot do the main job. LocalSystem is the
highest-privilege account. That is acceptable here only because of the layers above: loopback-only,
read-only, no execution of anything user-supplied, and no inputs other than the `all` query flag.
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
