#!/usr/bin/env python3
"""Merge the LoRA adapter into the base weights (nl-assist-finetune plan phase 3, spec Stage 3).

Loads the base model in fp16 (NOT 4-bit - merging into a quantized base is lossy), layers
the adapter on, folds it in with merge_and_unload(), and writes a standalone fp16 model +
tokenizer. That directory is what llama.cpp converts to GGUF next (spec Stage 4).

Usage:  python merge.py --config train-config.phi4-f8.json --adapter ../adapter --out ../merged
        (--config defaults to the mini; pass the phi4-f8 config to merge the 14B adapter or the
         base won't match. run.sh passes --config for you - this matters only for manual runs.)
"""

import argparse
import json
import sys
from pathlib import Path


def main() -> None:
    here = Path(__file__).resolve().parent
    ap = argparse.ArgumentParser()
    ap.add_argument("--config", type=Path, default=here / "train-config.phi4-f8-mini.json")
    ap.add_argument("--adapter", type=Path, default=here / "../adapter")
    ap.add_argument("--out", type=Path, default=here / "../merged")
    args = ap.parse_args()

    if not args.adapter.exists():
        sys.exit(f"Adapter not found at {args.adapter}. Run train_lora.py first.")

    cfg = {k: v for k, v in json.loads(args.config.read_text()).items() if not k.startswith("$")}

    import torch
    from peft import PeftModel
    from transformers import AutoModelForCausalLM, AutoTokenizer

    base = cfg["baseModel"]
    # Merge on CPU: adapter merge isn't GPU-bound, and a 14B in fp16 (~28GB) doesn't fit a 24GB
    # GPU - device_map="auto" spills to CPU but merge_and_unload then on-loads layers back to the
    # GPU and OOMs. Loading on CPU (no device_map) avoids that; the training box has ample RAM.
    print(f"loading base {base} in fp16 on CPU (merge is CPU-bound; keeps a 14B off the GPU)")
    model = AutoModelForCausalLM.from_pretrained(
        base, torch_dtype=torch.float16, device_map=None, low_cpu_mem_usage=True, trust_remote_code=True
    )
    print(f"applying adapter {args.adapter}")
    model = PeftModel.from_pretrained(model, str(args.adapter))
    print("merging adapter into base weights (CPU)")
    model = model.merge_and_unload()

    args.out.mkdir(parents=True, exist_ok=True)
    model.save_pretrained(str(args.out), safe_serialization=True)
    AutoTokenizer.from_pretrained(str(args.adapter), trust_remote_code=True).save_pretrained(str(args.out))
    print(f"merged fp16 model written to {args.out}")


if __name__ == "__main__":
    main()
