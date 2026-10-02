param([string]$DocumentPath = '')
$ErrorActionPreference = 'Stop'
$artifactRoot = 'D:\BIM-S_TestArtifacts'
New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null
$env:TEMP = $artifactRoot
$env:TMP = $artifactRoot
$env:NUGET_PACKAGES = Join-Path $artifactRoot 'nuget'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $artifactRoot 'nuget-http'
$env:DOTNET_CLI_HOME = Join-Path $artifactRoot 'dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$repo = Split-Path $PSScriptRoot -Parent
Push-Location $repo
try {
    dotnet build BimSAgent.csproj --artifacts-path "$artifactRoot\build"
    if ($LASTEXITCODE -ne 0) { throw 'Application build failed.' }
    dotnet build tests/Rag/Rag.Tests.csproj --artifacts-path "$artifactRoot\build"
    if ($LASTEXITCODE -ne 0) { throw 'RAG tests build failed.' }
    $ragAssembly = "$artifactRoot\build\Rag.Tests\bin\debug\Rag.Tests.dll"
    dotnet $ragAssembly $DocumentPath
    if ($LASTEXITCODE -ne 0) { throw 'RAG tests failed.' }
    dotnet build tests/Orchestration.Tests.csproj --artifacts-path "$artifactRoot\build"
    if ($LASTEXITCODE -ne 0) { throw 'Orchestration tests build failed.' }
    dotnet "$artifactRoot\build\Orchestration.Tests\bin\debug\Orchestration.Tests.dll"
    if ($LASTEXITCODE -ne 0) { throw 'Orchestration tests failed.' }
} finally { Pop-Location }
