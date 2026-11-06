param([switch]$Test, [switch]$Publish)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location $projectRoot
try {
    $env:DOTNET_CLI_HOME = Join-Path $projectRoot '.build'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:MSBuildEnableWorkloadResolver = 'false'
    $env:NUGET_PACKAGES = Join-Path $projectRoot '.build/packages'
    $env:NUGET_HTTP_CACHE_PATH = Join-Path $projectRoot '.build/http-cache'
    $env:NUGET_PLUGINS_CACHE_PATH = Join-Path $projectRoot '.build/plugin-cache'
    dotnet build Reflection.sln -c Release -m:1 --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
    if ($Test) {
        dotnet run --project tests/Reflection.Tests -c Release --no-build
        if ($LASTEXITCODE -ne 0) { throw 'Core tests failed' }
        python scripts/smoke.py
        if ($LASTEXITCODE -ne 0) { throw 'Desktop smoke tests failed' }
    }
    if ($Publish) {
        dotnet publish src/Reflection.Desktop -c Release --no-restore --self-contained false -o artifacts/Reflection-latest -p:DebugType=None -p:DebugSymbols=false --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
        Copy-Item -LiteralPath README.md, ARCH.md, DESIGN.html, LICENSE -Destination artifacts/Reflection-latest/
        foreach ($oldDocument in @('DESIGN.md', 'OPTIMIZATION.md')) {
            $oldDocumentPath = Join-Path $projectRoot "artifacts/Reflection-latest/$oldDocument"
            if (Test-Path -LiteralPath $oldDocumentPath) { Remove-Item -LiteralPath $oldDocumentPath }
        }
    }
}
finally { Pop-Location }
