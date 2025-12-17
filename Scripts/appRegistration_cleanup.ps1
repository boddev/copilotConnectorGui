<#
.SYNOPSIS
  Enumerate and (optionally) delete Entra ID (Azure AD) App Registrations whose Display Name
  starts with "Copilot Connector -".

.DESCRIPTION
  1. Connects to Microsoft Graph with required scopes.
  2. Retrieves all application registrations.
  3. Filters those whose Display Name matches the regex ^Copilot Connector -
  4. Applies safety skips (protected patterns, redirect URIs, exposed scopes if configured).
  5. Outputs a summary (dry run by default).
  6. Optionally deletes matching applications.
  7. (Optional) Hard-deletes (purges) deleted apps from the directory recycle bin.

.REQUIREMENTS
  - Microsoft.Graph PowerShell SDK
  - Permissions: Application.Read.All, Application.ReadWrite.All, Directory.Read.All
  - Appropriate admin consent

.NOTES
  Run first as dry run (default). Review. Then flip $ExecuteDeletion to $true.

#>

# =========================
# === CONFIGURATION =======
# =========================

# Regex pattern: any Display Name that begins with "Copilot Connector -"
$DeletionPattern        = '^Copilot Connector -'

# Protected patterns (NEVER delete if they match any of these, even if they match main pattern)
$ProtectedNamePatterns  = @(
    '^Copilot Connector - KEEP',
    '^Copilot Connector - DO NOT DELETE',
    '^Copilot Connector - PROD'   # add/remove as needed
)

# Safety toggles
$ExecuteDeletion        = $true   # Dry run by default. Set to $true to actually delete.
$InteractivePerItem     = $false   # If $true, ask Y/N before each deletion.
$HardDelete             = $false   # If $true, attempt permanent purge after soft delete.
$SkipIfHasRedirectUris  = $false    # Skip apps that have redirect URIs (likely in use).
$SkipIfExposesScopes    = $true    # Skip apps that define custom OAuth2 permission scopes.
$ExportCandidates       = $true    # Export CSV of candidates before any deletion.
$ExportDirectory        = "."      # Where to place CSV export.

# Throttling (set small delay if you expect large volumes or 429s)
$PostDeleteSleepSeconds = 0

# Optional normalization of spacing (set $true if you have messy naming with double spaces)
$NormalizeWhitespace    = $false

# =========================
# === CONNECT PHASE =======
# =========================

Write-Host "Connecting to Microsoft Graph..." -ForegroundColor Cyan

if (-not (Get-Module Microsoft.Graph.Applications -ListAvailable)) {
    Write-Host "Installing Microsoft.Graph module (first time only)..." -ForegroundColor Yellow
    Install-Module Microsoft.Graph -Scope CurrentUser -Force
}

$Scopes = @(
    'Application.Read.All',
    'Application.ReadWrite.All',
    'Directory.Read.All'
)

Connect-MgGraph -Scopes $Scopes | Out-Null
# You may remove the beta profile if you prefer v1.0. Keep if you rely on beta-only props.
Select-MgProfile -Name beta

$ctx = Get-MgContext
Write-Host ("Connected. Tenant: {0}" -f $ctx.TenantId) -ForegroundColor Green

# =========================
# === RETRIEVE APPS =======
# =========================

Write-Host "Retrieving all application registrations..." -ForegroundColor Cyan
# Get-MgApplication -All pages through results
$applications = Get-MgApplication -All
Write-Host ("Total applications retrieved: {0}" -f $applications.Count) -ForegroundColor Gray

if (-not $applications -or $applications.Count -eq 0) {
    Write-Host "No applications returned. Exiting." -ForegroundColor Yellow
    return
}

# =========================
# === FILTERING LOGIC =====
# =========================

$candidates = @()

foreach ($app in $applications) {
    $name = $app.DisplayName
    if (-not $name) { continue }

    if ($NormalizeWhitespace) {
        # Collapse multiple spaces and trim trailing
        $name = ($name -replace '\s{2,}',' ') -replace '\s+$',''
    }

    if ($name -match $DeletionPattern) {

        # Check protected patterns
        $isProtected = $false
        foreach ($pp in $ProtectedNamePatterns) {
            if ($name -match $pp) {
                $isProtected = $true
                break
            }
        }

        if ($isProtected) {
            $candidates += [pscustomobject]@{
                DisplayName    = $name
                AppId          = $app.AppId
                ObjectId       = $app.Id
                ReasonSkipped  = 'ProtectedPattern'
                WillDelete     = $false
                RedirectUris   = @($app.Web.RedirectUris + $app.Spa.RedirectUris) -join ';'
                HasScopes      = ($app.Api -and $app.Api.Oauth2PermissionScopes -and $app.Api.Oauth2PermissionScopes.Count -gt 0)
            }
            continue
        }

        # Skip if redirect URIs exist
        if ($SkipIfHasRedirectUris -and
            (
                ($app.Web -and $app.Web.RedirectUris.Count -gt 0) -or
                ($app.Spa -and $app.Spa.RedirectUris.Count -gt 0)
            )
        ) {
            $candidates += [pscustomobject]@{
                DisplayName    = $name
                AppId          = $app.AppId
                ObjectId       = $app.Id
                ReasonSkipped  = 'HasRedirectUris'
                WillDelete     = $false
                RedirectUris   = @($app.Web.RedirectUris + $app.Spa.RedirectUris) -join ';'
                HasScopes      = ($app.Api -and $app.Api.Oauth2PermissionScopes -and $app.Api.Oauth2PermissionScopes.Count -gt 0)
            }
            continue
        }

        # Skip if app exposes custom scopes
        $hasScopes = ($app.Api -and $app.Api.Oauth2PermissionScopes -and $app.Api.Oauth2PermissionScopes.Count -gt 0)
        if ($SkipIfExposesScopes -and $hasScopes) {
            $candidates += [pscustomobject]@{
                DisplayName    = $name
                AppId          = $app.AppId
                ObjectId       = $app.Id
                ReasonSkipped  = 'ExposesScopes'
                WillDelete     = $false
                RedirectUris   = @($app.Web.RedirectUris + $app.Spa.RedirectUris) -join ';'
                HasScopes      = $true
            }
            continue
        }

        # Mark for deletion
        $candidates += [pscustomobject]@{
            DisplayName    = $name
            AppId          = $app.AppId
            ObjectId       = $app.Id
            ReasonSkipped  = $null
            WillDelete     = $true
            RedirectUris   = @($app.Web.RedirectUris + $app.Spa.RedirectUris) -join ';'
            HasScopes      = $hasScopes
        }
    }
}

if (-not $candidates -or $candidates.Count -eq 0) {
    Write-Host "No applications matched the pattern '$DeletionPattern'." -ForegroundColor Yellow
    return
}

$toDelete    = $candidates | Where-Object { $_.WillDelete }
$skipped     = $candidates | Where-Object { -not $_.WillDelete }

Write-Host ""
Write-Host "========== SUMMARY ==========" -ForegroundColor Cyan
Write-Host ("Pattern:                {0}" -f $DeletionPattern)
Write-Host ("Matched total:          {0}" -f $candidates.Count)
Write-Host ("Planned deletions:      {0}" -f $toDelete.Count) -ForegroundColor Red
Write-Host ("Skipped (protected/etc):{0}" -f $skipped.Count) -ForegroundColor Yellow
Write-Host "=============================" -ForegroundColor Cyan
Write-Host ""

if ($toDelete.Count -gt 0) {
    Write-Host "Planned Deletions:" -ForegroundColor Red
    $toDelete | Select-Object DisplayName, AppId, ObjectId | Format-Table -AutoSize
}

if ($skipped.Count -gt 0) {
    Write-Host ""
    Write-Host "Skipped Candidates:" -ForegroundColor Yellow
    $skipped | Select-Object DisplayName, AppId, ReasonSkipped | Format-Table -AutoSize
}

# =========================
# === EXPORT (OPTIONAL) ===
# =========================

if ($ExportCandidates) {
    if (-not (Test-Path $ExportDirectory)) {
        New-Item -ItemType Directory -Path $ExportDirectory | Out-Null
    }
    $timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $exportPath = Join-Path $ExportDirectory "CopilotConnectorApps-$timestamp.csv"
    $candidates | Export-Csv -NoTypeInformation -Path $exportPath
    Write-Host ""
    Write-Host ("Exported candidate list to {0}" -f $exportPath) -ForegroundColor Cyan
}

# =========================
# === DRY RUN EXIT ========
# =========================

if (-not $ExecuteDeletion) {
    Write-Host ""
    Write-Host "Dry run complete. Set `$ExecuteDeletion = `$true to perform deletions." -ForegroundColor Yellow
    return
}

# =========================
# === DELETION PHASE ======
# =========================

if ($toDelete.Count -eq 0) {
    Write-Host "Nothing to delete (all matched apps skipped by safety rules)." -ForegroundColor Yellow
    return
}

Write-Host ""
Write-Host "DELETION EXECUTION STARTING..." -ForegroundColor Red

foreach ($item in $toDelete) {

    if ($InteractivePerItem) {
        $resp = Read-Host ("Delete '{0}'? (y/N)" -f $item.DisplayName)
        if ($resp -notin @('y','Y','yes','YES')) {
            Write-Host ("Skipped {0}" -f $item.DisplayName) -ForegroundColor DarkYellow
            continue
        }
    }

    try {
        Write-Host ("Deleting: {0} (AppId: {1})" -f $item.DisplayName, $item.AppId) -ForegroundColor Magenta
        Remove-MgApplication -ApplicationId $item.ObjectId -ErrorAction Stop
        Write-Host "  Soft-deleted (moved to Deleted Items)" -ForegroundColor Green

        if ($HardDelete) {
            Start-Sleep -Seconds 2
            $deletedObj = Get-MgDirectoryDeletedItem -All | Where-Object { $_.Id -eq $item.ObjectId }
            if ($deletedObj) {
                Write-Host "  Purging permanently..." -ForegroundColor DarkRed
                Remove-MgDirectoryDeletedItem -DirectoryObjectId $deletedObj.Id -ErrorAction Stop
                Write-Host "  Purged." -ForegroundColor Green
            } else {
                Write-Host "  Could not locate in Deleted Items (replication delay or already purged)." -ForegroundColor DarkYellow
            }
        }

        if ($PostDeleteSleepSeconds -gt 0) {
            Start-Sleep -Seconds $PostDeleteSleepSeconds
        }
    }
    catch {
        Write-Host ("  FAILED: {0}" -f $_.Exception.Message) -ForegroundColor Red
    }
}

Write-Host ""
Write-Host "Deletion phase complete." -ForegroundColor Green

# =========================
# === OPTIONAL NOTES ======
# =========================
# To also remove related service principals in other tenants, you'd need to run a cleanup
# there (service principals are per-tenant). This script only deletes the home application object.