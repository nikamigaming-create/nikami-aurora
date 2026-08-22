[CmdletBinding()]
param(
    [string]$Destination
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$expectedCommit = "7e40846d36acb5118e2e9feb2fd53620c29be540"
if ([string]::IsNullOrWhiteSpace($Destination)) {
    $Destination = Join-Path $repo "local\tools\mdlops"
}

if (Test-Path -LiteralPath $Destination) {
    if (-not (Test-Path -LiteralPath (Join-Path $Destination ".git"))) {
        throw "Existing MDLOps destination is not a Git checkout: $Destination"
    }
    $actual = (& git -C $Destination rev-parse HEAD).Trim()
    if ($actual -cne $expectedCommit) {
        throw "MDLOps checkout is $actual; expected pinned commit $expectedCommit"
    }
    Write-Host "MDLOps already ready at $Destination"
    return
}

$parent = Split-Path -Parent $Destination
New-Item -ItemType Directory -Force -Path $parent | Out-Null
& git clone https://github.com/ndixUR/mdlops.git $Destination
if ($LASTEXITCODE -ne 0) {
    throw "MDLOps clone failed with exit code $LASTEXITCODE"
}
& git -C $Destination checkout --detach $expectedCommit
if ($LASTEXITCODE -ne 0) {
    throw "MDLOps checkout failed with exit code $LASTEXITCODE"
}
Write-Host "MDLOps ready at $Destination ($expectedCommit)"
