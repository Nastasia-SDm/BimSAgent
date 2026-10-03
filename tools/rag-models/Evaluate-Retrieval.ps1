param([string]$App = 'D:\BIM-S_TestArtifacts\build\bin\BimSAgent\debug\BimSAgent.dll',
      [string]$OutputPath = 'D:\BIM-S_TestArtifacts\retrieval-evaluation.json')
$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$cases = Get-Content -Raw -Encoding UTF8 (Join-Path $repo 'tests\Rag\quality-cases.json') | ConvertFrom-Json
$env:BIMS_RERANK_URL = 'http://127.0.0.1:8766/rerank'
$env:BIMS_RERANK_MODEL = 'BAAI/bge-reranker-v2-m3'
$results = @()
foreach ($case in $cases) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $raw = & dotnet $App rag ask $case.question --strategy structural --retrieval-only --json
    if ($LASTEXITCODE -ne 0) { throw 'Retrieval command failed' }
    $answer = $raw | ConvertFrom-Json
    $hits = @($answer.questions[0].candidates | Where-Object selected)
    $names = @($hits | ForEach-Object { $_.fragment.family_names } | Select-Object -Unique)
    $missing = @($case.expected | Where-Object { $_ -notin $names })
    $wrong = @($case.excluded | Where-Object { $_ -in $names })
    $fallback = @($answer.questions[0].candidates | Where-Object { $_.reranker_mode -like 'lexical_fallback*' }).Count -gt 0
    $pass = $missing.Count -eq 0 -and $wrong.Count -eq 0 -and (-not $case.expect_empty -or $hits.Count -eq 0) -and -not $fallback
    $results += [pscustomobject]@{question=$case.question;passed=$pass;families=$names;missing=$missing;wrong=$wrong;fallback=$fallback;seconds=$watch.Elapsed.TotalSeconds;diagnostics=$answer}
    $results | ConvertTo-Json -Depth 30 | Set-Content -Encoding UTF8 -LiteralPath $OutputPath
    Write-Output ("passed={0}; seconds={1:N1}; {2}" -f $pass,$watch.Elapsed.TotalSeconds,$case.question)
}
if (@($results | Where-Object { -not $_.passed }).Count -gt 0) { exit 1 }
