#Requires -Version 5.1
# Проверяет синтаксис и копирование, не изменяя реестр и установленное меню.
$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $PSScriptRoot 'Add-FileExchangeMenu.ps1'
$tokens = $null
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw ($parseErrors | Out-String) }

$copyFunction = $ast.Find({
    param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Copy-ExchangeFile'
}, $true)
if ($null -eq $copyFunction) { throw 'Функция копирования не найдена.' }
. ([scriptblock]::Create($copyFunction.Extent.Text))

$testDirectory = Join-Path $PSScriptRoot ('.file-exchange-test-' + [guid]::NewGuid().ToString('N'))
try {
    $sourceDirectory = Join-Path $testDirectory 'Источник'
    $destination = Join-Path $testDirectory "Обмен [1] & O'Brien"
    $encodedDestination = [System.Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($destination))
    $powershellExe = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    [void][System.IO.Directory]::CreateDirectory($sourceDirectory)
    foreach ($name in @('один файл [1].txt', 'two & $three.txt')) {
        $source = Join-Path $sourceDirectory $name
        [System.IO.File]::WriteAllText($source, "Содержимое: $name")
        & $powershellExe -NoLogo -NoProfile -ExecutionPolicy Bypass -File $scriptPath -DestinationBase64 $encodedDestination -SourcePath $source
        if ($LASTEXITCODE -ne 0) { throw "Вызов скрипта завершился с ошибкой: $name" }
        $target = Join-Path $destination $name
        if ([System.IO.File]::ReadAllText($source) -ne [System.IO.File]::ReadAllText($target)) {
            throw "Содержимое не совпало: $name"
        }
    }

    [System.IO.File]::WriteAllText($source, 'Новая версия')
    $refusedOverwrite = $false
    try { Copy-ExchangeFile -SourcePath $source -DestinationPath $destination }
    catch { $refusedOverwrite = $true }
    if (-not $refusedOverwrite -or [System.IO.File]::ReadAllText($target) -eq 'Новая версия') {
        throw 'Существующий файл не был защищён от перезаписи.'
    }

    $refusedMissingFile = $false
    try { Copy-ExchangeFile -SourcePath (Join-Path $sourceDirectory 'missing.txt') -DestinationPath $destination }
    catch { $refusedMissingFile = $true }
    if (-not $refusedMissingFile) { throw 'Отсутствующий исходный файл не был отклонён.' }

    Write-Host 'OK: синтаксис, несколько файлов, специальные символы, защита от перезаписи, отсутствующий файл.'
}
finally {
    # Удаляется только созданная этой проверкой папка в каталоге скрипта.
    $resolvedTestDirectory = [System.IO.Path]::GetFullPath($testDirectory)
    $allowedPrefix = [System.IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\') + '\'
    if (-not $resolvedTestDirectory.StartsWith($allowedPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'Папка проверки находится за пределами каталога скрипта.'
    }
    if ([System.IO.Directory]::Exists($resolvedTestDirectory)) {
        [System.IO.Directory]::Delete($resolvedTestDirectory, $true)
    }
}
