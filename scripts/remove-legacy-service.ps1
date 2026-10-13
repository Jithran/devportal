#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Stops and removes the DevPortal Windows service, and deletes its install directory.
.PARAMETER InstallDir
    The directory install.ps1 published to. Defaults to "$env:ProgramFiles\DevPortal".
.PARAMETER KeepFiles
    Remove only the service registration and leave the binaries (and any appsettings.Production.json) in place.
#>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'DevPortal'),
    [switch]$KeepFiles
)

$ErrorActionPreference = 'Stop'
$serviceName = 'DevPortal'

$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne 'Stopped') {
        Stop-Service -Name $serviceName -Force
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }
    sc.exe delete $serviceName | Out-Null
    Write-Host "Service $serviceName removed."
} else {
    Write-Host "Service $serviceName is not installed."
}

if (Get-NetFirewallRule -Name 'DevPortal-Block-Inbound' -ErrorAction SilentlyContinue) {
    Remove-NetFirewallRule -Name 'DevPortal-Block-Inbound'
    Write-Host 'Firewall rule DevPortal-Block-Inbound removed.'
}

if (-not $KeepFiles -and (Test-Path $InstallDir)) {
    Remove-Item -Path $InstallDir -Recurse -Force
    Write-Host "Removed $InstallDir."
}
