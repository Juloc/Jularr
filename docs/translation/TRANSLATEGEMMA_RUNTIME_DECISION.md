# Clean-start: modular local translation runtime and model selection

**Status: architecture proposal + operator-only prototype.** No production Jularr runtime manager, host agent or in-app installer is activated in this change. See #881, #1461, #848 and #985.

## What exists today

- Jularr's `clean-start` main Dockerfile contains no TranslateGemma/Ollama weights; `compose.yaml` persists `/data` and has no Docker socket.
- Bergamot already downloads verified models to the Jularr data folder on demand and translates in the **browser**, without a heavy runtime.
- The admin Speech catalog already shows how to manage optional models outside the main application image.
- The older `dev` TranslateGemma translator accepts a separately installed local model endpoint; it is **not** compatible with the new backend merely by copying files.

## Proposed architecture: external runtime + opt-in model management

Owner intent: detect which models a Debian server **could** run, optionally benchmark them, and download them directly from a **Jularr Admin UI** while keeping the Jularr image lightweight and every feature modular.

**Recommended production design (subject to #881 review):**
- Only an explicitly configured private Ollama runtime is managed. Its own service/container holds runtime code and model bytes in a persistent model volume. Jularr has **no Docker socket**, no privileges to create containers, and no model binaries in the application image.
- Admin-only, disabled-by-default translation-provider module. No runtime polling/download or model loading at startup. Cached translation and Bergamot do not depend on Ollama being online.
- All operations go via canonical Data/Service/Logic authorization, policy, audit, rate/concurrency budgets and existing translation/Reader contracts. Never send book text to unconfigured clouds.
- Admin explicitly chooses from a **reviewed allowlist**, sees model download size, approximate RAM, verified available space/licensing, and approves a bounded download. Use only the configured runtime's `/api/pull`, with status and progress, no arbitrary registry URLs/model names.
- After pull, model identity/digest and license are recorded; a **separate** small benchmark produces actual latency/tokens-per-second and manually reviewed quality examples. Do not label a model “great quality” based on size or fluent text alone.
- Stable semantic block IDs, compact known glossary and target-language context, per-model cache identity, bounded retries, separate variants, source fingerprint and Reader progress are owned by #985/#834. The whole chapter must not be pushed into a single model request.

### Compared alternatives (mandatory #881)

| Option | Benefits | Main drawbacks | Decision |
| --- | --- | --- | --- |
| A. External Ollama, manual models | Fewest privileges, no Docker/Compose changes | No in-app one-click model download | Safe baseline |
| **A+. External Ollama, Jularr in-app approved model pulls** | Main image stays small, explicit control, persistent volume | Ollama must be provisioned separately and secured | **Recommended** |
| B. Optional Compose profile for Ollama | Reproducible installation | Requires separate deployment, update/rollback plan and approval | Later |
| C. Restricted host agent / Docker controller | Can start/stop runtime from UI | Broad new privilege boundary, Podman/rootless complexity | Not for MVP |
| D. Embed llama.cpp in Jularr core image | No companion server | Runtime ABI/image growth, extra resources | Reject |

**Owner gate:** The request to keep models modular and downloadable authorizes this architecture exploration and benchmark prototype. Production runtime installer, web UI and privileged host/container operations are **still behind #881's explicit architecture review/approval**. This PR does not add a Docker socket, modify Compose, start containers, or add any runtime process.

## Hardware checks: never promise what cannot be proven

1. Read `/proc/meminfo` and **cgroup v2 `memory.max` / `memory.current`**. A Debian VM with 16 GB host RAM can give Jularr much less via its container limit. CPU affinity and `cpu.max` show different facets of the CPU quota; neither predicts tokens/s.
2. An Ollama sidecar may run with **different** cgroup limits from Jularr. Its memory/VRAM must be probed on **that runtime** before claiming fit; the standalone probe reports `unknown` by default. Visible GPU nodes do not prove accelerated inference.
3. Starting reference catalog sizes (Q4; [Ollama model tags](https://ollama.com/library/translategemma/tags)): 4B = **3.3 GB**, 12B = **8.1 GB**, 27B = **17 GB**. Working RAM, KV cache and other processes require **more**. Probe's 6/14/28 GB are **planning heuristics only**, not measured memory requirements or quality scores.
4. Run a bounded request on an **already installed** model to measure the actual runtime and CPU/GPU combination. Compare multiple distinct language pairs (especially Japanese→German, Indonesian→German and German→Indonesian). These three smoke samples are NOT a novel-quality benchmark; #985 requires long chapters, omitted text checks, references and reviewer assessment.
5. After explicit selection, pull model into **Ollama's model volume**, verify installed digest and retest. Never modify Jularr's image or switch the default provider automatically.

## Developer/operator prototype

Uses Python standard library only, outside Jularr's application image. You need an Ollama runtime which you deployed and secured separately; do **not** expose its unauthenticated API publicly.

```bash
# Discover existing Ollama models. Does not download or infer.
python3 tools/translation/model_probe.py --ollama http://ollama:11434

# Measure an installed 4B model. Writes repeatable machine-readable results.
python3 tools/translation/model_probe.py --ollama http://ollama:11434 \
  --benchmark translategemma:4b --output /tmp/model-report.json

# EXPLICIT opt-in model pull, into Ollama's OWN persistent volume.
python3 tools/translation/model_probe.py --ollama http://ollama:11434 \
  --pull translategemma:4b

# Offline tests use a fake HTTP Ollama server. No model downloads.
python3 -m unittest discover -s tools/translation -p 'test_*.py'
```

**Do not use `--runtime-shares-memory-budget` merely because Ollama and Jularr are on the same physical Debian VM.** Separate containers normally have distinct cgroups. This flag is for verified shared accounting only.

## Follow-up slices to make this directly available in Jularr

1. Admin-owned runtime configuration (one allowlisted endpoint, secret/private-network/TLS policy, health, SSRF protection). Link to the existing instance modules and admin navigation, not a separate app.
2. Admin UI: list approved variants, installed/loaded, capabilities unknown/plausible/benchmarked, disk space; explicit Pull, progress, Cancel, Activate and Remove with confirmation, error/timeout handling and audit. Disabled = no background RAM/CPU/network work.
3. Authenticated backend adapter to the *already provisioned* runtime; stream pull progress safely through existing worker/operations rather than blocking a web request, bound downloads/concurrency and recover on restart.
4. TranslateGemma generation provider using the canonical Reading blocks and #985 QA/glossary; output source/provider/model/version/digest, language pair, glossary/context fingerprint. Keep Bergamot and completed existing translations safe.
5. Real VM + Android/iOS benchmarks (Pixel 8 Pro, iPhone 16 Pro, iPad Air M1); do **not** assume native TranslateGemma works well on phones simply because the 4B model is smaller.
6. Only if separately approved: optional runtime installation/Compose profile and host management. No dependency of the standard small-image deployment.

**Release gate:** Only ship the UI/runtime-manager slice after #881's owner architecture approval, isolated end-to-end functional tests, bounded idle overhead, no Docker permissions and verified persistent model/download/rollback behavior.
