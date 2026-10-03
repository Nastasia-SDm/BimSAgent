param([string]$ArtifactRoot = 'D:\BIM-S_TestArtifacts\rag-models')
$ErrorActionPreference = 'Stop'
$toolsPath = Join-Path $ArtifactRoot 'tools'
New-Item -ItemType Directory -Force -Path $toolsPath | Out-Null
$uv = Join-Path $toolsPath 'uv.exe'
if (-not (Test-Path -LiteralPath $uv)) {
    $archive = Join-Path $toolsPath 'uv.zip'
    & curl.exe -fL --retry 2 -o $archive 'https://github.com/astral-sh/uv/releases/download/0.8.22/uv-x86_64-pc-windows-msvc.zip'
    if ($LASTEXITCODE -ne 0) { throw 'uv download failed' }
    Expand-Archive -LiteralPath $archive -DestinationPath $toolsPath -Force
}
$env:UV_CACHE_DIR = Join-Path $ArtifactRoot 'uv-cache'
$env:UV_PYTHON_INSTALL_DIR = Join-Path $ArtifactRoot 'python'
$venv = Join-Path $ArtifactRoot 'venv'
$pythonExe = Join-Path $venv 'Scripts\python.exe'
if (-not (Test-Path -LiteralPath $pythonExe)) {
    & $uv venv --python 3.12 $venv
    if ($LASTEXITCODE -ne 0) { throw 'Python installation failed' }
}
& $uv pip install --python $pythonExe --index-url 'https://download.pytorch.org/whl/cpu' 'torch==2.8.0' 'torchvision==0.23.0'
if ($LASTEXITCODE -ne 0) { throw 'PyTorch installation failed' }
& $uv pip install --python $pythonExe 'transformers==4.56.2' 'sentencepiece==0.2.1' 'easyocr==1.7.2' 'torch==2.8.0+cpu' 'torchvision==0.23.0+cpu'
if ($LASTEXITCODE -ne 0) { throw 'Model libraries installation failed' }
