param(
    [string]$OutputDirectory = "Releases"
)

$ErrorActionPreference = "Stop"
$packageRoot = (Get-Location).Path
$manifest = Get-Content -Raw (Join-Path $packageRoot "package.json") | ConvertFrom-Json
$stage = Join-Path ([System.IO.Path]::GetTempPath()) ("eem-vpm-" + [guid]::NewGuid().ToString("N"))
$output = Join-Path $packageRoot $OutputDirectory
$zipName = "$($manifest.name)-$($manifest.version).zip"
$zip = Join-Path $output $zipName

try {
    New-Item -ItemType Directory -Path $stage,$output -Force | Out-Null

    foreach ($name in @("Editor", "package.json", "README.md", "LICENSE", "CHANGELOG.md")) {
        $source = Join-Path $packageRoot $name
        if (-not (Test-Path -LiteralPath $source)) {
            throw "Required package file is missing: $name"
        }
        Copy-Item -LiteralPath $source -Destination $stage -Recurse -Force
    }

    # Developer settings are intentionally project-local and are not shipped.
    $developerSettings = Join-Path $stage "Editor/developer.json"
    if (Test-Path -LiteralPath $developerSettings) {
        Remove-Item -LiteralPath $developerSettings -Force
        $developerMeta = "$developerSettings.meta"
        if (Test-Path -LiteralPath $developerMeta) { Remove-Item -LiteralPath $developerMeta -Force }
    }

    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
    Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip -CompressionLevel Optimal
    $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()

    Write-Output "Package: $zip"
    Write-Output "Version: $($manifest.version)"
    Write-Output "SHA256:  $hash"
    Write-Output "Add this SHA256 as zipSHA256 in index.json before publishing."
}
finally {
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
}
