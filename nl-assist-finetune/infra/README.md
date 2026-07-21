# Azure A10 fine-tune runner (phi4-f8-mini + phi4-f8)

One command spins up an NVIDIA **A10** VM on Azure, installs the whole fine-tune toolchain,
runs the [nl-assist-finetune](../) pipeline for **both variants in one session** —
`phi4-f8-mini` and `phi4-f8` (the full-Phi-4 14B your local GPU can't fit) — publishes each to
Ollama, and then **deletes the entire resource group itself**. You only watch. The dataset is
generated once and shared across both variants. The VM is **on-demand** by default (Spot A10
quota is usually unavailable); set `F8_SPOT=1` to try Spot.

## What gets created

A dedicated resource group `rg-f8-finetune-<rand>` containing: a `Standard_NV36ads_A10_v5`
VM (full A10, 24 GB; on-demand), a vnet/subnet/NSG (SSH from your IP only), a public IP, the
**NVIDIA GPU driver extension** (pinned to GRID `535.161` — the extension's default 17.5
breaks CUDA on A10), and a system-assigned managed identity with **Contributor** on that RG
so the VM can delete it at the end.

## Prerequisites (on your machine)

- **Azure CLI**, logged in: `az login` (and `az account set -s <sub>` if you have several).
- **GPU quota** for the A10 (see the box below) — `deploy.sh` preflights it.
- An **SSH public key** (`~/.ssh/id_ed25519.pub` or `id_rsa.pub`).
- The branch with the phi4-f8 configs must be **reachable by git clone** — public, or pass
  `GIT_TOKEN` for a private repo. Point `REPO_REF` at it (defaults to your current branch).
- For the **unattended push**: your Ollama signing key at `~/.ollama/id_ed25519`, whose public
  half is registered at <https://ollama.com/settings/keys>. Without it the run still trains, it
  just skips the push (and the models are lost on teardown).

## Run it

```bash
cd nl-assist-finetune/infra
PUBLISH_PREFIX=<your-namespace> ./deploy.sh    # publishes <ns>/phi4-f8-mini and <ns>/phi4-f8
```

It prints an SSH command to watch progress:

```bash
ssh azureuser@<ip> 'tail -f /var/log/f8-finetune.log'
```

You'll see: package install → GPU driver ready → apiApp up → per-variant `./run.sh all` (the
long GPU steps; the dataset is generated on the first variant and reused) → publish → then
**"Destroying resource group … in 60s"**. After that the VM and everything around it are gone.
Boot diagnostics are on, so you can also watch the serial console in the portal if SSH isn't up.

> **GPU quota (common first hurdle):** a full A10 is **36 vCPUs**, and fresh subscriptions
> default to tiny GPU quotas — the deploy would fail ARM preflight, so `deploy.sh` checks first
> and prints exactly what to raise ([myQuotas](https://portal.azure.com/#view/Microsoft_Azure_Capacity/QuotaMenuBlade/~/myQuotas)):
> - **on-demand (default):** raise BOTH **"Total Regional vCPUs"** and **"Standard NVadsA10v5
>   Family vCPUs"** to **≥ 36** in your region.
> - **Spot (`F8_SPOT=1`):** raise **"Total Regional Spot vCPUs"** to **≥ 36**.
>
> GPU increases may need a support request (self-service often auto-declines); then re-run.
> (14B QLoRA needs the full 24 GB A10, so you can't drop below 36 cores.)

## Useful overrides (env vars)

| Var | Default | Notes |
|---|---|---|
| `PUBLISH_PREFIX` | – | publishes each variant to `<prefix>/<variant>` (needs the Ollama key) |
| `VARIANTS` | `"phi4-f8-mini phi4-f8"` | space-separated; trained together in one session |
| `F8_SPOT` | `0` (on-demand) | `1` tries a Spot VM (needs Total Regional Spot vCPU quota) |
| `LOCATION` | `westeurope` | needs NVadsA10v5 capacity + quota |
| `VM_SIZE` | `Standard_NV36ads_A10_v5` | full A10; smaller A10 fractions can't fit 14B QLoRA |
| `REPO_URL` / `REPO_REF` | this repo's origin / current branch | where to clone the pipeline from |
| `GIT_TOKEN` | – | GitHub token if the repo is private |
| `OLLAMA_KEY_FILE` | `~/.ollama/id_ed25519` | the registered signing key for the push |
| `ALLOWED_SSH_CIDR` | your public IP `/32` | who may SSH in |
| `DESTROY_ON_FINISH` | `1` | set `0` to keep the VM after the run (to debug a failure) |

## Cost, teardown, and caveats

- **Teardown is automatic and defended three ways**: (1) the bootstrap's EXIT trap runs on
  success *or* failure; (2) a 6 h `timeout` caps *each variant's* training; (3) a
  `f8-teardown.timer` systemd backstop fires 8 h after boot and deletes the RG even if bootstrap
  hangs or is SIGKILLed (where the bash trap wouldn't run) — realistic both-variant runs finish
  well under that. Teardown uses the VM's managed identity via the
  ARM REST API (no dependency on the `az` CLI the run installs). If all of that somehow fails,
  delete it yourself: `az group delete -n <rg> --yes`.
- **Honest status**: a failed train/publish aborts with a non-zero code (no false "done"
  marker) and still tears down — you won't silently pay for a broken run reported as success.
- **On-demand vs Spot**: default is **on-demand** (no eviction). With `F8_SPOT=1` Azure may
  evict/deallocate mid-run (training stops, no auto-resume) — just re-run; `spotMaxPrice` in the
  Bicep sets a price cap if you want one.
- **Debugging a failure**: run with `DESTROY_ON_FINISH=0`, SSH in, read `/var/log/f8-finetune.log`
  and `/var/log/f8-apiapp.log`, then `az group delete -n <rg> --yes` when done.
- **Security**: the Ollama private key and any `GIT_TOKEN` are injected via cloud-init (visible
  to the VM and in the deployment). That's acceptable here because the RG is ephemeral and
  yours; for a shared/long-lived setup use Key Vault instead.
- Not deploy-tested end-to-end from this repo (no Azure subscription here); it's built to the
  documented Azure/NVIDIA/PyTorch behavior. First run, watch the log and keep `DESTROY_ON_FINISH=0`
  until you've seen it succeed once.
