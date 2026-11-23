param(
    [string]$OutputDirectory = "artifacts/PSUPanel"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

dotnet run --project "$root/tests/PSUPanel.Protocol.Tests/PSUPanel.Protocol.Tests.csproj" -c Release -- "$root/himos/analysis/psu-capture.jsonl"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet publish "$root/src/PSUPanel.Tray/PSUPanel.Tray.csproj" `
    -c Release `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $OutputDirectory
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet publish "$root/src/PSUPanel.Panel/PSUPanel.Panel.csproj" `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $OutputDirectory
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Remove-Item "$OutputDirectory/*.pdb" -Force -ErrorAction SilentlyContinue
Write-Host "Published PSUPanel to $OutputDirectory"
