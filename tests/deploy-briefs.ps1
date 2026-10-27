# Copies the live-verification briefs (tests\live\*.md) to <deploy dir>\tests\live, so the
# tester on the AutoCAD machine reads them from the same shared folder the DLL comes from.
#
#     powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\deploy-briefs.ps1
#
# Lives in tests\, so every repo path is resolved from its parent ($RepoRoot) — runnable from
# any working directory.
#
# The destination is MIRRORED, but non-destructively: a brief deleted or renamed here is moved
# out of the way there, into tests\live\_retired, rather than deleted. A stale brief is worse
# than none (AGENTS.md): it sends the tester to verify behaviour that no longer exists - so it
# must not stay next to the current ones. But a deletion here can be an accident (the briefs
# are not in git), and the deployed copy may then be the only one left - so it is kept.
param(
    # Overrides everything else. Otherwise the same precedence as deploy.ps1:
    # deploy.local.props -> $env:WANG_MCP_DEPLOY_DIR -> %LOCALAPPDATA% fallback.
    [string]$TargetDir
)

$ErrorActionPreference = "Stop"

$RepoRoot = Split-Path -Parent $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($TargetDir)) {
    $PropsPath = Join-Path $RepoRoot "deploy.local.props"
    if (Test-Path $PropsPath) {
        [xml]$Props = Get-Content -Raw $PropsPath
        $Node = $Props.SelectSingleNode("//*[local-name()='WangMcpDeployDir']")
        if ($null -ne $Node) {
            $TargetDir = $Node.InnerText.Trim()
        }
    }
}
if ([string]::IsNullOrWhiteSpace($TargetDir)) {
    $TargetDir = $env:WANG_MCP_DEPLOY_DIR
}
if ([string]::IsNullOrWhiteSpace($TargetDir)) {
    $TargetDir = Join-Path $env:LOCALAPPDATA "Wang_MCP_AutoCAD\deploy"
    Write-Warning "No deploy.local.props and no WANG_MCP_DEPLOY_DIR; copying briefs to $TargetDir instead of the AutoCAD host's shared folder. Copy deploy.local.props.example to deploy.local.props to fix this."
}

$SourceDir = Join-Path $RepoRoot "tests\live"
$DestDir = Join-Path $TargetDir "tests\live"

if (-not (Test-Path $SourceDir)) {
    throw "No briefs to copy: $SourceDir does not exist."
}

New-Item -ItemType Directory -Force -Path $DestDir | Out-Null

$SourceFiles = @(Get-ChildItem -Path $SourceDir -File)
foreach ($File in $SourceFiles) {
    Copy-Item -LiteralPath $File.FullName -Destination (Join-Path $DestDir $File.Name) -Force
}

$SourceNames = @{}
foreach ($File in $SourceFiles) {
    $SourceNames[$File.Name.ToLowerInvariant()] = $true
}

$RetiredDir = Join-Path $DestDir "_retired"
$Retired = 0
foreach ($Existing in @(Get-ChildItem -Path $DestDir -File)) {
    if (-not $SourceNames.ContainsKey($Existing.Name.ToLowerInvariant())) {
        New-Item -ItemType Directory -Force -Path $RetiredDir | Out-Null
        Move-Item -LiteralPath $Existing.FullName -Destination (Join-Path $RetiredDir $Existing.Name) -Force
        Write-Host "Retired $($Existing.Name) (no longer in tests\live) to $RetiredDir"
        $Retired++
    }
}

Write-Host "Copied $($SourceFiles.Count) brief(s) to $DestDir (retired $Retired stale)"
