#Requires -Version 5.1
[CmdletBinding()]
param([string]$InstallDirectory = $PSScriptRoot)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$OutputEncoding = [Console]::OutputEncoding
foreach ($package in @(Get-AppxPackage -Name FileExchange | Where-Object { $_.Publisher -eq 'CN=FileExchange' })) {
    Remove-AppxPackage -Package $package.PackageFullName -ErrorAction Stop
}
[Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree('Software\Classes\*\shell\FileExchange.Menu', $false)
[Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree('Software\Classes\CLSID\{B687DD84-18AA-4A7A-B6D3-3B4A4FBA2F92}', $false)
$markerPath = Join-Path ([IO.Path]::GetFullPath($InstallDirectory)) '.registered-package.sha256'
if ([IO.File]::Exists($markerPath)) { [IO.File]::Delete($markerPath) }
$certificatePath = Join-Path ([IO.Path]::GetFullPath($InstallDirectory)) 'FileExchange.cer'
if ([IO.File]::Exists($certificatePath)) {
    $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($certificatePath)
    $store = [Security.Cryptography.X509Certificates.X509Store]::new('TrustedPeople', 'CurrentUser')
    try {
        if ($certificate.Subject -eq 'CN=FileExchange') {
            $store.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
            foreach ($trusted in $store.Certificates.Find([Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint, $certificate.Thumbprint, $false)) {
                $store.Remove($trusted)
            }
        }
    }
    finally { $store.Dispose(); $certificate.Dispose() }

    $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($certificatePath)
    $machineStore = [Security.Cryptography.X509Certificates.X509Store]::new('TrustedPeople', 'LocalMachine')
    try {
        if ($certificate.Subject -eq 'CN=FileExchange') {
            $machineStore.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly)
            $matching = $machineStore.Certificates.Find([Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint, $certificate.Thumbprint, $false)
            if ($matching.Count -gt 0) {
                $principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
                if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
                    $machineStore.Close()
                    $machineStore.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
                    foreach ($trusted in $matching) { $machineStore.Remove($trusted) }
                }
                else {
                    Write-Warning 'Сертификат этой сборки остался в LocalMachine\TrustedPeople. Для удаления повторите .\FileExchange.exe --uninstall из PowerShell, запущенного от имени администратора.'
                }
            }
        }
    }
    finally { $machineStore.Dispose(); $certificate.Dispose() }
}
if (-not ('FileExchangeShellNotification' -as [type])) {
    Add-Type 'using System; using System.Runtime.InteropServices; public static class FileExchangeShellNotification { [DllImport("shell32.dll")] public static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2); }'
}
[FileExchangeShellNotification]::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero)
Write-Host 'Расширение «Файловый обмен» удалено. Настройки папок сохранены.'
