# Charts Studio — Software Architecture

**Phase 0 design document 4 of 4.** Architecture and planning; no implementation.

---

## 0. The finding that shaped this module

Measured on the codebase before Phase 1:

```
MainWindow.xaml.cs      11,141 lines
MainWindow.xaml          5,280 lines
                        ─────────────
                        16,421 lines   ≈ 50% of all source
Total source            32,556 lines
Source folders                       0   (29 .cs files, all at project root)
```

Research Lab's **engines** are well factored — `ResearchLabServices.cs` shows interface-first
design, a proxied implementation, a mock for tests, DTOs and typed exceptions. But Research Lab's
**UI and persistence live inside MainWindow**, and every phase slice has had to touch those two
files. They are the contention point for all change.

Charts Studio is comparable in scope to Research Lab. **Built the same way, it takes MainWindow
past 18,000 lines and the application becomes structurally unmaintainable.**

> **Charts Studio is the first OrbitLab module that does not live in MainWindow.**
> It is self-contained, folder-structured and properly layered, with a single narrow entry point.
> It becomes the pattern Research Lab is later migrated toward — **without a big-bang refactor of
> MainWindow now**, which would be high-risk and out of scope.

This is the one decision expensive to reverse later and nearly free today.

---

## 1. High-level architecture

### 1.1 Layers

```
┌──────────────────────────────────────────────────────────────┐
│  PRESENTATION            Views · ViewModels                  │
│  Knows nothing about statistics, files, or AI transport      │
├──────────────────────────────────────────────────────────────┤
│  APPLICATION             Orchestration services              │
│  Session, figure lifecycle, recommendation coordination      │
├──────────────────────────────────────────────────────────────┤
│  DOMAIN                  Models · specs · rules · registry   │
│  Pure, deterministic, no I/O, no framework dependencies      │
├──────────────────────────────────────────────────────────────┤
│  INFRASTRUCTURE          Rendering · persistence · export    │
│                          · AI transport                      │
└──────────────────────────────────────────────────────────────┘
                              │
        ══════════════ MODULE BOUNDARY ══════════════
                              │
┌──────────────────────────────────────────────────────────────┐
│  RESEARCH LAB (existing, unmodified)                         │
└──────────────────────────────────────────────────────────────┘
```

Dependencies point **downward and inward only**. The domain depends on nothing; rendering depends
on the domain, but the domain never knows a renderer exists.

### 1.2 The boundary contract

Charts Studio must never re-read a CSV, never re-derive a variable type, never recompute a
statistic. Everything it knows arrives through **one read-only, versioned snapshot**.

Not stylistic: commit `dbaeed5` spent a release eliminating exactly one class of bug — *two
independent readers of the same data that disagreed*, producing wrong numbers with no error
shown. A charting module that re-interpreted the dataset would reintroduce it where the output is
a published figure.

> **Research Lab produces the context. Charts Studio consumes it.**
> One direction, one shape, one version stamp.

### 1.3 End-to-end flow

```
User opens Charts Studio
    ↓  Session requests Analysis Context for a project
    ↓  Research Lab adapter builds the snapshot        (deterministic, no AI)
    ↓  Readiness gate                                  (Blocked → Magic Fix)
    ↓  Recommendation engine enumerates + ranks        (deterministic, offline)
    ↓  Render queue produces previews                  (off UI thread, cancellable)
── Contact Sheet is now interactive ──
    ↓  AI advisory pass refines order + rationale      (optional, async, degradable)
    ↓  User keeps / cuts / edits                       (spec + patch layering)
    ↓  Figure set assembled on the Shelf
    ↓  Export: same spec → screen, thumbnail, print
    ↓  Submission package written to disk
```

**Nothing downstream of the readiness gate blocks on the network.**

---

## 2. Module structure

```
ChartsStudio/
├── Domain/                       ← pure, no I/O, no framework
│   ├── Context/                  analysis context contract + fingerprint
│   ├── Specs/                    figure spec, patch layer, versioning
│   ├── ChartTypes/               registry + per-type descriptors
│   ├── Recommendation/           candidate generation, scoring, rationale
│   ├── Validation/               spec validity, data-shape rules
│   └── Themes/                   theme + journal profile definitions
├── Application/                  ← orchestration, owns no rules
│   ├── Session/                  studio session, project lifecycle
│   ├── Figures/                  figure lifecycle coordination
│   ├── Recommendations/          deterministic + advisory merge
│   ├── Rendering/                render queue, cancellation, caching
│   └── Export/                   export orchestration
├── Infrastructure/
│   ├── ResearchLabAdapter/       ← the ONLY point of contact with Research Lab
│   ├── Rendering/                renderer implementation(s)
│   ├── Persistence/              figure store, sidecar file, migration
│   ├── Export/                   raster, vector, document writers
│   └── Ai/                       advisory client, DTOs, validation
├── Presentation/
│   ├── Views/                    ContactSheet · Canvas · Shelf · Picker
│   ├── ViewModels/               one per view, plus shared studio VM
│   ├── Controls/                 figure card, variable rail, receipt
│   └── Resources/                styles scoped to the module
└── Tests/
```

**Why this shape:**

- **`Domain/` is separate from services.** Recommendation *rules* are pure logic and must be
  unit-testable without a renderer, file system or network.
- **`ResearchLabAdapter/` is its own folder** because it is the boundary. When Research Lab's
  internals change, exactly one folder should need attention.
- **`ChartTypes/` is a folder** — the extension point for Kaplan-Meier, ROC, forest plots.
- **`Rendering/` appears twice, deliberately** — the *queue* is an application concern
  (scheduling, cancellation, caching); the *renderer* is infrastructure (a library dependency).
  Splitting them keeps the chart library out of the domain and makes it replaceable.

**On assembly separation:** a separate class library would make the boundary compiler-enforced.
Weighed against a live, SHA-verified installer pipeline and cross-assembly XAML resource
resolution, the folder structure inside the existing csproj was chosen, laid out so promotion to
an assembly later is a move rather than a rewrite.

---

## 3. Data models

### 3.1 Context (read-only, produced by the adapter)

| Model | Responsibility |
|---|---|
| **AnalysisContext** | Complete immutable snapshot of one project. Fingerprint, timestamp, and everything below. The single input to recommendation. |
| **ContextFingerprint** | Content-derived identity of the data + metadata + results behind the snapshot. **The basis of all staleness detection.** |
| **ContextVariable** | One variable as Charts Studio sees it — a *projection*, never a reference. |
| **ContextResult** | One computed result: test, variables, effect, CI, p, n. The anchor for figure↔result binding. |
| **ContextDesign** | Design classification, arm structure, outcomes, objective. |
| **ReadinessSummary** | Verdict plus specific blockers, so the gate can explain itself. |

**Why projections, not references:** live references would let a change in Research Lab silently
change figures here. A projection plus a fingerprint makes change **detectable** instead of
invisible. That is the entire staleness design in one decision.

### 3.2 Figures

| Model | Responsibility |
|---|---|
| **FigureSpec** | Complete, serialisable, versioned description of one figure. **The single source of truth** — screen, thumbnail and 600-DPI export all derive from it, which is what guarantees WYSIWYG. |
| **FigureSpecVersion** | Schema stamp, present from the first release. |
| **FigurePatch** | User edits as an **overlay**, not mutations. Regeneration replaces the base and re-applies the patch, so refresh never destroys hand edits, and "reset" is a deletion. |
| **Figure** | Runtime pairing of base spec, patch, lifecycle state, provenance and cache key. |
| **FigureProvenance** | Dataset identity, rows used/excluded, variables, bound test, timestamps. Powers the receipt and verify-back. |
| **FigureRecommendation** | One candidate: form, bindings, deterministic score, rationale, applicability — with AI rank and wording kept in **separate fields**. |
| **FigureCollection** | Ordered set forming one manuscript's figures. Owns numbering, ordering, consistency checks. |
| **FigureHistoryEntry** | One prior state with timestamp and change summary. |

**On `FigureTemplate`:** it shouldn't be a data model. A template is not user-owned data — it is a
**descriptor registered by the chart type**. Modelling it as persisted data means shipping
template definitions in user files that then need migrating.

### 3.3 Presentation and output

| Model | Responsibility |
|---|---|
| **FigureTheme** | Palette, typography, weights, grid, mono-safety. Purely visual — **a theme can never change validity**, which is why themes are freely swappable and forms are not. |
| **JournalProfile** | Column widths, DPI, formats, colour policy, figure limits. Drives Journal Mode. |
| **ExportProfile** | Named reusable export configuration, modelled on Adobe presets. |
| **ExportPackage** | Manifest of a submission export: files, legends, provenance, naming, numbering. |
| **RenderRequest / RenderResult** | Unit of work for the queue: spec, size, DPI, cancellation token, cache key. |

### 3.4 On `ChartProject`

**Removed.** Charts Studio should not own a project concept — OrbitLab already has research
projects, and a second entity creates two identities for one thing, with synchronisation problems
and orphaned records (the class of problem Phase 10's draft registration had to solve). Figures
reference the existing project by id; the persisted unit is a `FigureCollection` scoped to it.

---

## 4. Services

| Service | Layer | Owns | Does not own |
|---|---|---|---|
| **StudioSessionService** | Application | Opening a project, holding context, coordinating the gate | Rules, rendering, persistence |
| **AnalysisContextProvider** | Infrastructure | Building the snapshot; computing the fingerprint. **The only code that talks to Research Lab.** | Interpreting variables — it *projects*, never re-derives |
| **RecommendationEngine** | Domain | Enumerating, scoring, ranking, template rationale | Any I/O or AI |
| **ChartTypeRegistry** | Domain | Catalogue of forms with validity rules, capabilities, defaults, metadata | Rendering |
| **FigureService** | Application | Figure lifecycle — materialise, edit, patch, refresh, restore, delete | Deciding validity |
| **FigureCollectionService** | Application | Membership, ordering, numbering, consistency | Individual figure state |
| **RenderQueue** | Application | Scheduling, prioritisation, cancellation, caching, throttling | Drawing |
| **ChartRenderer** | Infrastructure | Spec → pixels or vectors at any size and DPI | Deciding what to draw |
| **FigureStore** | Infrastructure | Persisting/loading; migration; atomic writes | Business rules |
| **AiAdvisoryService** | Infrastructure | Transporting bounded requests; validating against the whitelist | Anything statistical |
| **CaptionService** | Application | Deterministic caption composition; optional AI polish | Computing caption numbers |
| **ThemeService** | Application | Theme catalogue, active theme, set-wide application | Validity |
| **JournalProfileService** | Application | Journal catalogue, active profile, constraint checks | Export mechanics |
| **ExportService** | Application | Orchestrating a package | Writing files |

**Naming note:** the AI service is `AiAdvisoryService`, deliberately not `AIRecommendationService`.
Calling it a *recommendation* service invites the belief that it makes recommendations. It
doesn't. **Names shape what future contributors assume they may do.**

---

## 5. Project workflow

```
[Entry]
   ├─ Nav → Charts Studio ──────────► Project Picker
   ├─ Computed result → "Chart this" ► direct to Canvas, pre-bound
   └─ Variable → "Figures for this" ─► Contact Sheet, pre-filtered
                    ▼
        PROJECT PICKER  →  CONTEXT BUILD  →  READINESS GATE
                                                 │
                              Blocked ─────► Magic Fix
                                                 ▼
        RECOMMENDATION  →  RENDER QUEUE  →  CONTACT SHEET ◄── AI ADVISORY
                                                 │              (async, optional)
                                    keep ────────┼──── cut
                                                 ▼
                          FIGURE CANVAS  →  FIGURE SHELF  →  EXPORT

[Ambient]  fingerprint mismatch → figures marked stale
           autosave after every committed edit
```

**Intermediate steps that need an owner:** context build (observable, it is what the opening
sequence displays); readiness gate; render queue (a first-class step, not an implementation
detail); AI advisory pass (deliberately *after* the sheet is interactive); staleness detection;
autosave.

---

## 6. Contact Sheet architecture

**Previews are not a separate preview system.** They are real figures rendered small, from real
specs, through the same renderer as export. Exactly one rendering path exists, which is what makes
a thumbnail an honest promise of the final output.

```
RecommendationEngine → candidate → FigureSpec (base)
      → RenderRequest (thumbnail size) → RenderQueue → ChartRenderer
            → cached bitmap → card
```

**Preview subject selection is deterministic and priority-ordered** (bound result → primary
outcome → objective mention → completeness → stable tiebreak). **Never random** — a card showing
Age today and HbA1c tomorrow destroys trust in everything on the sheet.

**Recommendations load in two strictly ordered passes.** Pass 1 is deterministic, blocking,
offline, instant — the sheet is fully usable at its end, and that is the state an offline user
sees permanently. Pass 2 is advisory, non-blocking, and **merged, not substituted**: advisory rank
and wording live in separate fields, so the deterministic result stays recoverable. Failure,
timeout, truncation (a real mode — `935e10d` documents `finish_reason: "length"` returning HTTP
200 with truncated content) or an unrecognised response leaves pass 1 in place, silently.

**Only kept figures persist.** Proposals are derived state, reproducible from context plus engine
version; persisting them would mean migrating records the user never chose to keep.

**Storage is a sidecar file per project**, not inside `research_projects.json`. That file holds
irreplaceable research data, and the release history shows real care verifying it survives
upgrades byte-identical. **A failed write from a new charting module must never cost a user their
research.** Per-project files add a second isolation layer and keep writes proportional to what
changed.

**Three update paths, kept separate:** user edits → patch overlay; context change → figures marked
**stale** with *nothing regenerating automatically* (a figure silently changing under someone
preparing a submission is unacceptable); explicit refresh → base regenerated, patch re-applied,
prior version kept in history.

---

## 7. Figure lifecycle

```
        PROPOSED ──keep──► MATERIALIZED ──► ACTIVE ◄──────┐
           │ cut                              │           │
           ▼                            edit  ▼           │ refresh
        discarded (undoable)              PATCHED         │
                                              │           │
                          context change      ▼           │
                                            STALE ────────┘
                                              │
                                              ▼
                                          COLLECTED ──► EXPORTED
```

- **PROPOSED is not persisted** — derived, keeping the store small and migration-free.
- **PATCHED is distinct from ACTIVE** so "has the user customised this?" is answerable without
  diffing.
- **STALE is a marker, not an exit from ACTIVE.** A stale figure stays usable, exportable and
  editable — it just carries a truthful warning. Blocking use would punish the user for changing
  their data.
- **EXPORTED does not consume the figure.** Modelling export as terminal is a common mistake that
  makes re-export awkward.

Every transition appends to history.

---

## 8. AI architecture

```
┌──────────────────────────────────────────────────┐
│  ADVISORY LAYER   optional · online              │
│  reorder · explain · caption polish · set review │
│  ─────────────── FAILS SILENTLY ───────────────  │
├──────────────────────────────────────────────────┤
│  DETERMINISTIC LAYER   always · offline          │
├──────────────────────────────────────────────────┤
│  CONTEXT LAYER   always · offline                │
└──────────────────────────────────────────────────┘
```

The top layer may be removed entirely and the product still functions. **That is the architectural
test of whether AI is correctly positioned**, and it should be verified by an automated test that
runs the module with the advisory layer disabled.

**Inputs, exhaustive:** variable names, kinds, ordinality, roles; valid/missing counts; category
counts and labels; descriptive summaries; design classification and arms; objective and outcomes;
computed results (test, variables, effect, CI, p, n); **the candidate list the engine produced**;
journal constraints.

**Forbidden inputs:** any raw data row or cell value, ever; any field capable of carrying
participant data; file paths, dataset filenames, account information.

**Enforcement must be structural.** The request DTO should make raw values *unrepresentable* —
there should be no field capable of holding a row, so a future contributor cannot add one by
accident. A convention fails the first time someone is in a hurry; a type that cannot express rows
never fails.

**Outputs and validation:**

| Output | Validation |
|---|---|
| Reordering | Every identifier must exist in the submitted candidate list; unknown → **discard entire response** |
| Rationale | Length-bounded, stored separately, never parsed for values |
| Caption polish | Applied over an already-correct caption; **numbers never taken from the response** |
| Set review notes | Advisory text only; cannot mutate a figure |
| Missing-figure suggestions | Must map to a registered, valid form; anything else dropped |

**Metering:** advisory calls debounced and cached by context hash. Charts Studio operates on an
existing project and must **never consume a project slot** — that would silently spend a user's
allowance.

---

## 9. Extensibility

### 9.1 Chart type descriptors

Each form registers a descriptor declaring identity, data-shape requirement, **capability
requirement**, validity rules *with reason text*, default spec, scoring hints, metadata, and
renderer binding.

**Adding a chart type is adding a descriptor. Nothing in the core changes.**

### 9.2 Capability gating

Forms declare a required statistical capability; the registry checks it against what the
application provides.

| Form | Capability | Status |
|---|---|---|
| Histogram, box, scatter, bar | Descriptive | ✅ |
| Correlation heatmap | Correlation | ✅ |
| Kaplan-Meier | Survival | ❌ engine missing |
| ROC curve | Classification / AUC | ❌ engine missing |
| Forest plot | Regression / meta-analysis | ❌ engine missing |
| Funnel plot | Meta-analysis | ❌ engine missing |

**The "not applicable — here's what it needs" gallery tier falls out of the architecture
automatically.** It isn't hardcoded UI; it's the registry reporting an unmet capability with the
descriptor supplying the explanation. When the survival engine ships, Kaplan-Meier becomes
available with no UI change.

This also makes the roadmap honest: **these figures are blocked on statistics, not charting.**

### 9.3 Other seams

Journal Mode (profiles are data) · Themes (data; cannot affect validity) · Batch export (export
already operates on a collection) · **Multi-panel figures** (a composite form whose bindings are
other specs — *the reason specs must be composable from day one*, since retrofitting composition
onto a flat spec is expensive) · New renderer (one interface) · New export format (registered
writers) · Research Lab UI migration (the adapter is the only coupling point).

---

## 10. Self-critique

**10.1 The patch layer is elegant and will be the hardest thing to get right.** Base + patch
solves "refresh without losing edits" — until the base changes *shape*. If a refresh takes a
figure from two groups to three, what happens to a patch that set a colour for group 2? **This is
the most likely source of subtle bugs, and it manifests as users losing work.** *Mitigation:*
patches must be field-scoped and independently invalidatable — a stale entry is dropped with a
visible, non-alarming note, never silently, and never taking valid patches with it. Needs its own
test suite, built before the UI.

**10.2 The registry may be over-abstracted before it has earned it.** Designing a plugin registry
for KM/ROC/forest when none of the required engines exist risks the wrong abstraction. Real KM
needs censoring indicators, at-risk tables and confidence bands — none anticipable correctly
today. *Mitigation:* build against the MVP forms only and do not generalise until a *third*
materially different form demands it. Keep the seam, resist the framework.

**10.3 Sidecar persistence introduces desynchronisation risk** — figures referencing a renamed,
deleted or restored project. *Mitigation:* reference the normalised project id
(`ProjectIdNormalizer` guarantees stability) and reconcile orphans at load, reporting rather than
silently deleting.

**10.4 The context snapshot may be expensive at scale.** It holds summaries, not data, so it
scales with variable count rather than row count — the right shape, but measure it early with a
deliberately large dataset rather than assuming.

**10.5 Golden-image tests are notoriously brittle.** Pixel comparison across GPU drivers, DPI and
fonts produces false failures until someone disables the suite. *Mitigation:* test the **render
model** (geometry, scales, tick positions, coordinates) rather than pixels, reserving a few pinned
pixel smoke tests. Mirrors how the existing 107-value regression suite works.

**10.6 Threading and cancellation are underspecified, and that is where UX quality lives.** This
matters more than usual: `b31415c` had to fix scrolling that felt "laggy", and its deferred
non-virtualising item-controls issue applies directly to a grid of rendered cards. *Mitigation:*
specify bounded concurrency, viewport-priority scheduling, cancellation on scroll-away, cache
keyed on spec + size + DPI + theme, and a hard rule that **no rendering occurs on the UI thread**.

**10.7 The boundary is only as strong as its enforcement.** Convention will not hold — the
11,000-line MainWindow is evidence of how gradually a boundary erodes. *Mitigation:* one adapter,
and an architecture test asserting no Charts Studio type outside it references a Research Lab
type. **Make the boundary a build failure, not a review comment.**

**10.8 Failure and recovery are under-designed.** Corrupt store, renderer exception mid-export,
disk full, partially written package. A submission tool failing halfway at 11pm before a deadline
generates support tickets and lost trust. *Mitigation:* exports write to a temp location and move
on completion; the store keeps a last-known-good copy; renderer failures degrade to a placeholder
with a reason, never a crash.

---

## 11. Sequencing

| Slice | Delivers | Proves |
|---|---|---|
| 1 | Analysis context + fingerprint + adapter | The boundary holds; staleness is detectable |
| 2 | Spec model + versioning + store + migration | Persistence is safe before anything depends on it |
| 3 | Chart type registry + MVP forms + validity rules | Domain correctness, fully unit-testable |
| 4 | Recommendation engine + golden tests | Deterministic ranking, pinned by tests |
| 5 | Renderer spike + render queue | **Resolves the library decision with evidence** |
| 6 | Contact sheet + canvas + shelf | First user-visible surface |
| 7 | Patch layer + history | The hardest correctness problem, isolated |
| 8 | Export pipeline + profiles | Submission package |
| 9 | AI advisory layer | Last, and provably removable |

**Slice 5 must not be deferred** — the renderer choice constrains export fidelity, DPI handling
and WYSIWYG, the three things hardest to change later and most visible when wrong.

**Slice 9 being last is the point.** If the module ships complete and useful before the AI layer
exists, the architecture has proven AI is genuinely advisory — which is the commitment this whole
design exists to protect.

---

## 12. Renderer decision (resolved)

The spike specified in §11 slice 5 was run against **ScottPlot 5.1.59**, **OxyPlot 2.2.0** and
**LiveChartsCore.SkiaSharpView 2.0.5** — the same box plot at 3.5×2.5 in, rendered at 96 and 600
DPI plus vector. All three are MIT. All three depend on SkiaSharp, so its ~13 MB win-x64 native
payload is shared, not per-engine.

| | WYSIWYG 96→600 DPI | Vector | Box plots | Print render |
|---|---|---|---|---|
| **ScottPlot 5.1.59** | ✅ identical | SVG | native | 114 ms |
| **OxyPlot 2.2.0** | ✅ identical | SVG + PDF | native | 105 ms |
| **LiveCharts2 2.0.5** | ❌ **broken** | none | native | 118 ms |

**LiveCharts2 was eliminated.** At 600 DPI it produced a materially different figure — tick
density exploded from 5 labels to every 0.05, and type became microscopic because no scale factor
is applied. That is exactly the "labels illegible at submission size" failure Journal Mode exists
to prevent.

ScottPlot and OxyPlot both held WYSIWYG exactly — identical layout **and identical tick labels**.

**Decision: ScottPlot 5.** Materially more active development; a first-party WPF control for the
interactive canvas; `ScaleFactor` is precisely the primitive Journal Mode's true-print-size canvas
needs; and a better model for the custom plottables KM, ROC and forest plots will require.

**Known trade-off:** OxyPlot exports PDF vector natively and ScottPlot does not (SVG only). If
direct PDF vector becomes a hard requirement — particularly for embedding into
`ResearchLabPdfExporter` — that is the one factor that would justify revisiting. The
`IFigureRenderer` interface exists so that revisit costs one implementation and nothing else.
