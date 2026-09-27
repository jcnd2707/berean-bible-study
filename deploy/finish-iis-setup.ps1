<#
  One-time IIS setup for the Berean sites. Run in an ELEVATED PowerShell:

      powershell -ExecutionPolicy Bypass -File .\deploy\finish-iis-setup.ps1

  Assumes the two API sites already exist and the new builds are in their folders:
      "Resource API"  http://localhost:5121   D:\Bible Study\ResourceAPI
      "BibleAgent"    http://localhost:5050   D:\Bible Study\Agent
  and creates the web app site (static files in D:\Bible Study\Web, built from berean-web):
      "Berean Web"    http://localhost:4200   D:\Bible Study\Web

  What it does (IIS configuration needs administrator rights, which is why it is a script):
    1. Runs the BibleAgent app pool as YOUR Windows account. The Claude Code provider reads your
       Claude login from your user profile, which the default pool identity cannot see.
    2. Stops both pools from idling out after 20 minutes. The Agent keeps the search index in
       memory, and the default would unload it (and reload it on the next question).
    3. Creates the "Berean Web" site (port 4200) if it doesn't exist.
    4. Starts everything and checks that each site answers.
#>
#Requires -RunAsAdministrator
Import-Module WebAdministration
$ErrorActionPreference = 'Stop'

$resourceSite = 'Resource API'; $agentSite = 'BibleAgent'
$resourcePool = (Get-Item "IIS:\Sites\$resourceSite").applicationPool
$agentPool    = (Get-Item "IIS:\Sites\$agentSite").applicationPool

# 1. Agent pool identity -------------------------------------------------------------------
# The password is checked against Windows BEFORE it is applied: a wrong one would disable the pool
# (and take the Agent down) the moment IIS tried to start it.
Add-Type -AssemblyName System.DirectoryServices.AccountManagement
function Test-LocalCredential($cred) {
    $ctx = New-Object System.DirectoryServices.AccountManagement.PrincipalContext('Machine', $env:COMPUTERNAME)
    $user = ($cred.UserName -split '\\')[-1]
    return $ctx.ValidateCredentials($user, $cred.GetNetworkCredential().Password)
}

$identityOk = $false
for ($try = 1; $try -le 3 -and -not $identityOk; $try++) {
    Write-Host "Enter the Windows account that is logged in to Claude Code and its Windows password (attempt $try of 3; Cancel to skip)."
    $cred = Get-Credential -UserName "$env:USERDOMAIN\$env:USERNAME" -Message 'Account for the BibleAgent app pool'
    if (-not $cred) { break }
    if (Test-LocalCredential $cred) { $identityOk = $true } else { Write-Host 'Windows rejected that password.' -ForegroundColor Yellow }
}

if ($identityOk) {
    Set-ItemProperty "IIS:\AppPools\$agentPool" -Name processModel -Value @{
        identityType = 'SpecificUser'
        userName     = $cred.UserName
        password     = $cred.GetNetworkCredential().Password
        loadUserProfile = $true
        idleTimeout  = [TimeSpan]::Zero
    }
    Write-Host "BibleAgent now runs as $($cred.UserName)." -ForegroundColor Green
} else {
    # Put the pool back on its normal identity so the Agent works (local Ollama models only).
    Set-ItemProperty "IIS:\AppPools\$agentPool" -Name processModel.identityType -Value 'ApplicationPoolIdentity'
    Write-Host 'Identity NOT changed: the Agent runs as the default pool identity, so Claude Code models cannot work (Ollama models can).' -ForegroundColor Yellow
}

# 2. Keep both apps loaded ------------------------------------------------------------------
foreach ($pool in $resourcePool, $agentPool) {
    Set-ItemProperty "IIS:\AppPools\$pool" -Name processModel.idleTimeout -Value ([TimeSpan]::Zero)
    Set-ItemProperty "IIS:\AppPools\$pool" -Name startMode -Value 'AlwaysRunning'
}
foreach ($site in $resourceSite, $agentSite) {
    Set-ItemProperty "IIS:\Sites\$site\" -Name applicationDefaults.preloadEnabled -Value $true
}

# 3. The web app site ------------------------------------------------------------------------
$webSite = 'Berean Web'; $webPath = 'D:\Bible Study\Web'
if (-not (Test-Path "IIS:\AppPools\$webSite")) {
    New-WebAppPool -Name $webSite | Out-Null
    Set-ItemProperty "IIS:\AppPools\$webSite" -Name managedRuntimeVersion -Value ''   # static files only: no .NET
}
if (-not (Get-Website -Name $webSite)) {
    if (Get-WebBinding -Port 4200) { throw 'Port 4200 is already bound by another site; stop it or change the port (and Cors origins).' }
    New-Website -Name $webSite -PhysicalPath $webPath -ApplicationPool $webSite -Port 4200 | Out-Null
}
$webPool = (Get-Item "IIS:\Sites\$webSite").applicationPool

# 4. Start and check ------------------------------------------------------------------------
foreach ($pool in $resourcePool, $agentPool, $webPool) {
    if ((Get-WebAppPoolState $pool).Value -ne 'Started') { Start-WebAppPool $pool }
}
foreach ($site in $resourceSite, $agentSite, $webSite) {
    if ((Get-WebsiteState $site).Value -ne 'Started') { Start-Website $site }
}

Start-Sleep -Seconds 5
$checks = @(
    @{ Name = 'Resource API'; Url = 'http://localhost:5121/api/resources/profiles/unclassified' },
    @{ Name = 'Agent health'; Url = 'http://localhost:5050/health' },
    @{ Name = 'Agent models'; Url = 'http://localhost:5050/api/models' },
    @{ Name = 'Web app';      Url = 'http://localhost:4200/' },
    @{ Name = 'Web app route'; Url = 'http://localhost:4200/some/deep/link' }   # must fall back to index.html
)
foreach ($c in $checks) {
    try {
        $r = Invoke-WebRequest $c.Url -UseBasicParsing -TimeoutSec 60
        Write-Host ("OK   {0,-13} {1}  ({2} bytes)" -f $c.Name, $r.StatusCode, $r.Content.Length) -ForegroundColor Green
    } catch {
        Write-Host ("FAIL {0,-13} {1}" -f $c.Name, $_.Exception.Message) -ForegroundColor Red
    }
}
Write-Host "`nThe first chat opens the index and migrates it (a backup is written next to it first)."
