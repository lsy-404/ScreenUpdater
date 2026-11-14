<#
  Publishes ScreenUpdater and registers it as an elevated logon task.
  Run from an elevated PowerShell.
#>
#Requires -RunAsAdministrator
$ErrorActionPreference = 'Stop'

$dist = Join-Path $PSScriptRoot 'dist'
$exe = Join-Path $dist 'ScreenUpdater.exe'
$taskName = 'ScreenUpdater'

if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
    Stop-ScheduledTask -TaskName $taskName
}
Get-Process ScreenUpdater -ErrorAction SilentlyContinue | Stop-Process -Force

dotnet publish (Join-Path $PSScriptRoot 'ScreenUpdater.csproj') -c Release -o $dist --nologo
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }

$user = [Security.Principal.WindowsIdentity]::GetCurrent().Name
$action = New-ScheduledTaskAction -Execute $exe -WorkingDirectory $dist
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $user
$principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)

Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger `
    -Principal $principal -Settings $settings -Force | Out-Null
Start-ScheduledTask -TaskName $taskName

Write-Host "Task '$taskName' registered and started."
