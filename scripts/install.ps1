#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Publishes DevPortal and registers it as an auto-start (delayed) Windows service.
.DESCRIPTION
    Re-running the script upgrades an existing installation in place.
.PARAMETER InstallDir
    Where the published binaries go. Defaults to "$env:ProgramFiles\DevPortal".
.PARAMETER Port
    Overrides the listening port (stored as the service environment variable DevPortal__Port).
.PARAMETER NoBuild
    Skip "dotnet publish" and register whatever is already in InstallDir.
#>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'DevPortal'),
    [int]$Port = 0,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$serviceName = 'DevPortal'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src\DevPortal\DevPortal.csproj'
$exe = Join-Path $InstallDir 'DevPortal.exe'

$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($existing -and $existing.Status -ne 'Stopped') {
    Write-Host "Stopping $serviceName..."
    Stop-Service -Name $serviceName -Force
    $existing.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
}

if (-not $NoBuild) {
    Write-Host "Publishing to $InstallDir..."
    dotnet publish $project -c Release -r win-x64 --self-contained false -o $InstallDir --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
}
if (-not (Test-Path $exe)) { throw "$exe not found; run without -NoBuild first." }

if ($existing) {
    sc.exe config $serviceName binPath= "`"$exe`"" | Out-Null
} else {
    New-Service -Name $serviceName -BinaryPathName "`"$exe`"" -DisplayName 'DevPortal' `
        -Description 'Lists local web instances (host processes and Docker containers) on http://localhost.' `
        -StartupType Automatic | Out-Null
}

sc.exe config $serviceName start= delayed-auto | Out-Null
sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/5000/restart/60000 | Out-Null

$serviceKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName"
if ($Port -gt 0) {
    Set-ItemProperty -Path $serviceKey -Name Environment -Type MultiString -Value @("DevPortal__Port=$Port")
}
$environment = (Get-ItemProperty -Path $serviceKey -Name Environment -ErrorAction SilentlyContinue).Environment
$effectivePort = ($environment | Where-Object { $_ -like 'DevPortal__Port=*' } | ForEach-Object { ($_ -split '=', 2)[1] }) | Select-Object -First 1
if (-not $effectivePort) { $effectivePort = 7800 }

# Belt and braces next to the loopback-only bind: never an allow rule, always an explicit inbound block.
# Loopback traffic is exempt from Windows Firewall filtering, so local access keeps working.
$firewallRule = 'DevPortal-Block-Inbound'
Remove-NetFirewallRule -Name $firewallRule -ErrorAction SilentlyContinue
New-NetFirewallRule -Name $firewallRule -DisplayName "DevPortal - block inbound TCP $effectivePort" `
    -Description 'DevPortal is local-only; blocks any inbound connection to its port from other hosts.' `
    -Direction Inbound -Action Block -Protocol TCP -LocalPort $effectivePort -Profile Any | Out-Null

Start-Service -Name $serviceName

$url = "http://localhost:$effectivePort"
foreach ($attempt in 1..30) {
    try {
        Invoke-RestMethod "$url/health" -TimeoutSec 2 | Out-Null
        Write-Host "DevPortal is running at $url"
        return
    } catch {
        Start-Sleep -Seconds 1
    }
}
throw "Service started but $url/health did not answer within 30 seconds. Check the Application event log (source: DevPortal)."
