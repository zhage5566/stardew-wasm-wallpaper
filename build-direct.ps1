param(
    [string]$SdkDirectory = (Join-Path $PSScriptRoot 'bin-next')
)

$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$outDir = Join-Path $PSScriptRoot 'bin-direct'
New-Item -ItemType Directory -Path $outDir -Force | Out-Null
foreach ($name in 'Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.WinForms.dll', 'WebView2Loader.dll') {
    Copy-Item -LiteralPath (Join-Path $SdkDirectory $name) -Destination $outDir
}
$sources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' | ForEach-Object FullName)
$output = Join-Path $outDir '启动星露谷纯壁纸版.exe'
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 /warnaserror /define:DIRECT_WALLPAPER "/win32manifest:$PSScriptRoot\app.manifest" "/out:$output" /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:Accessibility.dll "/reference:$outDir\Microsoft.Web.WebView2.Core.dll" "/reference:$outDir\Microsoft.Web.WebView2.WinForms.dll" @sources
if ($LASTEXITCODE -ne 0) { throw "Compilation failed ($LASTEXITCODE)." }
Copy-Item -LiteralPath $output -Destination (Join-Path $outDir 'StardewWallpaperEngine.exe')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'project.json') -Destination $outDir
Write-Output $output
