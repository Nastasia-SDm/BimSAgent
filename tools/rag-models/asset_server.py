"""Local OCR endpoint. Images stay local; diagram interpretation is explicitly absent."""
import base64
import hashlib
import io
import json
import os
from pathlib import Path
from http.server import BaseHTTPRequestHandler, HTTPServer

import easyocr
import numpy as np
import torch
from PIL import Image, UnidentifiedImageError

ROOT = Path(os.environ.get("BIMS_MODEL_ROOT", r"D:\BIM-S_TestArtifacts\rag-models"))
VERSION = "easyocr-1.7.2-ru-en-v1"
torch.set_num_threads(2)
reader = easyocr.Reader(["ru", "en"], gpu=False, model_storage_directory=str(ROOT / "ocr-weights"), verbose=False)
CACHE = ROOT / "ocr-cache" / VERSION
CACHE.mkdir(parents=True, exist_ok=True)


class Handler(BaseHTTPRequestHandler):
    def reply(self, status, value):
        data = json.dumps(value, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        self.reply(200 if self.path == "/health" else 404,
                   {"processor_version": VERSION, "ocr": "ru,en", "vision": "unavailable"})

    def do_POST(self):
        if self.path != "/assets":
            return self.reply(404, {"error": "not_found"})
        try:
            length = int(self.headers.get("Content-Length", "0"))
            if not 0 < length <= 30_000_000:
                return self.reply(413, {"error": "request_size"})
            data = json.loads(self.rfile.read(length))
            if data["processor_version"] != VERSION:
                raise ValueError("version mismatch")
            media = base64.b64decode(data["image_base64"], validate=True)
            digest = hashlib.sha256(media).hexdigest()
            if digest != data["content_hash"]:
                raise ValueError("hash mismatch")
            cache = CACHE / (digest + ".json")
            if cache.exists():
                return self.reply(200, json.loads(cache.read_text(encoding="utf-8")))
            with Image.open(io.BytesIO(media)) as image:
                if getattr(image, "n_frames", 1) > 1:
                    return self.reply(200, {"ocr_text": "", "vision_text": "", "status": "unsupported_animation"})
                results = reader.readtext(np.asarray(image.convert("RGB")), detail=1, paragraph=False)
            # Low confidence is visible, never silently corrected into a dimension or identifier.
            lines = [(text if confidence >= 0.8 else "[неуверенное OCR] " + text) for _, text, confidence in results]
            value = {"ocr_text": "\n".join(lines), "vision_text": "", "status": "ocr_only_unverified",
                     "processor_version": VERSION, "vision_status": "unavailable"}
            temporary = cache.with_suffix(".tmp")
            temporary.write_text(json.dumps(value, ensure_ascii=False), encoding="utf-8")
            temporary.replace(cache)
            self.reply(200, value)
        except (UnidentifiedImageError, OSError):
            self.reply(200, {"ocr_text": "", "vision_text": "", "status": "unsupported_format"})
        except (ValueError, KeyError, TypeError):
            self.reply(400, {"error": "invalid_request"})

    def log_message(self, *_):
        pass


if __name__ == "__main__":
    print("BIM-S OCR ready on 127.0.0.1:8767", flush=True)
    HTTPServer(("127.0.0.1", 8767), Handler).serve_forever()
