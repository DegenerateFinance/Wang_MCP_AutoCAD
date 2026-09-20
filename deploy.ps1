param(
    [string]$Config = "Debug",
    [Parameter(Mandatory = $true)]
    [ValidateSet("Loader", "Plugin")]
    [string]$Project,
    # Passed by the csproj from $(WangMcpDeployDir), which comes from the gitignored
    # deploy.local.props. Kept out of source control so no machine-specific path is
    # committed - see deploy.local.props.example.
    [string]$TargetDir
)

# Precedence: -TargetDir (deploy.local.props) -> $env:WANG_MCP_DEPLOY_DIR -> fallback.
# The env var wins over nothing here but is the easier lever on the AutoCAD machine,
# where there is no source tree to hold a props file.
if ([string]::IsNullOrWhiteSpace($TargetDir)) {
    $TargetDir = $env:WANG_MCP_DEPLOY_DIR
}
if ([string]::IsNullOrWhiteSpace($TargetDir)) {
    $TargetDir = Join-Path $env:LOCALAPPDATA "Wang_MCP_AutoCAD\deploy"
    # Warn rather than fail: a fresh clone must still build. But say so loudly, because
    # a silent fallback is how you end up NETLOADing a stale DLL from the other folder.
    Write-Warning "No deploy.local.props and no WANG_MCP_DEPLOY_DIR; deploying to $TargetDir instead of the AutoCAD host's shared folder. Copy deploy.local.props.example to deploy.local.props to fix this."
}
New-Item -ItemType Directory -Force -Path $TargetDir | Out-Null

$ProjectDirs = @{
    Loader = "Wang_MCP_AutoCAD.Loader"
    Plugin = "Wang_MCP_AutoCAD"
}
$AssemblyNames = @{
    Loader = "Wang_MCP_AutoCAD.Loader"
    Plugin = "Wang_MCP_AutoCAD"
}

$AssemblyName = $AssemblyNames[$Project]
$SourceDir = "$PSScriptRoot\$($ProjectDirs[$Project])\bin\$Config\net8.0-windows"

if ($Project -eq "Loader") {
    # AutoCAD's CLR never unloads a NETLOAD'ed assembly, so re-loading the same
    # file name is a no-op (in fact it errors: "Assembly with same name is
    # already loaded" - the collision is on the assembly's simple name, not the
    # file path). Stamp each build with a timestamp so a NETLOAD after rebuilding
    # the Loader still picks up a genuinely new assembly without restarting
    # AutoCAD. The Loader is meant to change rarely - this is only for that case.
    $Stamp = Get-Date -Format "yyyyMMdd_HHmmss"
    $TargetName = "$AssemblyName`_$Stamp"

    Get-ChildItem "$TargetDir\$AssemblyName*.dll", "$TargetDir\$AssemblyName*.pdb" -ErrorAction SilentlyContinue |
        Remove-Item -Force -ErrorAction SilentlyContinue
} else {
    # The Plugin is never NETLOAD'ed by the Loader (it's read from disk into a
    # fresh collectible AssemblyLoadContext on every MCPRELOAD), so there's no
    # "already loaded" identity to collide with and no reason to stamp the name.
    # Overwriting one fixed file also keeps this folder clean: MCPRELOAD's file
    # picker opens here, so timestamped copies would pile up for you to pick the
    # newest out of on every reload.
    $TargetName = $AssemblyName
}

Copy-Item "$SourceDir\$AssemblyName.dll" -Destination "$TargetDir\$TargetName.dll" -Force
$PdbPath = "$SourceDir\$AssemblyName.pdb"
if (Test-Path $PdbPath) {
    Copy-Item $PdbPath -Destination "$TargetDir\$TargetName.pdb" -Force
}

Write-Host "Copied $Project to $TargetDir\$TargetName.dll"
