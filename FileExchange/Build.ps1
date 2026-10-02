#Requires -Version 5.1
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$version = & dotnet msbuild (Join-Path $PSScriptRoot 'App\FileExchange.csproj') -nologo -verbosity:quiet -target:GetFileExchangeVersion -getProperty:Version
if ($LASTEXITCODE -ne 0) { throw 'Не удалось вычислить версию проекта из истории Git.' }
$version = ([string]$version).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Некорректная версия проекта: $version" }
$archiveName = "FileExchange-$version-win-x64.zip"
$output = Join-Path $PSScriptRoot 'artifacts\app'
$packageDirectory = Join-Path $PSScriptRoot 'artifacts\package'
$privateKeyPath = Join-Path $PSScriptRoot 'artifacts\temporary-signing-key.pfx'
$packagePath = Join-Path $output 'FileExchange.msix'
$archivePath = Join-Path (Join-Path $PSScriptRoot 'artifacts') $archiveName
[void][IO.Directory]::CreateDirectory($output)
[void][IO.Directory]::CreateDirectory($packageDirectory)

# NativeAOT requires the Windows SDK and Visual Studio C++ build tools, although all source is C#.
$sdkBinRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
$sdkDirectory = Get-ChildItem -LiteralPath $sdkBinRoot -Directory |
    Where-Object { $_.Name -match '^10\.0\.\d+\.\d+$' -and [IO.File]::Exists((Join-Path $_.FullName 'x64\makeappx.exe')) } |
    Sort-Object { [Version]$_.Name } -Descending | Select-Object -First 1
if ($null -eq $sdkDirectory) { throw 'Установите Windows SDK: не найден MakeAppx.exe.' }
$makeAppx = Join-Path $sdkDirectory.FullName 'x64\makeappx.exe'
$signTool = Join-Path $sdkDirectory.FullName 'x64\signtool.exe'
if (-not [IO.File]::Exists($signTool)) { throw 'В Windows SDK не найден SignTool.exe.' }
$manifestTool = Join-Path $sdkDirectory.FullName 'x64\mt.exe'
if (-not [IO.File]::Exists($manifestTool)) { throw 'В Windows SDK не найден Mt.exe.' }

& dotnet publish (Join-Path $PSScriptRoot 'App\FileExchange.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=None -o $output
if ($LASTEXITCODE -ne 0) { throw 'Не удалось собрать FileExchange.exe.' }
& dotnet publish (Join-Path $PSScriptRoot 'Manager\FileExchange.Manager.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=None -o $output
if ($LASTEXITCODE -ne 0) { throw 'Не удалось собрать WPF-приложение управления.' }
$managerManifest = Join-Path $PSScriptRoot 'artifacts\manager.manifest.xml'
& $manifestTool "-inputresource:$(Join-Path $output 'FileExchange.Manager.exe');#1" "-out:$managerManifest"
if ($LASTEXITCODE -ne 0) { throw 'Не удалось проверить манифест WPF-приложения.' }
[xml]$manifest = [IO.File]::ReadAllText($managerManifest)
if ($manifest.SelectSingleNode('//*[local-name()="requestedExecutionLevel"]').GetAttribute('level') -ne 'requireAdministrator') {
    throw 'WPF-приложение должно требовать права администратора.'
}
& dotnet publish (Join-Path $PSScriptRoot 'ShellExtension\FileExchange.ShellExtension.csproj') -c Release -r win-x64 -p:PublishAot=true -p:NativeLib=Shared -p:DebugType=None -o $output
if ($LASTEXITCODE -ne 0) { throw 'Не удалось собрать NativeAOT DLL.' }
[IO.File]::Delete((Join-Path $output 'FileExchange.ShellExtension.pdb'))
foreach ($binary in @('FileExchange.exe', 'FileExchange.Manager.exe', 'FileExchange.ShellExtension.dll')) {
    $productVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $output $binary)).ProductVersion
    if ($productVersion.Split('+')[0] -ne $version) { throw "Версия $binary ($productVersion) не совпадает с версией проекта $version." }
}

[xml]$packageManifest = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Packaging\AppxManifest.xml'))
$packageManifest.Package.Identity.Version = "$version.0"
$packageManifest.Save((Join-Path $packageDirectory 'AppxManifest.xml'))
foreach ($script in @('Install.ps1', 'Uninstall.ps1')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "Packaging\$script") -Destination (Join-Path $output $script)
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination (Join-Path $output 'README.md')

# The sparse package references assets and binaries in the external installation directory.
Add-Type -AssemblyName System.Drawing
$assetDirectory = Join-Path $output 'Assets'
[void][IO.Directory]::CreateDirectory($assetDirectory)
foreach ($size in @(44, 150)) {
    $bitmap = New-Object Drawing.Bitmap $size, $size
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $pen = New-Object Drawing.Pen ([Drawing.Color]::White), ([single]($size / 15))
    try {
        $graphics.Clear([Drawing.Color]::FromArgb(34, 102, 204))
        $graphics.DrawRectangle($pen, [single]($size * .2), [single]($size * .35), [single]($size * .6), [single]($size * .4))
        $graphics.DrawLine($pen, [single]($size * .2), [single]($size * .35), [single]($size * .2), [single]($size * .25))
        $graphics.DrawLine($pen, [single]($size * .2), [single]($size * .25), [single]($size * .45), [single]($size * .25))
        $bitmap.Save((Join-Path $assetDirectory "Logo$size.png"), [Drawing.Imaging.ImageFormat]::Png)
    }
    finally { $pen.Dispose(); $graphics.Dispose(); $bitmap.Dispose() }
}

& $makeAppx pack /o /d $packageDirectory /nv /p $packagePath
if ($LASTEXITCODE -ne 0) { throw 'Не удалось создать sparse MSIX.' }

# Generate an ephemeral key in memory. Only the public certificate is shipped.
# No certificate store or Explorer registration is modified by this build.
$rsa = [Security.Cryptography.RSACng]::new(2048)
$certificate = $null
try {
    $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=FileExchange', $rsa, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $usages = [Security.Cryptography.OidCollection]::new()
    [void]$usages.Add([Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.3'))
    $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($usages, $true))
    $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new([Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature, $true))
    $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false, $false, 0, $true))
    $certificate = $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddMinutes(-5), [DateTimeOffset]::UtcNow.AddYears(3))
    $password = [Guid]::NewGuid().ToString('N')
    [IO.File]::WriteAllBytes($privateKeyPath, $certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $password))
    [IO.File]::WriteAllBytes((Join-Path $output 'FileExchange.cer'), $certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert))
    & $signTool sign /fd SHA256 /f $privateKeyPath /p $password $packagePath
    if ($LASTEXITCODE -ne 0) { throw 'Не удалось подписать MSIX.' }
}
finally {
    if ($null -ne $certificate) { $certificate.Dispose() }
    $rsa.Dispose()
    # Delete only this known temporary file, never a recursively computed directory.
    if ([IO.File]::Exists($privateKeyPath)) { [IO.File]::Delete($privateKeyPath) }
}

Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$releaseFiles = @(
    'FileExchange.exe', 'FileExchange.Manager.exe', 'FileExchange.ShellExtension.dll', 'FileExchange.msix', 'FileExchange.cer',
    'Install.ps1', 'Uninstall.ps1', 'README.md', 'Assets/Logo44.png', 'Assets/Logo150.png'
)
[IO.File]::Delete($archivePath)
$archive = [IO.Compression.ZipFile]::Open($archivePath, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in $releaseFiles) {
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, (Join-Path $output $file), $file, [IO.Compression.CompressionLevel]::Optimal)
    }
}
finally { $archive.Dispose() }

# Verify the distributable itself; stale output files and private keys must never be shipped.
$archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
try {
    foreach ($file in $releaseFiles) {
        $entry = $archive.GetEntry($file)
        if ($null -eq $entry -or $entry.Length -eq 0) { throw "В архиве отсутствует установочный файл: $file" }
    }
    if ($archive.Entries.Count -ne $releaseFiles.Count -or @($archive.Entries | Where-Object { $_.FullName -like '*.pfx' }).Count -gt 0 -or [IO.File]::Exists($privateKeyPath)) {
        throw 'В архиве обнаружены лишние файлы или остался временный приватный ключ.'
    }
}
finally { $archive.Dispose() }

$releaseInfo = @{ Version = $version; ArchiveName = $archiveName } | ConvertTo-Json
[IO.File]::WriteAllText((Join-Path $PSScriptRoot 'artifacts\release.json'), $releaseInfo, [Text.UTF8Encoding]::new($false))
Write-Host "Версия: $version"
Write-Host "Готово: $output"
Write-Host "Проверен архив: $archivePath"
