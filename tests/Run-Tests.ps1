$ErrorActionPreference = 'Stop'
$artifactRoot = 'D:\BIM-S\_TestArtifacts'
$testEnvironment = @{
    TEMP = "$artifactRoot\tooling-temp"
    TMP = "$artifactRoot\tooling-temp"
    DOTNET_CLI_HOME = "$artifactRoot\dotnet-home"
    DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    DOTNET_NOLOGO = '1'
}
$previous = @{}
try {
    New-Item -ItemType Directory -Force -Path $testEnvironment.TEMP,$testEnvironment.DOTNET_CLI_HOME | Out-Null
    foreach ($name in $testEnvironment.Keys) {
        $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        [Environment]::SetEnvironmentVariable($name, $testEnvironment[$name], 'Process')
    }
    & dotnet run --project (Join-Path $PSScriptRoot 'Orchestration.Tests.csproj') -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw "BimSAgent tests failed: $LASTEXITCODE" }
    # Use the existing production build without changing its output directories.
    & dotnet run --project 'D:\BIM-S-MCP-3\tests\Mcp3.Tests.csproj' -p:NuGetAudit=false -p:BuildProjectReferences=false
    if ($LASTEXITCODE -ne 0) { throw "MCP3 tests failed: $LASTEXITCODE" }
}
finally {
    foreach ($name in $previous.Keys) {
        [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process')
    }
}
