<#
  Moves the Agent from IIS to a Windows Service that runs as YOUR account. Run ELEVATED:

      powershell -ExecutionPolicy Bypass -File .\deploy\install-agent-service.ps1 [-PublishDir <folder>]

  Why: the Claude Code provider shells out to the claude CLI, which reads your Claude login from
  your user profile. A service running as your account gets your profile environment from the
  Service Control Manager; an IIS worker process does not, so claude reported "Not logged in".

  What it does:
    1. Asks for your Windows password and checks it before using it.
    2. Stops the IIS "BibleAgent" site and pool, stops them auto-starting, and puts the pool back on
       its default identity (so IIS no longer stores your password).
    3. Copies the new build into D:\Bible Study\Agent (keeps the deployed appsettings.json).
    4. Grants your account "Log on as a service" and creates the "BereanAgent" service
       (automatic start, restarts on failure). The app listens on http://*:5050 ("Urls" in appsettings.json).
    5. Starts it and checks http://localhost:5050/health.
#>
#Requires -RunAsAdministrator
param([string]$PublishDir)
$ErrorActionPreference = 'Stop'

$serviceName = 'BereanAgent'
$agentDir    = 'D:\Bible Study\Agent'
$exe         = Join-Path $agentDir 'Berean.Agent.Api.exe'
$iisSite     = 'BibleAgent'

# 1. Credentials ------------------------------------------------------------------------------
Add-Type -AssemblyName System.DirectoryServices.AccountManagement
function Test-LocalCredential($cred) {
    $ctx  = New-Object System.DirectoryServices.AccountManagement.PrincipalContext('Machine', $env:COMPUTERNAME)
    $user = ($cred.UserName -split '\\')[-1]
    return $ctx.ValidateCredentials($user, $cred.GetNetworkCredential().Password)
}
$cred = $null
for ($try = 1; $try -le 3; $try++) {
    Write-Host "Enter your Windows password (the account logged in to Claude Code). Attempt $try of 3."
    $c = Get-Credential -UserName "$env:COMPUTERNAME\$env:USERNAME" -Message 'Account the BereanAgent service runs as'
    if (-not $c) { break }
    if (Test-LocalCredential $c) { $cred = $c; break }
    Write-Host 'Windows rejected that password.' -ForegroundColor Yellow
}
if (-not $cred) { throw 'No valid credential; nothing was changed.' }

# 2. Retire the IIS site --------------------------------------------------------------------------
Import-Module WebAdministration
if (Get-Website -Name $iisSite) {
    $pool = (Get-Item "IIS:\Sites\$iisSite").applicationPool
    if ((Get-WebsiteState $iisSite).Value -ne 'Stopped') { Stop-Website $iisSite }
    Set-ItemProperty "IIS:\Sites\$iisSite" -Name serverAutoStart -Value $false
    if ((Get-WebAppPoolState $pool).Value -ne 'Stopped') { Stop-WebAppPool $pool }
    Set-ItemProperty "IIS:\AppPools\$pool" -Name autoStart -Value $false
    Set-ItemProperty "IIS:\AppPools\$pool" -Name startMode -Value 'OnDemand'
    Set-ItemProperty "IIS:\AppPools\$pool" -Name processModel.identityType -Value 'ApplicationPoolIdentity'
    # wait for the worker process to release the files
    for ($i = 0; $i -lt 30 -and (Get-WebAppPoolState $pool).Value -ne 'Stopped'; $i++) { Start-Sleep 1 }
    Start-Sleep 2
    Write-Host "IIS site '$iisSite' stopped and set not to auto-start." -ForegroundColor Green
}

# stop an existing service before replacing its files
$svc = Get-Service $serviceName -ErrorAction SilentlyContinue
if ($svc -and $svc.Status -ne 'Stopped') { Stop-Service $serviceName -Force; $svc.WaitForStatus('Stopped', '00:00:30') }

# 3. Files ------------------------------------------------------------------------------------------
if ($PublishDir) {
    robocopy $PublishDir $agentDir /E /NFL /NDL /NJH /NP /XF appsettings.json appsettings.Development.json web.config | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE)" }
    Write-Host "Copied new build into $agentDir." -ForegroundColor Green
}
if (-not (Test-Path $exe)) { throw "$exe not found." }

# 4. Service ------------------------------------------------------------------------------------------
# "Log on as a service" right (services.msc grants it automatically; New-Service does not).
$sid = (New-Object System.Security.Principal.NTAccount($cred.UserName)).Translate([System.Security.Principal.SecurityIdentifier]).Value
$tmp = Join-Path $env:TEMP 'berean-rights'
New-Item -ItemType Directory -Force $tmp | Out-Null
secedit /export /cfg "$tmp\cur.inf" /areas USER_RIGHTS | Out-Null
$line = Get-Content "$tmp\cur.inf" | Where-Object { $_ -like 'SeServiceLogonRight*' }
if (-not $line -or $line -notmatch [regex]::Escape("*$sid")) {
    $value = if ($line) { ($line -split '=', 2)[1].Trim() + ",*$sid" } else { "*$sid" }
    @('[Unicode]', 'Unicode=yes', '[Version]', 'signature="$CHICAGO$"', 'Revision=1',
      '[Privilege Rights]', "SeServiceLogonRight = $value") | Set-Content "$tmp\new.inf" -Encoding Unicode
    secedit /configure /db "$tmp\rights.sdb" /cfg "$tmp\new.inf" /areas USER_RIGHTS | Out-Null
    Write-Host 'Granted "Log on as a service".' -ForegroundColor Green
}
Remove-Item $tmp -Recurse -Force

$binPath = "`"$exe`""
if (-not (Get-Service $serviceName -ErrorAction SilentlyContinue)) {
    New-Service -Name $serviceName -DisplayName 'Berean Agent API' -BinaryPathName $binPath `
        -StartupType Automatic -Credential $cred `
        -Description 'Berean study agent (SignalR hub on port 5050). Runs as the user logged in to Claude Code.' | Out-Null
} else {
    $pw = $cred.GetNetworkCredential().Password
    & sc.exe config $serviceName binPath= $binPath start= auto obj= $cred.UserName password= $pw | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "sc.exe config failed ($LASTEXITCODE)" }
}
& sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null
Write-Host "Service '$serviceName' runs as $($cred.UserName)." -ForegroundColor Green

# 5. Start and check ------------------------------------------------------------------------------
Start-Service $serviceName
Start-Sleep 5
try {
    $r = Invoke-WebRequest 'http://localhost:5050/health' -UseBasicParsing -TimeoutSec 60
    Write-Host "OK   Agent health $($r.StatusCode)" -ForegroundColor Green
} catch {
    Write-Host "FAIL Agent health: $($_.Exception.Message)" -ForegroundColor Red
    Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = (Get-Date).AddMinutes(-2) } -MaxEvents 10 -ErrorAction SilentlyContinue |
        Where-Object { $_.ProviderName -match 'Berean|\.NET Runtime|Application Error' } | Format-List TimeCreated, ProviderName, Message
}
