#Requires -Version 5.1
$ErrorActionPreference = 'Stop'

# Exercise the actual installer catch block without running installation or trusting certificates.
$errors = $null
$tokens = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Install.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count -gt 0) { throw $errors[0].Message }
$tryAst = $ast.FindAll({
    param($node)
    $node -is [Management.Automation.Language.TryStatementAst] -and $node.CatchClauses.Count -gt 0 -and $node.Body.Extent.Text -match 'Add-AppxPackage'
}, $true)[0]
$catchSource = ($tryAst.CatchClauses[0].Body.Statements | ForEach-Object { $_.Extent.Text }) -join "`n"
# Hosted Windows runners are administrators: force only the role check to false before executing any sample.
$adminCheck = '$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)'
if (-not $catchSource.Contains($adminCheck)) { throw 'В установщике изменилось условие повышения прав; проверку нужно обновить.' }
$catchSource = $catchSource.Replace($adminCheck, '$false')
$run = [ScriptBlock]::Create("param([Exception] `$sample)`ntry { throw `$sample } catch {`n" + $catchSource + "`n}")

foreach ($sample in @(
    [Runtime.InteropServices.COMException]::new('Untrusted root', -2146762487),
    [InvalidOperationException]::new('Outer error', [Runtime.InteropServices.COMException]::new('CERT_E_UNTRUSTEDROOT 0x800B0109', -2146762487))
)) {
    $caught = $null
    try { & $run $sample } catch { $caught = $_.Exception.Message }
    if ($caught -notmatch 'LocalMachine' -or $caught -notmatch '--install') { throw 'Не показана инструкция об установке от администратора.' }
}
$caught = $null
try { & $run ([Runtime.InteropServices.COMException]::new('AccessDenied-SyntheticTest', -2147024891)) } catch { $caught = $_.Exception.Message }
if ($caught -match 'LocalMachine' -or $caught -notmatch 'AccessDenied-SyntheticTest') { throw 'Посторонняя ошибка не передана вызывающему коду.' }
Write-Host 'OK: ошибка доверия требует явных прав администратора; прочие ошибки переданы без подмены. Установка не выполнялась.'
