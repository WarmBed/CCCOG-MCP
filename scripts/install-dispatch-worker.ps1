param(
    [string]$Version = "0.5.5",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path $PSScriptRoot -Parent
$publishRoot = Join-Path $repoRoot "artifacts\cccg-dispatch-worker\$Version"
$installRoot = Join-Path $env:LOCALAPPDATA "CCCG\dispatch\workers\$Version"
$descriptorPath = Join-Path $env:LOCALAPPDATA "CCCG\dispatch\worker-current.json"

dotnet publish (Join-Path $repoRoot "src\CCCG.Dispatch.Worker\CCCG.Dispatch.Worker.csproj") `
    -c $Configuration `
    --no-restore `
    -p:Version=$Version `
    -o $publishRoot

New-Item -ItemType Directory -Force -Path $installRoot | Out-Null
Get-ChildItem -LiteralPath $publishRoot -Force | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $installRoot -Recurse -Force
}

$workerPath = Join-Path $installRoot "cccg-dispatch-worker.exe"
if (-not (Test-Path -LiteralPath $workerPath)) {
    throw "Published worker is missing: $workerPath"
}

$descriptor = [ordered]@{
    schemaVersion = 1
    path = $workerPath
    version = $Version
    sha256 = (Get-FileHash -LiteralPath $workerPath -Algorithm SHA256).Hash.ToLowerInvariant()
    installedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
}

$descriptorDirectory = Split-Path $descriptorPath -Parent
New-Item -ItemType Directory -Force -Path $descriptorDirectory | Out-Null
$temporary = "$descriptorPath.$([Guid]::NewGuid().ToString('N')).tmp"
try {
    [IO.File]::WriteAllText(
        $temporary,
        ($descriptor | ConvertTo-Json),
        [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporary -Destination $descriptorPath -Force
}
finally {
    Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue
}

[ordered]@{
    installed = $true
    version = $Version
    worker = $workerPath
    descriptor = $descriptorPath
    sha256 = $descriptor.sha256
} | ConvertTo-Json
