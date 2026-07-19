# Charts Studio — design set

Charts Studio is OrbitLab's module for producing publication-quality scientific figures from
an existing research project. These four documents are the Phase 0 design work, written before
any implementation and used as the reference for every phase since.

| # | Document | What it settles |
|---|---|---|
| 1 | [Product vision & architecture](01-product-vision-and-architecture.md) | What the feature is, why deterministic-first, MVP scope, risks |
| 2 | [Template gallery & live preview](02-template-gallery-and-live-preview.md) | Browsing model, template taxonomy, live previews from the user's own variables |
| 3 | [Premium experience design](03-premium-experience-design.md) | The end-to-end experience: contact sheet, curation, journal mode, submission export |
| 4 | [Software architecture](04-software-architecture.md) | Layers, module boundary, data models, services, figure lifecycle, extensibility |

## The one invariant

Everything below follows from a single commitment:

> **AI never touches the data. Deterministic C# computes every number.**

`ResearchLabNarrativeGenerator.cs` already proves the pattern — manuscript-ready prose with zero
AI in the path. Charts Studio holds the same line. AI may reorder suggestions and reword
explanations; it may never compute, choose a bin width, determine an *n*, or decide
significance. The module must ship fully working with the AI layer disabled, and that is
verified by test.

A second rule follows from it, learned the expensive way in commit `dbaeed5`:

> **Charts Studio never reads the dataset.** It renders only from aggregates Research Lab has
> already computed. Two independent readers of the same data that disagree is a bug class this
> codebase has already paid to eliminate — and here the output would be a published figure.

## Implementation status

| Phase | State | Commit |
|---|---|---|
| 0 — Design | Complete (these documents) | — |
| 1 — Foundation | Complete — module skeleton, project picker, session, per-project persistence | `12953d1` |
| 2 — Contact Sheet | Complete — ScottPlot 5 rendering, recommendations, keep/remove, add figure | `0e3e158` |
| 3 — Figure Editor | Complete — patch layer (non-destructive), undo/redo, live preview, validation | `27ba766` |
| 4 — Figure Shelf | Complete — curated set, multi-select, drag reorder, duplicate, schema v2 | `27ba766` |
| 5 — Export | Complete — PNG/SVG/PDF, profiles, batch, manifest, captions, WYSIWYG | `29afe13` |
| 6 — AI advisory layer | Complete — Core AI module + advisory assistant (caption, critique, accessibility, consistency); deterministic-first, degrades offline | `67e1447` |

## AI architecture (Phase 6)

The shared AI transport (`OrbitLabAiProxyClient`) already existed and was already shared by the
flashcard AI and Research Lab. Phase 6 added a **Core AI module** (`CoreAi/`) that lifts the
generic completion orchestration (timeout policy, error mapping, diagnostics) into a reusable
layer wrapping that transport. **Research Lab was left byte-for-byte unchanged.** Charts Studio's
AI builds entirely on Core AI; both modules depend on the shared transport, neither depends on
the other. AI is advisory only — the accessibility and consistency reviews have a deterministic
core that runs fully offline, and every task degrades to those deterministic findings when AI is
unavailable. The prompt payload is data-free by construction.

**Rendering engine:** ScottPlot 5, chosen by spike over OxyPlot and LiveCharts2. ScottPlot and
OxyPlot both held WYSIWYG exactly from 96 to 600 DPI; LiveCharts2 produced a materially
different figure at print size and was eliminated. See document 4, §12.

## Known constraint on chart types

Charts Studio can currently draw **box plots, bar charts and mean ± SD intervals**, because
those are exactly what Research Lab's persisted aggregates support. Histograms, scatter plots
and grouped comparison figures are blocked on Research Lab persisting more — bin counts, paired
raw values, and per-group five-number summaries respectively. That is a statistics dependency,
not a charting one, and it must not be worked around by reading the dataset.
