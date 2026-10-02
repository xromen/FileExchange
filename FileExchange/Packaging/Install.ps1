#Requires -Version 5.1
[CmdletBinding()]
param([string]$InstallDirectory = $PSScriptRoot)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$OutputEncoding = [Console]::OutputEncoding
Set-StrictMode -Version Latest

$InstallDirectory = [IO.Path]::GetFullPath($InstallDirectory)
$dllPath = Join-Path $InstallDirectory 'FileExchange.ShellExtension.dll'
foreach ($file in @('FileExchange.exe', 'FileExchange.ShellExtension.dll', 'FileExchange.msix', 'FileExchange.cer')) {
    if (-not [IO.File]::Exists((Join-Path $InstallDirectory $file))) { throw "Не найден установочный файл: $file" }
}

$classId = '{B687DD84-18AA-4A7A-B6D3-3B4A4FBA2F92}'
$classKeyPath = "Software\Classes\CLSID\$classId"
$menuKeyPath = 'Software\Classes\*\shell\FileExchange.Menu'

if ([Environment]::OSVersion.Version.Build -ge 22000) {
    $packagePath = Join-Path $InstallDirectory 'FileExchange.msix'
    $certificatePath = Join-Path $InstallDirectory 'FileExchange.cer'
    $markerPath = Join-Path $InstallDirectory '.registered-package.sha256'
    $packageHash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash
    $existing = @(Get-AppxPackage -Name FileExchange | Where-Object { $_.Publisher -eq 'CN=FileExchange' })
    $registered = $existing.Count -gt 0 -and [IO.File]::Exists($markerPath) -and [IO.File]::ReadAllText($markerPath).Trim() -eq $packageHash
    if (-not $registered) {
        # Explicit installation trusts only this package's public signing certificate for the current user.
        $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($certificatePath)
        $store = [Security.Cryptography.X509Certificates.X509Store]::new('TrustedPeople', 'CurrentUser')
        try {
            if ($certificate.Subject -ne 'CN=FileExchange') { throw 'Сертификат не соответствует издателю FileExchange.' }
            Add-Type -AssemblyName System.IO.Compression.FileSystem
            Add-Type -AssemblyName System.Security
            $archive = [IO.Compression.ZipFile]::OpenRead($packagePath)
            try {
                $signatureEntry = $archive.GetEntry('AppxSignature.p7x')
                if ($null -eq $signatureEntry) { throw 'MSIX не подписан.' }
                $stream = $signatureEntry.Open()
                $buffer = [IO.MemoryStream]::new()
                try { $stream.CopyTo($buffer); $signature = $buffer.ToArray() }
                finally { $buffer.Dispose(); $stream.Dispose() }
                if ($signature.Length -lt 5 -or [Text.Encoding]::ASCII.GetString($signature, 0, 4) -ne 'PKCX') { throw 'Неверная подпись MSIX.' }
                $cms = [Security.Cryptography.Pkcs.SignedCms]::new()
                $cms.Decode([byte[]]$signature[4..($signature.Length - 1)])
                $cms.CheckSignature($true)
                if ($cms.SignerInfos.Count -ne 1 -or $cms.SignerInfos[0].Certificate.Thumbprint -ne $certificate.Thumbprint) {
                    throw 'Подпись MSIX не соответствует приложенному сертификату.'
                }
                $validatedCertificateBytes = $certificate.RawData
            }
            finally { $archive.Dispose() }
            $store.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
            if ($store.Certificates.Find([Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint, $certificate.Thumbprint, $false).Count -eq 0) {
                $store.Add($certificate)
            }
        }
        finally { $store.Dispose(); $certificate.Dispose() }
        foreach ($package in $existing) { Remove-AppxPackage -Package $package.PackageFullName -ErrorAction Stop }
        try {
            Add-AppxPackage -Path $packagePath -ExternalLocation $InstallDirectory -ErrorAction Stop
        }
        catch {
            $deploymentError = $_
            $details = $deploymentError.ToString() + "`n" + $deploymentError.Exception.ToString()
            if ($deploymentError.Exception.HResult -ne -2146762487 -and $details -notmatch '0x800B0109|CERT_E_UNTRUSTEDROOT') { throw }

            $principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
            if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
                throw "Windows не доверяет подписи MSIX через CurrentUser\TrustedPeople (0x800B0109). Запустите PowerShell от имени администратора и повторите: .\FileExchange.exe --install. Для регистрации этой сборки требуется доверие сертификату в LocalMachine\TrustedPeople.`r`n$details"
            }

            # Only an explicitly elevated installation may trust the verified certificate for the machine.
            $machineCertificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new([byte[]]$validatedCertificateBytes)
            $machineStore = [Security.Cryptography.X509Certificates.X509Store]::new('TrustedPeople', 'LocalMachine')
            try {
                $machineStore.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
                if ($machineStore.Certificates.Find([Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint, $machineCertificate.Thumbprint, $false).Count -eq 0) {
                    $machineStore.Add($machineCertificate)
                }
            }
            finally { $machineStore.Dispose(); $machineCertificate.Dispose() }
            Add-AppxPackage -Path $packagePath -ExternalLocation $InstallDirectory -ErrorAction Stop
        }
        [IO.File]::WriteAllText($markerPath, $packageHash)
    }

    # If this user upgraded from Windows 10, remove only our legacy verb to avoid duplicate entries.
    [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($menuKeyPath, $false)
    [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($classKeyPath, $false)
}
else {
    # Windows 10 uses the same COM command with the full IShellItemArray selection.
    $classKey = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey("$classKeyPath\InprocServer32")
    try { $classKey.SetValue('', $dllPath); $classKey.SetValue('ThreadingModel', 'Apartment') }
    finally { $classKey.Dispose() }
    $menuKey = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($menuKeyPath)
    try {
        $menuKey.SetValue('MUIVerb', 'Файловый обмен')
        $menuKey.SetValue('ExplorerCommandHandler', $classId)
        $menuKey.SetValue('MultiSelectModel', 'Player')
    }
    finally { $menuKey.Dispose() }
}

if (-not ('FileExchangeShellNotification' -as [type])) {
    Add-Type 'using System; using System.Runtime.InteropServices; public static class FileExchangeShellNotification { [DllImport("shell32.dll")] public static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2); }'
}
[FileExchangeShellNotification]::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero)
Write-Host 'Расширение «Файловый обмен» установлено для текущего пользователя.'
