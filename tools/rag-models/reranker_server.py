"""Loopback-only, non-generative cross-encoder service for BIM-S.

POST /rerank: {query, texts, model, truncate:false} -> [{index, score}].
The model tokenizer controls passage windows; no silent truncation.
"""
import json
import os
from http.server import BaseHTTPRequestHandler, HTTPServer

import torch
from transformers import AutoModelForSequenceClassification, AutoTokenizer

MODEL = "BAAI/bge-reranker-v2-m3"
REVISION = os.environ.get("BIMS_RERANK_REVISION", "953dc6f6f85a1b2dbfca4c34a2796e7dde08d41e")
torch.set_num_threads(max(1, min(4, os.cpu_count() or 1)))
tokenizer = AutoTokenizer.from_pretrained(MODEL, revision=REVISION, trust_remote_code=False)
model = AutoModelForSequenceClassification.from_pretrained(
    MODEL, revision=REVISION, trust_remote_code=False, use_safetensors=True
).eval()
QUANTIZED = os.environ.get("BIMS_RERANK_INT8", "1") == "1"
if QUANTIZED:
    model = torch.ao.quantization.quantize_dynamic(model, {torch.nn.Linear}, dtype=torch.qint8, inplace=True)


def rerank(query, texts):
    query_ids = tokenizer.encode(query, add_special_tokens=False)
    if len(query_ids) > 256:
        raise ValueError("Query exceeds 256 model tokens; not truncated")
    size = 512 - len(query_ids) - tokenizer.num_special_tokens_to_add(pair=True)
    pairs, owners = [], []
    for index, text in enumerate(texts):
        tokens = tokenizer.encode(text, add_special_tokens=False)
        for start in range(0, max(1, len(tokens)), max(1, size - 64)):
            pairs.append(tokenizer.prepare_for_model(query_ids, tokens[start:start + size], truncation=False))
            owners.append(index)
            if start + size >= len(tokens):
                break
    scores = [float("-inf")] * len(texts)
    with torch.inference_mode():
        for start in range(0, len(pairs), 2):
            inputs = tokenizer.pad(pairs[start:start + 2], return_tensors="pt")
            values = model(**inputs).logits.view(-1).tolist()
            for offset, score in enumerate(values):
                owner = owners[start + offset]
                scores[owner] = max(scores[owner], score)
    return [{"index": i, "score": score} for i, score in enumerate(scores)]


class Handler(BaseHTTPRequestHandler):
    def reply(self, code, value):
        data = json.dumps(value, ensure_ascii=False).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        if self.path != "/health":
            return self.reply(404, {"error": "not_found"})
        self.reply(200, {"model": MODEL, "revision": getattr(model.config, "_commit_hash", REVISION),
                         "device": "cpu", "int8": QUANTIZED, "max_pair_tokens": 512})

    def do_POST(self):
        if self.path != "/rerank":
            return self.reply(404, {"error": "not_found"})
        try:
            length = int(self.headers.get("Content-Length", "0"))
            if length <= 0 or length > 2_000_000:
                return self.reply(413, {"error": "request_size"})
            data = json.loads(self.rfile.read(length))
            if data.get("model") != MODEL or data.get("truncate") is not False:
                raise ValueError("model/truncation configuration mismatch")
            texts, query = data["texts"], data["query"]
            if not isinstance(query, str) or not isinstance(texts, list) or not 1 <= len(texts) <= 32:
                raise ValueError("invalid batch")
            if not all(isinstance(text, str) and text for text in texts):
                raise ValueError("invalid text")
            self.reply(200, rerank(query, texts))
        except (ValueError, KeyError, TypeError):
            self.reply(400, {"error": "invalid_request_or_token_limit"})

    def log_message(self, *_):
        pass  # Never log questions or document contents.


if __name__ == "__main__":
    print("BIM-S reranker ready on 127.0.0.1:8766", flush=True)
    HTTPServer(("127.0.0.1", 8766), Handler).serve_forever()
