#!/usr/bin/env python3
"""LoRA fine-tune of the NL-assist base model (nl-assist-finetune plan phase 3, spec Stage 2).

Trains a small LoRA adapter on the generated (intent -> fragment) dataset so the model's
FIRST-pass drafts land on Fallen-8's exact delegate surface. 4-bit QLoRA, sized for a
single 8GB+ NVIDIA GPU under WSL2. Deterministic: same dataset + train-config.json + seed
=> equivalent adapter (spec FT-1). Never trains in-process in F8 - this is an offline
operator tool (spec non-goals).

Loss is computed on the assistant fragment only (completion-only collator): the shared,
verbose system prompt is context, not a target. The one model-revision-sensitive knob is
`responseTemplate` in the config - run with --inspect FIRST to see the rendered chat and
confirm the marker before spending GPU hours.

Usage (inside the WSL2 venv):
  python train_lora.py --inspect                         # render one example, verify marker
  python train_lora.py --dataset ../dataset/train.jsonl --out ../adapter
"""

import argparse
import json
import os
import sys
from pathlib import Path


def load_config(path: Path) -> dict:
    cfg = json.loads(path.read_text())
    # Strip the "$"-prefixed doc keys so the config reads cleanly as data.
    return {k: v for k, v in cfg.items() if not k.startswith("$")}


def probe_gpu() -> None:
    import torch

    if not torch.cuda.is_available():
        sys.exit(
            "No CUDA GPU visible to PyTorch. QLoRA on a 3.8B base needs an NVIDIA GPU;\n"
            "on WSL2 confirm `nvidia-smi` works and torch was installed from the cu12x index\n"
            "(see requirements.txt). CPU training is not supported by this script."
        )
    name = torch.cuda.get_device_name(0)
    total_gb = torch.cuda.get_device_properties(0).total_memory / 1024**3
    print(f"GPU: {name} ({total_gb:.1f} GB VRAM), torch {torch.__version__}")
    if total_gb < 7.5:
        print("  ! <8GB VRAM: if you hit OOM, set training.perDeviceBatchSize to 1 in the config.")


def build_texts(dataset_path: Path, tokenizer):
    """Load JSONL rows and render each conversation to a single training string.

    Reads the generated corpus AND, if present, dataset/captured.jsonl - the consolidated
    real-usage feedback from FL-3 (feature nl-assist-feedback-loop). Same row shape, so they
    train together; captures are absent on a fresh checkout and simply add nothing.
    """
    from datasets import load_dataset

    data_files = [str(dataset_path)]
    captured = dataset_path.parent / "captured.jsonl"
    if captured.exists():
        data_files.append(str(captured))
        print(f"including {captured.name} (consolidated feedback captures)")

    ds = load_dataset("json", data_files=data_files, split="train")

    def render(row):
        return {"text": tokenizer.apply_chat_template(row["messages"], tokenize=False)}

    return ds.map(render, remove_columns=ds.column_names)


def main() -> None:
    ap = argparse.ArgumentParser()
    here = Path(__file__).resolve().parent
    ap.add_argument("--config", type=Path, default=here / "train-config.json")
    ap.add_argument("--dataset", type=Path, default=here / "../dataset/train.jsonl")
    ap.add_argument("--out", type=Path, default=here / "../adapter")
    ap.add_argument("--inspect", action="store_true", help="render one example and exit")
    args = ap.parse_args()

    cfg = load_config(args.config)
    if not args.dataset.exists():
        sys.exit(f"Dataset not found at {args.dataset}. Run the phase-2 generator first.")

    import torch
    from transformers import AutoModelForCausalLM, AutoTokenizer, BitsAndBytesConfig, set_seed

    set_seed(cfg["seed"])
    torch.manual_seed(cfg["seed"])

    base = cfg["baseModel"]
    tokenizer = AutoTokenizer.from_pretrained(base, trust_remote_code=True)
    if tokenizer.pad_token is None:
        tokenizer.pad_token = tokenizer.eos_token

    texts = build_texts(args.dataset, tokenizer)
    response_template = cfg["responseTemplate"]

    if args.inspect:
        example = texts[0]["text"]
        print("=== rendered chat (row 0) ===")
        print(example)
        marker_at = example.find(response_template)
        print("\n=== response marker check ===")
        print(f"responseTemplate = {response_template!r}")
        if marker_at < 0:
            print("  !! marker NOT found in the rendered chat - training would compute loss on")
            print("     the whole sequence. Set responseTemplate to the exact assistant opener")
            print("     shown above (the text just before the fragment).")
        else:
            print(f"  ok: found at char {marker_at}; loss is masked up to and including it.")
        print(f"\nrows: {len(texts)}")
        return

    probe_gpu()

    quant = cfg["quantize"]
    bnb = BitsAndBytesConfig(
        load_in_4bit=quant["load4bit"],
        bnb_4bit_quant_type=quant["bnbQuantType"],
        bnb_4bit_compute_dtype=getattr(torch, quant["computeDtype"]),
        bnb_4bit_use_double_quant=quant["useDoubleQuant"],
    )
    model = AutoModelForCausalLM.from_pretrained(
        base,
        quantization_config=bnb,
        device_map="auto",
        trust_remote_code=True,
        # flash-attn isn't a pinned dep; eager attention works everywhere.
        attn_implementation=os.environ.get("ATTN_IMPL", "eager"),
    )

    from peft import LoraConfig, prepare_model_for_kbit_training
    from trl import DataCollatorForCompletionOnlyLM, SFTConfig, SFTTrainer

    train = cfg["training"]
    model = prepare_model_for_kbit_training(
        model, use_gradient_checkpointing=train["gradientCheckpointing"]
    )
    lora = LoraConfig(
        r=cfg["lora"]["r"],
        lora_alpha=cfg["lora"]["alpha"],
        lora_dropout=cfg["lora"]["dropout"],
        target_modules=cfg["lora"]["targetModules"],
        bias="none",
        task_type="CAUSAL_LM",
    )
    collator = DataCollatorForCompletionOnlyLM(response_template, tokenizer=tokenizer)

    sft = SFTConfig(
        output_dir=str(args.out),
        num_train_epochs=train["epochs"],
        per_device_train_batch_size=train["perDeviceBatchSize"],
        gradient_accumulation_steps=train["gradAccumSteps"],
        learning_rate=train["learningRate"],
        lr_scheduler_type=train["lrScheduler"],
        warmup_ratio=train["warmupRatio"],
        weight_decay=train["weightDecay"],
        logging_steps=train["loggingSteps"],
        optim=train["optim"],
        gradient_checkpointing=train["gradientCheckpointing"],
        # Use non-reentrant checkpointing (torch's recommended mode; silences the 2.5+ warning
        # about the default flipping). Ignored when gradient_checkpointing is off.
        gradient_checkpointing_kwargs={"use_reentrant": False},
        bf16=True,
        max_seq_length=cfg["maxSeqLength"],
        dataset_text_field="text",
        packing=False,
        seed=cfg["seed"],
        report_to="none",
    )

    effective = train["perDeviceBatchSize"] * train["gradAccumSteps"]
    print(f"training {len(texts)} rows, {train['epochs']} epochs, effective batch {effective}")

    trainer = SFTTrainer(
        model=model,
        args=sft,
        train_dataset=texts,
        peft_config=lora,
        data_collator=collator,
        processing_class=tokenizer,
    )
    trainer.train()
    trainer.save_model(str(args.out))
    tokenizer.save_pretrained(str(args.out))
    print(f"\nadapter written to {args.out}")


if __name__ == "__main__":
    main()
