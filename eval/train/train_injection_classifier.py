#!/usr/bin/env python3
"""
Trains the small prompt-injection classifier that ships with the gateway: logistic regression over 384-dimensional
sentence embeddings (all-MiniLM-L6-v2 served by Ollama as `all-minilm:l6-v2`, the same model the gateway embeds with).

Data (neither is committed; see eval/README.md):
  * deepset/prompt-injections (CC BY 4.0): train split for training, test split held out for evaluation
  * the internal Turkish/English set (eval/Sentinel.Evals/InjectionEval.cs): every fifth sample held out

Hyper-parameters and the decision threshold are chosen by cross-validation on the training data only; the held-out
splits are looked at once, at the end. Requires numpy and a running Ollama with `all-minilm:l6-v2`.

    dotnet run --project eval/Sentinel.Evals -- injection      # writes eval/out/injection-samples.json
    python3 eval/train/train_injection_classifier.py
"""
import hashlib
import json
import os
import sys
import urllib.request

import numpy as np

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
SAMPLES = os.path.join(ROOT, "eval", "out", "injection-samples.json")
CACHE = os.path.join(ROOT, "eval", "out", "injection-embeddings.npy")
MODEL_OUT = os.path.join(ROOT, "src", "Sentinel.Infrastructure", "AI", "Learned", "injection-model.json")
OLLAMA = os.environ.get("OLLAMA_URL", "http://localhost:11434/v1")
EMBEDDING_MODEL = "all-minilm:l6-v2"
MAX_CHARS = 2000  # must match LearnedInjectionDetector.MaxCharacters


def embed(texts):
    vectors = []
    for start in range(0, len(texts), 32):
        batch = [t[:MAX_CHARS] for t in texts[start:start + 32]]
        request = urllib.request.Request(
            f"{OLLAMA}/embeddings",
            data=json.dumps({"model": EMBEDDING_MODEL, "input": batch}).encode(),
            headers={"content-type": "application/json", "authorization": "Bearer ollama"},
        )
        data = json.load(urllib.request.urlopen(request, timeout=600))["data"]
        vectors.extend(item["embedding"] for item in sorted(data, key=lambda d: d["index"]))
        print(start, end=" ", flush=True)
    x = np.array(vectors, dtype=np.float64)
    return x / np.maximum(np.linalg.norm(x, axis=1, keepdims=True), 1e-12)


def fit(x, y, l2, epochs=1500, lr=0.1):
    """Class-balanced logistic regression, Adam on the mean loss."""
    n, d = x.shape
    w, b = np.zeros(d), 0.0
    weight = np.where(y == 1, n / (2 * max(1, y.sum())), n / (2 * max(1, (1 - y).sum())))
    mw, vw, mb, vb = np.zeros(d), np.zeros(d), 0.0, 0.0
    for t in range(1, epochs + 1):
        p = 1 / (1 + np.exp(-(x @ w + b)))
        g = (p - y) * weight
        gw, gb = x.T @ g / n + l2 * w / n, g.mean()
        mw, vw = 0.9 * mw + 0.1 * gw, 0.999 * vw + 0.001 * gw * gw
        mb, vb = 0.9 * mb + 0.1 * gb, 0.999 * vb + 0.001 * gb * gb
        w -= lr * (mw / (1 - 0.9 ** t)) / (np.sqrt(vw / (1 - 0.999 ** t)) + 1e-8)
        b -= lr * (mb / (1 - 0.9 ** t)) / (np.sqrt(vb / (1 - 0.999 ** t)) + 1e-8)
    return w, b


def score(x, w, b):
    return 1 / (1 + np.exp(-(x @ w + b)))


def metrics(pred, y):
    tp, fp = int((pred & (y == 1)).sum()), int((pred & (y == 0)).sum())
    fn, tn = int(((~pred) & (y == 1)).sum()), int(((~pred) & (y == 0)).sum())
    precision, recall = tp / max(1, tp + fp), tp / max(1, tp + fn)
    return {
        "attacks": tp + fn, "benign": fp + tn, "tp": tp, "fp": fp, "fn": fn, "tn": tn,
        "precision": round(precision, 4), "recall": round(recall, 4),
        "f1": round(2 * precision * recall / max(1e-12, precision + recall), 4),
        "falsePositiveRate": round(fp / max(1, fp + tn), 4),
    }


def main():
    rows = json.load(open(SAMPLES, encoding="utf-8"))
    texts = [r["text"] for r in rows]
    y = np.array([1 if r["attack"] else 0 for r in rows])
    heuristic = np.array([bool(r["heuristic"]) for r in rows])
    sets = np.array([r["set"] for r in rows])

    if os.path.exists(CACHE) and np.load(CACHE).shape[0] == len(rows):
        x = np.load(CACHE)
    else:
        x = embed(texts)
        np.save(CACHE, x)
    print(f"\nembedded {x.shape}")

    own_index = np.where(sets == "own")[0]
    own_test = own_index[::5]
    train_mask = (sets == "deepset-train")
    train_mask[np.setdiff1d(own_index, own_test)] = True
    test_masks = {
        "deepset-test": sets == "deepset-test",
        "own-heldout": np.isin(np.arange(len(rows)), own_test),
    }
    xt, yt = x[train_mask], y[train_mask]
    print(f"training on {len(yt)} samples ({int(yt.sum())} attacks)")

    # Cross-validation on the training data only: pick l2 and a threshold with at most 3% false positives.
    rng = np.random.default_rng(7)
    folds = rng.permutation(len(yt)) % 5
    best = None
    for l2 in (0.03, 0.1, 0.3, 1.0, 3.0):
        oof = np.zeros(len(yt))
        for k in range(5):
            w, b = fit(xt[folds != k], yt[folds != k], l2)
            oof[folds == k] = score(xt[folds == k], w, b)
        for threshold in np.arange(0.30, 0.91, 0.05):
            m = metrics(oof >= threshold, yt)
            if m["falsePositiveRate"] <= 0.03 and (best is None or m["f1"] > best[0]):
                best = (m["f1"], l2, round(float(threshold), 2), m)
    _, l2, threshold, cv = best
    print(f"chosen by 5-fold CV on training data: l2={l2}, threshold={threshold}, cv={cv}")

    w, b = fit(xt, yt, l2)
    report = {}
    for name, mask in test_masks.items():
        s = score(x[mask], w, b)
        learned = s >= threshold
        report[name] = {
            "learned": metrics(learned, y[mask]),
            "heuristicOnly": metrics(heuristic[mask], y[mask]),
            "learnedOrHeuristic": metrics(learned | heuristic[mask], y[mask]),
        }
        print(name)
        for key, value in report[name].items():
            print(f"  {key:20s} {value}")

    model = {
        "embeddingModel": EMBEDDING_MODEL,
        "dimensions": int(x.shape[1]),
        "maxCharacters": MAX_CHARS,
        "threshold": threshold,
        "bias": float(b),
        "weights": [round(float(v), 6) for v in w],
        "training": {
            "samples": int(len(yt)), "attacks": int(yt.sum()), "l2": l2,
            "data": ["deepset/prompt-injections train split (CC BY 4.0, Hugging Face)", "internal Turkish/English set (80%)"],
            "samplesSha256": hashlib.sha256(json.dumps(texts, ensure_ascii=False).encode()).hexdigest(),
            "crossValidation": cv,
        },
        "heldOutEvaluation": report,
        "limits": "Short prompts only (trained on user prompts, not on 300-token document chunks). German and English "
                  "dominate the public data, Turkish comes from the small internal set.",
    }
    os.makedirs(os.path.dirname(MODEL_OUT), exist_ok=True)
    json.dump(model, open(MODEL_OUT, "w", encoding="utf-8"), ensure_ascii=False, indent=1)
    print("->", MODEL_OUT)


if __name__ == "__main__":
    sys.exit(main())
