param([string]$ArtifactRoot = 'D:\BIM-S_TestArtifacts\rag-models')
$ErrorActionPreference = 'Stop'
$pythonExe = Join-Path $ArtifactRoot 'venv\Scripts\python.exe'
if (-not (Test-Path -LiteralPath $pythonExe)) { throw 'Python environment not installed. See README.' }
$env:HF_HOME = Join-Path $ArtifactRoot 'huggingface'
$env:HF_HUB_DISABLE_SYMLINKS_WARNING = '1'
$env:TOKENIZERS_PARALLELISM = 'false'
$scriptPath = Join-Path $PSScriptRoot 'reranker_server.py'
$existing = $null
try { $existing = Invoke-RestMethod 'http://127.0.0.1:8766/health' -TimeoutSec 2 } catch { }
if ($existing) { $existing; return }
$process = Start-Process -FilePath $pythonExe -ArgumentList @('"' + $scriptPath + '"') -WindowStyle Hidden -PassThru `
    -RedirectStandardOutput (Join-Path $ArtifactRoot 'reranker.stdout.log') `
    -RedirectStandardError (Join-Path $ArtifactRoot 'reranker.stderr.log')
Write-Output "Starting reranker, PID $($process.Id). Health: http://127.0.0.1:8766/health"
