$ErrorActionPreference = 'Stop'
$projectDirectory = $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /codepage:65001 /target:winexe /platform:x64 /optimize+ "/out:$projectDirectory\MinecraftDownloader.exe" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll /reference:Microsoft.CSharp.dll "$projectDirectory\*.cs"
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
Write-Output "Built: $projectDirectory\MinecraftDownloader.exe"
