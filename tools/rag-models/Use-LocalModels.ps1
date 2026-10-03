# Dot-source this file in the shell running BIM-S. No secrets are written to disk.
$env:BIMS_RERANK_URL = 'http://127.0.0.1:8766/rerank'
$env:BIMS_RERANK_MODEL = 'BAAI/bge-reranker-v2-m3'
$env:BIMS_ASSET_URL = 'http://127.0.0.1:8767/assets'
$env:BIMS_ASSET_VERSION = 'easyocr-1.7.2-ru-en-v1'
