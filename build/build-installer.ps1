param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'
$dist = Join-Path $root 'dist'
$appProject = Join-Path $root '_decompile\SymbolsTool.csproj'
$setupProject = Join-Path $root 'src\SymbolsTool.Installer\SymbolsTool.Installer.csproj'
$uninstallerSource = Join-Path $root 'src\SymbolsTool.Uninstaller\Program.cs'
$appIcon = Join-Path $root '_decompile\app.ico'

Write-Host '[1/5] 清理输出目录...'
if (Test-Path $artifacts) { Remove-Item $artifacts -Recurse -Force }
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Path $artifacts | Out-Null
New-Item -ItemType Directory -Path $dist | Out-Null

Write-Host '[2/5] 发布主程序（自包含，含 .NET 运行时）...'
$appOut = Join-Path $artifacts 'app'
& dotnet publish $appProject -c $Configuration -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false `
    -p:GenerateDocumentationFile=false --nologo -o $appOut
if ($LASTEXITCODE -ne 0) { throw '主程序发布失败' }

Get-ChildItem $appOut -Filter '*.pdb' -Recurse | Remove-Item -Force

Write-Host '[3/5] 打包安装载荷...'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$payload = Join-Path $artifacts 'payload.zip'
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $appOut, $payload, [System.IO.Compression.CompressionLevel]::Optimal, $false)

Write-Host '[4/5] 编译卸载程序（.NET Framework 4.8）...'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "未找到 .NET Framework 编译器：$csc" }
$uninstaller = Join-Path $artifacts 'Uninstall.exe'
& $csc /nologo /target:winexe /optimize+ /platform:anycpu /win32icon:$appIcon `
    /out:$uninstaller /r:System.Windows.Forms.dll /r:System.Drawing.dll $uninstallerSource
if ($LASTEXITCODE -ne 0) { throw '卸载程序编译失败' }

Write-Host '[5/5] 编译安装程序（自包含单文件）...'
# 安装程序工程按相对路径引用 src\SymbolsTool\AppIcon.ico，缺失时从主程序图标补齐
$installerIcon = Join-Path $root 'src\SymbolsTool\AppIcon.ico'
if (-not (Test-Path $installerIcon)) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $installerIcon) | Out-Null
    Copy-Item $appIcon $installerIcon -Force
}
$setupOut = Join-Path $artifacts 'setup'
& dotnet publish $setupProject -c $Configuration -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None -p:DebugSymbols=false `
    "-p:PayloadZip=$payload" "-p:UninstallerExe=$uninstaller" `
    --nologo -o $setupOut
if ($LASTEXITCODE -ne 0) { throw '安装程序编译失败' }

Copy-Item (Join-Path $setupOut 'SymbolsToolSetup.exe') (Join-Path $dist '符号工具安装程序.exe') -Force

$sizeMb = [math]::Round((Get-Item (Join-Path $dist '符号工具安装程序.exe')).Length / 1MB, 1)
Write-Host ''
Write-Host "完成：$(Join-Path $dist '符号工具安装程序.exe')（$sizeMb MB）"
