param([string]$UnityPackage = (Join-Path $PSScriptRoot '../Project/Packages/com.counull.clientmcp'))
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '../DotNet/ClientMcp.Scripting/ClientMcp.Scripting.csproj'
dotnet build $project -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Roslyn 构建失败。' }
$source = Join-Path $PSScriptRoot '../DotNet/ClientMcp.Scripting/bin/Release/netstandard2.0'
$destination = Join-Path $UnityPackage 'Plugins/Roslyn'
New-Item -ItemType Directory -Path $destination -Force | Out-Null
foreach ($obsolete in @('Microsoft.CodeAnalysis.Scripting.dll', 'Microsoft.CodeAnalysis.CSharp.Scripting.dll', 'Microsoft.CSharp.dll')) {
    $obsoletePath = Join-Path $destination $obsolete
    if (Test-Path -LiteralPath $obsoletePath) { Remove-Item -LiteralPath $obsoletePath }
    if (Test-Path -LiteralPath ($obsoletePath + '.meta')) { Remove-Item -LiteralPath ($obsoletePath + '.meta') }
}
# Unity 已提供 Newtonsoft 和 Core；不复制以免重复程序集。其余依赖随官方 NuGet 闭包部署。
Get-ChildItem -LiteralPath $source -Filter '*.dll' | Where-Object { $_.Name -notin @('ClientMcp.Core.dll', 'Newtonsoft.Json.dll', 'Microsoft.CSharp.dll', 'Microsoft.CodeAnalysis.Scripting.dll', 'Microsoft.CodeAnalysis.CSharp.Scripting.dll') } | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $destination $_.Name) -Force
}
Write-Output "Roslyn 已安装到 $destination；Unity 导入后通过 --clientmcp --clientmcp-scripts 显式启用。"
