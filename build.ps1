param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$OutputPath = ""
)

$ErrorActionPreference = "Stop"
$projectPath = Join-Path $PSScriptRoot "WallpaperMatrix.csproj"
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $PSScriptRoot "dist"
}
elseif (-not [IO.Path]::IsPathRooted($OutputPath)) {
    $OutputPath = Join-Path $PSScriptRoot $OutputPath
}
$projectDefinition = [xml](Get-Content -LiteralPath $projectPath -Raw)
$version = [string]$projectDefinition.Project.PropertyGroup.Version
$releaseNotesPath = Join-Path $PSScriptRoot "RELEASE_NOTES-$version.md"
if (-not (Test-Path -LiteralPath $releaseNotesPath)) {
    throw "Release notes not found: $releaseNotesPath"
}

dotnet restore $projectPath `
    --runtime win-x64 `
    --locked-mode

if ($LASTEXITCODE -ne 0) {
    throw "dotnet restore failed with exit code $LASTEXITCODE"
}

dotnet publish $projectPath `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained true `
    --output $OutputPath `
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
    -Destination (Join-Path $OutputPath "README.txt") `
    -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "THIRD-PARTY-NOTICES.txt") `
    -Destination (Join-Path $OutputPath "THIRD-PARTY-NOTICES.txt") `
    -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "LICENSE") `
    -Destination (Join-Path $OutputPath "LICENSE.txt") `
    -Force
Copy-Item -LiteralPath $releaseNotesPath `
    -Destination (Join-Path $OutputPath "RELEASE_NOTES.txt") `
    -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "PRIVACY.md") `
    -Destination (Join-Path $OutputPath "PRIVACY.txt") `
    -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "CODE_SIGNING_POLICY.md") `
    -Destination (Join-Path $OutputPath "CODE_SIGNING_POLICY.txt") `
    -Force

Write-Host ""
Write-Host "Wallpaper Matrix portable self-contained build completed:" -ForegroundColor Green
Write-Host (Join-Path $OutputPath "WallpaperMatrix.exe")
