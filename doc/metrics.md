# Throughput Metrics

yamca can record how fast each model round-trip runs and plot that speed against
the size of the context it ran against. The result — on the **`/metrics`** page —
shows how a local model's throughput degrades as the conversation grows, and lets
you compare endpoints and models head to head.

## What gets recorded

One sample per **model round-trip** — every iteration of the agent loop, not every
user turn. A turn that makes three tool calls produces several samples, so a single
busy turn already shows how speed changes as the context fills up. Chat, subagent,
and orchestrator runs all record into the same dataset, tagged by endpoint·model
and by whether they belong to an interactive session.

Each sample captures:

- **Starting context size** (prompt tokens) — the X axis of every chart.
- **Token-generation speed** (predicted tokens/sec) — how fast the model produced
  its reply.
- **Prompt-processing speed** (prompt tokens/sec) — how fast it ingested the
  context before generating.
- Cached prompt tokens, completion tokens, and the endpoint·model the run used.

A round-trip is only recorded when the backend returns **real token counts** (a
usage chunk). Without them there is no context-size axis to plot against, so the
sample is dropped rather than estimated from a character count.

## Two accuracy tiers

- **Tier A — server-measured** (the lightning bolt in the chart legend). When the
  backend reports `timings` (llama.cpp / llama-server does), yamca uses its
  authoritative prompt/predicted speeds. These are exact.
- **Tier B — client wall-clock.** Backends that report token counts but no timings
  (OpenAI, vLLM) fall back to wall-clock measurement: prompt time is the span from
  request start to the first streamed token, generation time the span from there to
  completion. Good enough to see the trend, but it includes network latency and
  queueing, so treat it as an estimate.

Flip **Server-measured only** on the `/metrics` page to hide Tier-B samples when
you want exact numbers.

## Reading the charts

Both charts share an X axis of starting context size, bucketed into bands (default
2k tokens; selectable). For each band, each series plots the **median** speed of
its samples — median rather than mean so a single slow outlier (a GC pause, a
background load spike) doesn't distort the curve. A downward slope to the right is
the expected shape: the more context the model carries, the slower each new token.

Controls on the page:

- **Time range** — all time, or the last 1 / 7 / 30 days.
- **Bucket size** — width of each context band (1k–10k tokens).
- **Series** — toggle individual endpoint·model combinations on and off.

## Storage and retention

Metrics live in their **own** VestPocket store at `.yamca/metrics.db`, separate
from the board/chat store (`.yamca/yamca.db`). Keeping them apart means this
high-volume, disposable telemetry can grow — and be wiped — without touching your
curated state. Like everything under `.yamca/`, the file is gitignored and never
committed.

Retention is enforced automatically: the most recent **N** samples are kept, and
nothing older than the **age limit** is retained, whichever bites first. Both are
configured under [Preferences](settings-and-backup.md) (defaults: 50,000 samples /
90 days); set the age to 0 to keep samples regardless of age. Retention is a
per-user setting, applied globally, and takes effect at the next prune without a
restart. Pruning runs at startup and periodically while the app is open — and
regardless of whether new recording is switched on — so the file can't grow without
bound even if you never open the dashboard.

**Clear metrics** on the `/metrics` page deletes every sample and reclaims the disk
space. Because the store is dedicated, clearing it is safe — your board, chats, and
settings are untouched.

## Turning it off

Recording is **on by default**. The **Record throughput metrics** switch under
[Preferences](settings-and-backup.md) (a per-user setting) stops collection for new
round-trips across chat, subagents, and the orchestrator. Existing samples are kept
until you clear them from the dashboard.

## See also

- [settings-and-backup.md](settings-and-backup.md) — where the preference lives
- [endpoints.md](endpoints.md) — the endpoint·model each sample is tagged by
- [orchestrator.md](orchestrator.md) — headless runs that also record metrics
