$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Не найден компилятор .NET Framework.' }
$source = Join-Path $PSScriptRoot 'RustCaveFinder.cs'
$destination = Join-Path $PSScriptRoot 'RustCaveFinder.exe'
& $compiler /nologo /codepage:65001 /optimize+ /platform:x64 /target:winexe "/out:$destination" /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Core.dll $source
if ($LASTEXITCODE -ne 0) { throw 'Ошибка сборки.' }
Write-Output "Готово: $destination"
