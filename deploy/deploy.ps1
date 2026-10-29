# Updates the live website on a Windows/IIS server in one go.
# Started by Deploy.cmd (or Rollback.cmd with -Rollback). Written for Windows PowerShell 5.1.
#
# What it does: gets the latest code, lists any new database scripts to run in SSMS, builds the site,
# backs up the live folder, swaps the new files in (keeping appsettings.Production.json) and checks the
# site answers. If the copy fails it puts the backup back. Settings are asked once and saved in
# deploy\deploy-settings.json (not in git).

param([switch]$Rollback)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$RepoRoot = Split-Path -Parent $PSScriptRoot
$SettingsFile = Join-Path $PSScriptRoot 'deploy-settings.json'
$KeepBackups = 5

function Say($text, $colour = 'Gray') { Write-Host $text -ForegroundColor $colour }
function Step($text) { Write-Host ''; Write-Host "== $text" -ForegroundColor Cyan }
function Stop-Deploy($text) {
    Write-Host ''
    Write-Host "STOPPED: $text" -ForegroundColor Red
    exit 1
}
function Ask-YesNo($question) {
    while ($true) {
        $answer = (Read-Host "$question (Y/N)").Trim().ToUpperInvariant()
        if ($answer -eq 'Y') { return $true }
        if ($answer -eq 'N') { return $false }
    }
}
function Ask-Text($question, $default) {
    if ($default) { $question = "$question`n  [press Enter for: $default]" }
    $answer = Read-Host $question
    if ([string]::IsNullOrWhiteSpace($answer)) { return $default }
    return $answer.Trim().Trim('"')
}

# Robocopy exit codes below 8 mean success.
function Copy-Folder($from, $to, [string[]]$extra) {
    $options = @($from, $to, '/E', '/R:5', '/W:2', '/NFL', '/NDL', '/NJH', '/NJS', '/NP') + $extra
    & robocopy @options | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Copying $from to $to failed (robocopy code $LASTEXITCODE)." }
}

function Find-Git {
    $onPath = Get-Command git -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    # GitHub Desktop ships its own git.
    $desktop = Join-Path $env:LOCALAPPDATA 'GitHubDesktop'
    if (Test-Path $desktop) {
        $found = Get-ChildItem $desktop -Directory -Filter 'app-*' | Sort-Object Name -Descending |
            ForEach-Object { Join-Path $_.FullName 'resources\app\git\cmd\git.exe' } |
            Where-Object { Test-Path $_ } | Select-Object -First 1
        if ($found) { return $found }
    }
    return $null
}

function Find-SiteFolder {
    $roots = @("$env:SystemDrive\HostingSpaces", "$env:SystemDrive\inetpub") | Where-Object { Test-Path $_ }
    foreach ($root in $roots) {
        $hits = @(Get-ChildItem $root -Recurse -Depth 3 -Filter 'PortfolioManager.Web.dll' -ErrorAction SilentlyContinue |
            Where-Object { $_.DirectoryName -notmatch '\\_deploy\\|\\bin\\|\\obj\\|\\publish' })
        if ($hits.Count -eq 1) { return $hits[0].DirectoryName }
    }
    return ''
}

function Get-Settings {
    if (Test-Path $SettingsFile) { return Get-Content $SettingsFile -Raw | ConvertFrom-Json }

    Step 'First run: a few questions (asked only once)'
    $site = Ask-Text 'Which folder does the live site run from (the one holding appsettings.Production.json)?' (Find-SiteFolder)
    if (-not (Test-Path (Join-Path $site 'appsettings.Production.json'))) {
        Stop-Deploy "No appsettings.Production.json in '$site'. Run Deploy.cmd again and give the live site's folder."
    }
    $backupDefault = Join-Path (Split-Path -Parent (Split-Path -Parent $site)) '_deploy'
    $backups = Ask-Text 'Where should backups and builds go (not inside the site folder)?' $backupDefault
    if ($backups.TrimEnd('\').StartsWith($site.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) {
        Stop-Deploy 'Backups cannot go inside the site folder. Run Deploy.cmd again and pick another folder.'
    }
    $url = Ask-Text 'What is the site address?' 'https://app.shareportfoliomanager.com/'

    $settings = [pscustomobject]@{ SiteFolder = $site; DeployFolder = $backups; SiteUrl = $url }
    $settings | ConvertTo-Json | Set-Content $SettingsFile -Encoding UTF8
    Say "Saved to $SettingsFile. Delete that file to answer again." 'DarkGray'
    return $settings
}

function Set-Offline($site) {
    $html = '<!doctype html><html><head><meta charset="utf-8"><title>Updating</title></head>' +
        '<body style="font-family:sans-serif;text-align:center;padding-top:4em">' +
        '<h1>Updating the site</h1><p>Back in a minute. Please refresh shortly.</p></body></html>'
    Set-Content (Join-Path $site 'app_offline.htm') $html -Encoding UTF8
    # Give IIS a moment to stop the app and let go of its files.
    Start-Sleep -Seconds 5
}

function Set-Online($site) {
    Remove-Item (Join-Path $site 'app_offline.htm') -ErrorAction SilentlyContinue
}

function Test-Site($url) {
    Say "Checking $url ..."
    for ($try = 1; $try -le 12; $try++) {
        try {
            $response = Invoke-WebRequest $url -UseBasicParsing -TimeoutSec 30
            if ($response.StatusCode -eq 200) { return $true }
        } catch { }
        Start-Sleep -Seconds 5
    }
    return $false
}

function Restore-Backup($backup, $site) {
    Set-Offline $site
    # Mirror makes the folder exactly the backup again; app_offline.htm is left alone until the end.
    Copy-Folder $backup $site @('/MIR', '/XF', 'app_offline.htm')
    Set-Online $site
}

# ---------------------------------------------------------------------------

if (-not (Test-Path (Join-Path $RepoRoot 'PortfolioManager.slnx'))) {
    Stop-Deploy ("This copy of Deploy.cmd is not in the code folder from GitHub Desktop ($RepoRoot)." +
        "`nIn GitHub Desktop choose Repository > Show in Explorer, then run deploy\Deploy.cmd from there.")
}

$settings = Get-Settings
$site = $settings.SiteFolder
$deployFolder = $settings.DeployFolder
New-Item -ItemType Directory -Force -Path $deployFolder | Out-Null
$lastDeployFile = Join-Path $deployFolder 'last-deployed-commit.txt'

if ($Rollback) {
    $latest = Get-ChildItem $deployFolder -Directory -Filter 'wwwroot-backup-*' | Sort-Object Name -Descending | Select-Object -First 1
    if (-not $latest) { Stop-Deploy "No backups found in $deployFolder." }
    Step "Put back the backup $($latest.Name)"
    if (-not (Ask-YesNo "Replace the live site with $($latest.Name)? Database changes are not undone")) { Stop-Deploy 'Nothing changed.' }
    Restore-Backup $latest.FullName $site
    if (Test-Site $settings.SiteUrl) { Say 'Done. The site is back on the previous version.' 'Green' }
    else { Say 'The backup is back in place but the site is not answering. Check the Windows Application event log.' 'Yellow' }
    exit 0
}

if ($site.TrimEnd('\').StartsWith($RepoRoot.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) {
    Stop-Deploy 'The site folder is inside the code folder. The site must run from its own folder.'
}

# 1. Latest code -------------------------------------------------------------
Step '1 of 6: Getting the latest code from GitHub'
$git = Find-Git
if (-not $git) { Stop-Deploy 'Could not find git. Open GitHub Desktop, click Fetch origin then Pull, and run Deploy.cmd again.' }
Push-Location $RepoRoot
try {
    $branch = (& $git rev-parse --abbrev-ref HEAD).Trim()
    if ($branch -ne 'main') { Stop-Deploy "The code folder is on branch '$branch'. Switch to main in GitHub Desktop first." }
    $changed = (& $git status --porcelain --untracked-files=no) -join "`n"
    if ($changed) { Stop-Deploy "Files in the code folder were changed by hand:`n$changed`nDiscard them in GitHub Desktop (Changes tab) first." }
    & $git pull --ff-only origin main
    if ($LASTEXITCODE -ne 0) { Stop-Deploy 'Getting the latest code failed. Try Fetch origin and Pull in GitHub Desktop, then run Deploy.cmd again.' }
    $commit = (& $git rev-parse --short HEAD).Trim()
    $title = (& $git log -1 --format=%s).Trim()
    Say "Code is now at $commit ($title)."

    # 2. Database scripts ----------------------------------------------------
    Step '2 of 6: New database scripts'
    $lastCommit = if (Test-Path $lastDeployFile) { (Get-Content $lastDeployFile -Raw).Trim() } else { '' }
    $known = $false
    if ($lastCommit) { & $git rev-parse --verify --quiet "$lastCommit^{commit}" | Out-Null; $known = ($LASTEXITCODE -eq 0) }
    if ($known) {
        $scripts = @(& $git diff --name-only --diff-filter=AM "$lastCommit" HEAD -- 'Database/Migrations/*.sql' | Sort-Object)
        if ($scripts.Count -eq 0) { Say 'None since the last deploy.' 'Green' }
    } else {
        Say 'No record of the last deploy yet, so here are the five newest scripts. Run any you have not run before.' 'Yellow'
        $scripts = @(Get-ChildItem (Join-Path $RepoRoot 'Database\Migrations') -Filter '*.sql' | Sort-Object Name |
            Select-Object -Last 5 | ForEach-Object { "Database/Migrations/$($_.Name)" })
    }
    if ($scripts.Count -gt 0) {
        Say 'Run these in SSMS on the live database now, top to bottom (each is safe to run twice):' 'Yellow'
        foreach ($s in $scripts) { Say ('  ' + (Join-Path $RepoRoot ($s -replace '/', '\'))) 'White' }
        if (-not (Ask-YesNo 'Have they all run without errors?')) { Stop-Deploy 'Nothing on the site was changed. Run Deploy.cmd again once the scripts are done.' }
    }
} finally { Pop-Location }

# 3. Build -------------------------------------------------------------------
Step '3 of 6: Building the site (about a minute)'
$build = Join-Path $deployFolder 'publish'
if (Test-Path $build) { Remove-Item $build -Recurse -Force }
& dotnet publish (Join-Path $RepoRoot 'src\PortfolioManager.Web') -c Release -o $build --nologo -v quiet
if ($LASTEXITCODE -ne 0) { Stop-Deploy 'The build failed (see the messages above). The live site was not touched.' }
# The server's own settings file must never be replaced.
Remove-Item (Join-Path $build 'appsettings.Production.json') -ErrorAction SilentlyContinue
Say 'Built.' 'Green'

# 4. Backup ------------------------------------------------------------------
Step '4 of 6: Backing up the live site'
$stamp = Get-Date -Format 'yyyyMMdd-HHmm'
$backup = Join-Path $deployFolder "wwwroot-backup-$stamp"
Copy-Folder $site $backup @('/XF', 'app_offline.htm')
Say "Backed up to $backup"
Get-ChildItem $deployFolder -Directory -Filter 'wwwroot-backup-*' | Sort-Object Name -Descending |
    Select-Object -Skip $KeepBackups | ForEach-Object { Remove-Item $_.FullName -Recurse -Force; Say "Removed old backup $($_.Name)" 'DarkGray' }

# 5. Swap in the new files -----------------------------------------------------
Step '5 of 6: Putting the new version live'
try {
    Set-Offline $site
    Copy-Folder $build $site @('/XF', 'appsettings.Production.json', 'app_offline.htm')
    Set-Online $site
} catch {
    Say "Copy failed: $($_.Exception.Message)" 'Red'
    Say 'Putting the backup back...' 'Yellow'
    Restore-Backup $backup $site
    Stop-Deploy 'The site is back on the previous version. Nothing else changed.'
}
Set-Content $lastDeployFile $commit -Encoding ASCII

# 6. Check -------------------------------------------------------------------
Step '6 of 6: Checking the site'
if (Test-Site $settings.SiteUrl) {
    Write-Host ''
    Say "Done. $commit is live at $($settings.SiteUrl)" 'Green'
} else {
    Write-Host ''
    Say 'The new version is in place but the site is not answering yet.' 'Yellow'
    Say 'Wait a minute and open the site. If it still fails, double-click Rollback.cmd to go back.' 'Yellow'
    Say 'The error will be in Event Viewer > Windows Logs > Application.' 'Yellow'
    exit 1
}
