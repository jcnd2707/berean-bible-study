# Deploy scripts

These scripts are a **reference** for one way to run all three apps continuously on a single
Windows machine — the setup this project itself uses — not a general-purpose installer. Read
[finish-iis-setup.ps1](finish-iis-setup.ps1) and [install-agent-service.ps1](install-agent-service.ps1)
before running them; each one explains what it does and why at the top of the file. Both
`#Requires -RunAsAdministrator` and must be launched from an elevated PowerShell.

See the main [README's Deployment section](../README.md#deployment) for why there are two
different hosting mechanisms (IIS for the two APIs and the web app, a Windows Service for the
Agent) instead of one.

## Parameters

Both scripts default to this repo's own layout (`D:\Bible Study\...`, ports 5121/5050/4200, IIS
site names `Resource API`/`BibleAgent`/`Berean Web`). Override them for a different machine:

```powershell
.\deploy\finish-iis-setup.ps1 `
    -InstallRoot 'D:\wherever' -ResourceSiteName 'Resource API' -AgentSiteName 'BibleAgent' `
    -WebSiteName 'Berean Web' -ResourcePort 5121 -AgentPort 5050 -WebPort 4200

.\deploy\install-agent-service.ps1 -PublishDir <folder> `
    -InstallRoot 'D:\wherever' -ServiceName 'BereanAgent' -IisSiteName 'BibleAgent' -Port 5050
```

`finish-iis-setup.ps1` assumes the `-ResourceSiteName`/`-AgentSiteName` IIS sites already exist
with builds published to `<InstallRoot>\ResourceAPI` and `<InstallRoot>\Agent`; it creates the
web site itself. `install-agent-service.ps1` assumes an existing `-IisSiteName` site to retire.

## web.config

[web/web.config](web/web.config) is the static-files + URL-Rewrite config for the `berean-web`
IIS site (SPA fallback to `index.html`). Copy it into the published `berean-web/dist` output;
it isn't parameterized since it has no machine-specific values.
