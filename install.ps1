param(
    [Parameter(Mandatory = $true)]
    [string]$KspRoot
)

$ErrorActionPreference = 'Stop'

$resolvedRoot = (Resolve-Path -LiteralPath $KspRoot).Path
$gameExecutables = @(
    (Join-Path $resolvedRoot 'KSP_x64.exe'),
    (Join-Path $resolvedRoot 'KSP.exe')
)
if (-not ($gameExecutables | Where-Object { Test-Path -LiteralPath $_ })) {
    throw "KSP executable was not found below $resolvedRoot"
}

$source = Join-Path $PSScriptRoot 'ksp-plugin\GameData\KspMcp'
if (-not (Test-Path -LiteralPath $source)) {
    throw "The package is incomplete; missing $source"
}

$running = Get-Process -Name 'KSP_x64','KSP' -ErrorAction SilentlyContinue
if ($running) {
    throw 'Close KSP before installing the plugin so Unity cannot keep an older DLL loaded.'
}

$destination = Join-Path $resolvedRoot 'GameData\KspMcp'
New-Item -ItemType Directory -Force -Path $destination | Out-Null
Copy-Item -Path (Join-Path $source '*') -Destination $destination -Recurse -Force
Write-Host "Installed KspMcp to $destination"
Write-Host 'Start KSP, then start the MCP server with: python -m server'
