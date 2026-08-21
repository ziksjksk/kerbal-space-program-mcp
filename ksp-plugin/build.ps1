Import-Clixml: N attribute was expected. Line 1125, position 28.
InvalidOperation: Index operation failed; the array index evaluated to null.
param(
    [string]$KspRoot = $env:KSP_ROOT,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($KspRoot)) {
    throw 'Set KSP_ROOT to the KSP installation directory before building.'
}

$managedCandidates = @(
    (Join-Path $KspRoot 'KSP_x64_Data\Managed'),
    (Join-Path $KspRoot 'KSP_Data\Managed')
)
$managed = $managedCandidates | Where-Object { Test-Path (Join-Path $_ 'Assembly-CSharp.dll') } | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($managed)) {
    throw "Could not find Assembly-CSharp.dll below $KspRoot. Is KSP_ROOT the game root?"
}

$compilerCandidates = @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
)
$compiler = $compilerCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($compiler)) {
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -eq $dotnet) { throw 'Could not find csc.exe or dotnet. Install the .NET Framework developer tools.' }
    & $dotnet.Source build (Join-Path $PSScriptRoot 'KspMcpBridge.csproj') -c $Configuration -p:KSP_ROOT=$KspRoot
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE" }
    $built = Join-Path $PSScriptRoot "bin\$Configuration\KspMcpBridge.dll"
    $destination = Join-Path $PSScriptRoot 'GameData\KspMcp\Plugins\KspMcpBridge.dll'
    Copy-Item -Force $built $destination
    Write-Host "Built $destination"
    exit 0
}

$outputDirectory = Join-Path $PSScriptRoot 'GameData\KspMcp\Plugins'
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
$output = Join-Path $outputDirectory 'KspMcpBridge.dll'
$sources = Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' | Sort-Object Name | Select-Object -ExpandProperty FullName

$references = @(
    (Join-Path $managed 'Assembly-CSharp.dll'),
    (Join-Path $managed 'UnityEngine.dll'),
    (Join-Path $managed 'UnityEngine.CoreModule.dll'),
    (Join-Path $managed 'UnityEngine.PhysicsModule.dll'),
    (Join-Path $managed 'UnityEngine.UI.dll')
) | Where-Object { Test-Path $_ }

$arguments = @('/target:library', '/optimize+', "/out:$output", '/warn:4')
foreach ($reference in $references) { $arguments += "/reference:$reference" }
$arguments += $sources

& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw "C# build failed with exit code $LASTEXITCODE" }
Write-Host "Built $output"

