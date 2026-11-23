param(
    [string]$OutputDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts\installer')
)

# Installer della Beta 4 per Windows con la nuova interfaccia, dalla cartella Cinecore_Windows (dal 21/11/2026:
# prima compilava la copia in Downloads\Player_Beta).
# Stesso AppId e stessa cartella del player precedente: l'installazione lo sostituisce (dati e impostazioni restano).
# Runtime .NET incluso (anche nel VideoHost e nel rilevatore delle sigle), app precompilata (ReadyToRun), componenti
# di terze parti facoltativi come prima (LAV, MPC Audio Decoder/Renderer, madVR, MPC-VR, XySubFilter, WebView2).
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$avalonia = $repo
$project = Join-Path $avalonia 'win-native\CinecoreWin\CinecoreWin.csproj'
$detector = Join-Path $avalonia 'IntroOutroDetector\IntroOutroDetector.csproj'
$resolvedOutput = [System.IO.Path]::GetFullPath($OutputDirectory)
$allowedRoot = [System.IO.Path]::GetFullPath((Join-Path $repo 'artifacts\installer'))
if (-not $resolvedOutput.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase)) { throw "La directory di output deve rimanere sotto $allowedRoot" }

$isccPath = @(
    (Get-Command iscc.exe -ErrorAction SilentlyContinue | ForEach-Object Source),
    (Join-Path $env:USERPROFILE 'scoop\apps\inno-setup\current\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
) | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
if (-not $isccPath) { throw 'Serve Inno Setup 6.6 o successivo (ISCC.exe).' }

New-Item -ItemType Directory -Force -Path $resolvedOutput | Out-Null
$work = Join-Path $resolvedOutput ("build-avalonia-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$payload = Join-Path $work 'payload'
New-Item -ItemType Directory -Force -Path $payload | Out-Null

Write-Host 'Pubblico Cinecore (nuova interfaccia) per Windows x64, runtime incluso e precompilato...'
& dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -o $payload
if ($LASTEXITCODE -ne 0) { throw "dotnet publish non riuscito ($LASTEXITCODE)." }
if (-not (Test-Path (Join-Path $payload 'VideoHost\VideoHost.exe'))) { throw 'VideoHost mancante nel payload.' }

# Il rilevatore delle sigle e' net8.0 dipendente dal runtime: nell'installer va con il suo runtime.
$detectorOut = Join-Path $payload 'IntroOutroDetector'
if (Test-Path $detectorOut) { Remove-Item -LiteralPath $detectorOut -Recurse -Force }
& dotnet publish $detector -c Release -r win-x64 --self-contained true -o $detectorOut
if ($LASTEXITCODE -ne 0) { throw "Publish del rilevatore delle sigle non riuscito ($LASTEXITCODE)." }

# Componenti di terze parti usati dalla nuova interfaccia (niente runtime Python delle stem).
$thirdSource = Join-Path $repo 'third-parties'
$thirdTarget = Join-Path $payload 'third-parties'
foreach ($name in @('madVR09217', 'MpcVideoRenderer-0.9.7.2387', 'XySubFilter_3.1.0.752_x64', 'mpv', 'ffmpeg', 'libaacs', 'rubberband', 'yt-dlp', 'installers')) {
    $src = Join-Path $thirdSource $name
    if (-not (Test-Path -LiteralPath $src)) { throw "Componente mancante: $src" }
    Copy-Item -LiteralPath $src -Destination (Join-Path $thirdTarget $name) -Recurse -Force
}

# Niente file privati o di sviluppo.
foreach ($path in @((Join-Path $thirdTarget 'installers\setting.reg'))) { if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force } }
Get-ChildItem -LiteralPath $payload -Recurse -File | Where-Object { $_.Extension -eq '.pdb' -or $_.Name -like '*.log' } | Remove-Item -Force
$tooLong = Get-ChildItem -LiteralPath $payload -Recurse -File | Where-Object { $_.FullName.Length -ge 255 }
if ($tooLong) { throw ("Percorsi troppo lunghi: " + (($tooLong | Select-Object -First 3 | ForEach-Object FullName) -join '; ')) }
Copy-Item -LiteralPath (Join-Path $repo 'LICENSE') -Destination $payload -Force
Copy-Item -LiteralPath (Join-Path $avalonia 'THIRD-PARTY-CREDITS.md') -Destination $payload -Force
[xml]$projectXml = Get-Content -LiteralPath $project -Raw
$assembly = ($projectXml.Project.PropertyGroup | ForEach-Object { $_.AssemblyName } | Where-Object { $_ } | Select-Object -First 1)
if (-not $assembly) { throw 'AssemblyName mancante nel progetto.' }
$appExe = $assembly + '.exe'
foreach ($need in @($appExe, 'cinecore.ico', 'third-parties\installers\LAVFilters-0.80.exe', 'third-parties\mpv\x86_64\libmpv-2.dll', 'IntroOutroDetector\IntroOutroDetector.exe')) {
    if (-not (Test-Path (Join-Path $payload $need))) { throw "Manca nel payload: $need" }
}

[xml]$xml = Get-Content -LiteralPath $project -Raw
$versionNumber = ($xml.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
$versionLabel = ($xml.Project.PropertyGroup | ForEach-Object { $_.InformationalVersion } | Where-Object { $_ } | Select-Object -First 1)
if ($versionNumber -notmatch '^\d+\.\d+\.\d+$') { throw "<Version> non valida: '$versionNumber'" }
$outputName = 'CinecorePlayer2026-' + ($versionLabel -replace '[^A-Za-z0-9.]', '') + '-Windows-Setup'

Write-Host 'Creo il programma di installazione con Inno Setup...'
& $isccPath "/DPayloadDir=$payload" "/DOutputDir=$resolvedOutput" "/DAppVersion=$versionLabel" "/DAppVersionNumber=$versionNumber" `
    "/DOutputName=$outputName" "/DAppExe=$appExe" "/DLicenseDir=$repo" "/DAvaloniaBuild=1" (Join-Path $PSScriptRoot 'CinecorePlayer.iss')
if ($LASTEXITCODE -ne 0) { throw "Compilazione dell'installer non riuscita ($LASTEXITCODE)." }

$installer = Join-Path $resolvedOutput ($outputName + '.exe')
if (-not (Test-Path -LiteralPath $installer)) { throw "Installer non trovato: $installer" }
$hash = Get-FileHash -LiteralPath $installer -Algorithm SHA256
$hash.Hash + '  ' + (Split-Path $installer -Leaf) | Set-Content -LiteralPath (Join-Path $resolvedOutput ($outputName + '.sha256.txt')) -Encoding ASCII
Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "Installer creato: $installer"
Write-Host ('SHA-256: ' + $hash.Hash)
Write-Host "Release su GitHub: tag beta-v$versionNumber-Avalonia-Windows con questo .exe allegato."
