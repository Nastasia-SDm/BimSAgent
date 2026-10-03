param([string]$ArtifactRoot = 'D:\BIM-S_TestArtifacts\rag-models')
$ErrorActionPreference = 'Stop'
$pythonExe = Join-Path $ArtifactRoot 'venv\Scripts\python.exe'
$env:BIMS_MODEL_ROOT = $ArtifactRoot
$existing = $null
try { $existing = Invoke-RestMethod 'http://127.0.0.1:8767/health' -TimeoutSec 2 } catch { }
if ($existing) { $existing; return }
$scriptPath = Join-Path $PSScriptRoot 'asset_server.py'
$process = Start-Process -FilePath $pythonExe -ArgumentList @('"' + $scriptPath + '"') -WindowStyle Hidden -PassThru `
    -RedirectStandardOutput (Join-Path $ArtifactRoot 'ocr.stdout.log') `
    -RedirectStandardError (Join-Path $ArtifactRoot 'ocr.stderr.log')
Write-Output "Starting OCR, PID $($process.Id). Health: http://127.0.0.1:8767/health"
