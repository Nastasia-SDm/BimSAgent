"""Compare family-card dense recall only; does not overwrite production indexes."""
import json
import os
from pathlib import Path
from urllib.request import Request, urlopen

import numpy as np

ROOT = Path(__file__).resolve().parents[2]
index = json.loads(Path(r"D:\BIM-S_TestArtifacts\rag-data\structural.json").read_text(encoding="utf-8-sig"))
cases = json.loads((ROOT / "tests/Rag/quality-cases.json").read_text(encoding="utf-8"))
cards = [f for f in index["families"] if f["purpose"]]
texts = [f["canonical_name"] + "\n" + f["purpose"] for f in cards] + [c["question"] for c in cases]
report = {"scope": "dense family-card recall; no reranking or generation", "models": {}}
for model in ("text-embedding-3-small", "text-embedding-3-large"):
    vectors = []
    for start in range(0, len(texts), 16):
        body = json.dumps({"model": model, "input": texts[start:start + 16]}).encode("utf-8")
        request = Request("https://api.openai.com/v1/embeddings", body,
                          {"Content-Type": "application/json", "Authorization": "Bearer " + os.environ["OPENAI_API_KEY"]})
        with urlopen(request, timeout=120) as response:
            rows = json.load(response)["data"]
        vectors.extend(row["embedding"] for row in sorted(rows, key=lambda row: row["index"]))
    matrix = np.asarray(vectors, dtype=np.float64)
    matrix /= np.linalg.norm(matrix, axis=1, keepdims=True)
    results = []
    for i, case in enumerate(cases):
        similarities = matrix[:len(cards)] @ matrix[len(cards) + i]
        order = np.argsort(-similarities)
        names = [cards[j]["canonical_name"] for j in order[:5]]
        expected = set(case["expected"])
        results.append({"question": case["question"], "top5": names,
                        "recall5": len(expected.intersection(names)) / len(expected) if expected else None})
    report["models"][model] = results
    print(model, "mean recall@5", np.mean([r["recall5"] for r in results if r["recall5"] is not None]), flush=True)
Path(r"D:\BIM-S_TestArtifacts\embedding-comparison.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
