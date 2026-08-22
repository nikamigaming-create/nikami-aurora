[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$GameRoot = $env:NIKAMI_KOTOR_ROOT,

    [string]$Module = "end_m01aa",

    [string]$OutputRoot,

    [string]$MdlOps,

    [string]$Python = "py"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($GameRoot)) {
    throw "Pass -GameRoot or set NIKAMI_KOTOR_ROOT."
}
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repo "local\kotor\$Module"
}
if ([string]::IsNullOrWhiteSpace($MdlOps)) {
    $MdlOps = Join-Path $repo "local\tools\mdlops\mdlops.exe"
}
if (-not (Test-Path -LiteralPath $MdlOps -PathType Leaf)) {
    throw "MDLOps not found: $MdlOps. Run scripts/Bootstrap-MDLOps.ps1 first."
}

$arguments = @()
if ((Split-Path -Leaf $Python) -ieq "py" -or (Split-Path -Leaf $Python) -ieq "py.exe") {
    $arguments += "-3.12"
}
$arguments += @(
    (Join-Path $PSScriptRoot "import_kotor_module.py"),
    "--game-root", $GameRoot,
    "--module", $Module,
    "--output", $OutputRoot,
    "--mdlops", $MdlOps
)

& $Python @arguments
if ($LASTEXITCODE -ne 0) {
    throw "KOTOR module import failed with exit code $LASTEXITCODE"
}
