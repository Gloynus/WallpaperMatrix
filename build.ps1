param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$projectPath = Join-Path $PSScriptRoot "WallpaperMatrix.csproj"
$outputPath = Join-Path $PSScriptRoot "dist"

dotnet restore $projectPath `
    --runtime win-x64

if ($LASTEXITCODE -ne 0) {
    throw "dotnet restore failed with exit code $LASTEXITCODE"
}

dotnet publish $projectPath `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained true `
    --output $outputPath `
    --no-restore `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:PublishTrimmed=false `
    -p:PublishReadyToRun=false `
    -p:DebugType=None `
    -p:DebugSymbols=false

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

Copy-Item -LiteralPath (Join-Path $PSScriptRoot "PORTABLE-README.txt") `
    -Destination (Join-Path $outputPath "README.txt") `
    -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "THIRD-PARTY-NOTICES.txt") `
    -Destination (Join-Path $outputPath "THIRD-PARTY-NOTICES.txt") `
    -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "LICENSE") `
    -Destination (Join-Path $outputPath "LICENSE.txt") `
    -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "RELEASE_NOTES-3.3.2.md") `
    -Destination (Join-Path $outputPath "RELEASE_NOTES.txt") `
    -Force

Write-Host ""
Write-Host "Wallpaper Matrix portable self-contained build completed:" -ForegroundColor Green
Write-Host (Join-Path $outputPath "WallpaperMatrix.exe")
