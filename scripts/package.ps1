param(
    [Parameter(Mandatory=$true)][ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')][string]$Repository,
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '0.1.0',
    [string]$Dotnet = 'dotnet'
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$output = Join-Path $projectRoot 'artifacts'
New-Item -ItemType Directory -Force -Path $output | Out-Null
& $Dotnet build (Join-Path $projectRoot 'src') -c Release "-p:Version=$Version" "-p:AssemblyVersion=$Version.0"
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
$dll = Join-Path $projectRoot 'src/bin/Release/net10.0/Jellyfin.Plugin.MangaReader.dll'
$zipName = "manga-reader_$Version.0.zip"
$zipPath = Join-Path $output $zipName
Compress-Archive -LiteralPath $dll -DestinationPath $zipPath -Force
$checksum = (Get-FileHash -LiteralPath $zipPath -Algorithm MD5).Hash.ToLowerInvariant()
$manifest = @(@{
    guid='42cc80cc-2832-4b92-a583-06ab560eca34'; name='Manga Reader';
    description='Read CBZ/ZIP manga with page turning and per-user progress saved on your Jellyfin server.';
    overview='A browser-based manga reader with synced reading progress.';
    owner=$Repository.Split('/')[0]; category='General';
    versions=@(@{ version="$Version.0"; changelog='Initial preview release: CBZ/ZIP reader, right-to-left controls, per-user resume.';
        targetAbi='12.1.0.0'; sourceUrl="https://github.com/$Repository/releases/download/v$Version/$zipName";
        checksum=$checksum; timestamp=[DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ') })
})
ConvertTo-Json -InputObject $manifest -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'manifest.json') -Encoding utf8NoBOM
Write-Output "Created $zipPath and artifacts/manifest.json"
