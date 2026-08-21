param(
    [string]$BridgeUrl = $env:KSP_MCP_URL,
    [string]$BridgeToken = $env:KSP_MCP_TOKEN
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path -LiteralPath $PSScriptRoot).Path
$python = Get-Command python -ErrorAction SilentlyContinue
if (-not $python) {
    throw 'Python 3.10 or newer was not found on PATH. Install Python, then run this script again.'
}

if ([string]::IsNullOrWhiteSpace($BridgeUrl)) { $BridgeUrl = 'http://127.0.0.1:8765' }
$env:KSP_MCP_URL = $BridgeUrl
$env:KSP_MCP_TOKEN = if ($null -eq $BridgeToken) { '' } else { $BridgeToken }
$env:PYTHONPATH = if ([string]::IsNullOrWhiteSpace($env:PYTHONPATH)) {
    $projectRoot
} else {
    "$projectRoot;$($env:PYTHONPATH)"
}

Write-Host "KSP MCP root: $projectRoot"
Write-Host "Bridge URL: $env:KSP_MCP_URL"
Write-Host 'Starting the stdio MCP server. Keep this process attached to the MCP client.'
& $python.Source -m server
exit $LASTEXITCODE

