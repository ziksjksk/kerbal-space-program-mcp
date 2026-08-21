Import-Clixml: Unexpected end of file has occurred. The following elements are not closed: En, DCT, Obj, En, DCT, Obj, En, DCT, Obj,
Objs. Line 536, position 15.
InvalidOperation: Index operation failed; the array index evaluated to null.
Import-Clixml: N attribute was expected. Line 1133, position 20.
InvalidOperation: Index operation failed; the array index evaluated to null.
InvalidOperation: Index operation failed; the array index evaluated to null.
param(
    [string]$OutputPath = (Join-Path $PSScriptRoot 'outputs\ksp-mcp-0.4.9.zip')
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $PSScriptRoot).Path
$pluginSource = Join-Path $root 'ksp-plugin\GameData\KspMcp'
$pluginDll = Join-Path $pluginSource 'Plugins\KspMcpBridge.dll'
if (-not (Test-Path -LiteralPath $pluginDll)) {
    throw "Missing prebuilt plugin DLL: $pluginDll. Build it with ksp-plugin\build.ps1 before packaging."
}

$output = [IO.Path]::GetFullPath($OutputPath)
$outputDirectory = Split-Path -Parent $output
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
$stage = Join-Path ([IO.Path]::GetTempPath()) ('ksp-mcp-package-' + [Guid]::NewGuid().ToString('N'))

try {
    New-Item -ItemType Directory -Force -Path $stage | Out-Null
    foreach ($file in @('README.md', 'pyproject.toml', 'install.ps1', 'start-server.ps1', '.gitignore')) {
        Copy-Item -LiteralPath (Join-Path $root $file) -Destination (Join-Path $stage $file) -Force
    }
    foreach ($directory in @('server', 'examples', 'ksp-plugin')) {
        Copy-Item -LiteralPath (Join-Path $root $directory) -Destination (Join-Path $stage $directory) -Recurse -Force
    }
    Get-ChildItem -LiteralPath (Join-Path $stage 'ksp-plugin') -Directory -Recurse -Force |
        Where-Object { $_.Name -in @('bin', 'obj') } |
        Sort-Object FullName -Descending |
        ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force }
    Get-ChildItem -LiteralPath $stage -Directory -Recurse -Force |
        Where-Object { $_.Name -eq '__pycache__' } |
        Sort-Object FullName -Descending |
        ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force }
    Get-ChildItem -LiteralPath $stage -File -Recurse -Force |
        Where-Object { $_.Extension -eq '.pyc' } |
        ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }

    if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Force }
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $output -CompressionLevel Optimal
    $hash = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash
    $hashPath = [IO.Path]::ChangeExtension($output, '.sha256')
    "$hash  $([IO.Path]::GetFileName($output))" | Set-Content -LiteralPath $hashPath -Encoding ASCII
    Write-Host "Created $output"
    Write-Host "SHA256: $hash"
} finally {
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
}

