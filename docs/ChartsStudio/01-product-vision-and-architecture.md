# Charts Studio — Product Vision & Architecture

**Phase 0 design document 1 of 4.** Planning only; no implementation.

---

## 0. The invariant this feature must not break

Everything in Research Lab is built on one commitment: **AI never touches the data;
deterministic C# computes every number.** `ResearchLabNarrativeGenerator.cs` is the proof — it
produces manuscript-ready Methods and Results prose with zero AI in the path.
`ResearchLabTestRecommendations.cs` recommends statistical tests deterministically. The AI proxy
exists for text assistance, and raw CSV rows are never sent to it.

Charts Studio is the first feature where the temptation to break this is strong, because
"AI analyses the project and picks charts" *sounds* like an AI task. It mostly isn't. If a
figure in a published paper is wrong because a language model mis-summarised a column,
OrbitLab's core claim dies.

> **Every number, bin, quartile and error bar in a Charts Studio figure must come from the
> existing deterministic engines.** AI may influence ordering, phrasing and prioritisation.
> Nothing else.

This is a non-negotiable written into the design, not a preference.

---

## 1. Challenges to the original proposal

The initial brief proposed an AI-first workflow. Seven places where that should change.

### 1.1 "AI extracts variables, types, study design, stats" — this should not be AI

OrbitLab **already knows all of it deterministically**. `VariablePreparation` in
`ResearchLabStatistics.cs` is described in its own comment as "the single shared interpretation
step used by BOTH the readiness checks and the engine, so they can never disagree." `RecoVarKind`
already classifies every variable as Continuous / Binary / Nominal / Ordinal / Unsupported /
Ambiguous. `TwoByTwoStudyDesignKind` already classifies study design. Descriptive statistics,
tests and outcomes are already persisted as `SavedComputedResult`.

Sending that to a language model to "extract" what the app already computed adds hallucination
risk, latency and cost in exchange for nothing — and creates a second source of truth that can
disagree with the engine. That is precisely the bug class commit `dbaeed5` spent a release
eliminating: two CSV readers that disagreed.

**Better:** extraction is a deterministic C# step producing an `AnalysisContext`. AI receives it
as already-structured facts, never as an extraction task.

### 1.2 Recommendation should be deterministic-first, with AI as a re-ranking layer

Consider the canonical examples:

| Example | What actually determines it |
|---|---|
| Age → Histogram | Continuous, single variable, distribution intent |
| Gender → Pie | Nominal, low cardinality, single variable |
| BMI by Treatment Group → Box Plot | Continuous outcome × Binary/Nominal predictor |
| HbA1c vs Age → Scatter | Continuous × Continuous |
| Treatment Outcome → Bar | Categorical, single variable |

Every one is mechanically derivable from `RecoVarKind` + role + cardinality. A rule engine gets
these right **reproducibly, offline, instantly, at zero marginal cost, and under unit test**. An
LLM gets them right *most* of the time, differently each run, only online, and you cannot write
a regression test that pins the output.

This mirrors what already exists: `ResearchLabTestRecommendations.cs` recommends Welch vs
Mann-Whitney vs chi-square deterministically. Chart recommendation is a strictly easier problem
than test recommendation, and the harder one was already solved without AI.

**Better:** a `ResearchLabChartRecommendations` engine, sibling to the test recommender, sharing
its enums and pair model. AI sits *on top*, reordering against the stated objective and writing
the human-facing rationale. **AI enhances; it is never load-bearing.**

### 1.3 Do not recommend pie charts

For a tool positioned on *publication-quality figures for medical research*, "Gender → Pie
Chart" is the one example to reject outright.

Pie charts encode quantity as angle and area, which humans judge far less accurately than
position and length — the most replicated finding in graphical perception research. Biomedical
style guidance broadly discourages them, and reviewers treat them as a marker of statistical
naïveté. A two-category pie is strictly worse than a single labelled bar or an inline "n (%)"
in Table 1, which OrbitLab can already generate.

**Better:** emit **bar chart** for categorical distributions. Pie stays available in manual mode
but is explicitly de-ranked, with a note explaining why bars are preferred. This is exactly the
guidance the target user is paying for. Charts Studio should make people's papers better, not
just faster.

### 1.4 The binary "AI Mode / Manual Mode" split is the wrong shape

Two modes means two UX surfaces, two code paths, two sets of bugs, and a forced choice at the
moment the user knows least. It also strands users: someone in AI Mode who wants one axis
changed has to abandon and restart.

**Better:** one canvas, one editing model. AI is a *starting state*, not a mode. Suggestions are
always present; clicking one materialises a fully-formed, immediately editable chart. "Build
from scratch" enters the same editor empty. Both original modes become *entry points* into one
surface, halving what has to be built and maintained.

### 1.5 The real moat isn't pretty charts — it's provenance

Anyone can draw a box plot. What no one else does is guarantee that **the figure, the Results
paragraph and the statistical test all came from the same data at the same moment and agree.**

OrbitLab is uniquely positioned: `SavedComputedResult`, `IInferenceExportable`, the stale-chip
mechanism and the narrative generator already exist. A box plot of BMI-by-treatment should be
*bound* to the Welch result for that pair — same n, same p, same CI — and able to emit its own
figure legend.

**Better:** make chart↔result binding first-class in the MVP. A figure is either **bound** to a
`SavedComputedResult` (inheriting its staleness, n and test annotation) or explicitly
**unbound/exploratory** and visibly marked. Competitors structurally cannot copy this, because
they don't own the analysis.

### 1.6 Project selection needs a readiness gate

A project can have no dataset, an unready dataset, or unresolved variable metadata. Generating a
confident-looking publication figure from data the engine considers `Blocked` is worse than
generating nothing — it launders bad data into something authoritative.

The gate already exists: `StatisticsReadinessState` (Blocked / NeedsReview / Ready) and Magic Fix.

- `Ready` → proceed silently
- `NeedsReview` → proceed, but every figure carries a review banner
- `Blocked` → do not recommend; route to Magic Fix with an explanation

### 1.7 "Editable and exportable" hides the hardest problems — specify them now

**Editing.** If a user hand-edits a chart, then data changes and it regenerates, do their edits
survive? If edits mutate the generated spec directly, regeneration destroys user work and trust
goes with it. **Design a two-layer spec: a generated base plus a user patch layer.**
Regeneration replaces the base; the patch re-applies. This also makes "reset to suggested"
trivial.

**Exporting.** PNG at screen DPI is useless for submission. Journals typically require 300 DPI
for halftone and 600–1200 for line art, and increasingly prefer vector. **The on-screen render
and the print export must come from the same spec through the same renderer**, or WYSIWYG breaks
and users find out after submission.

---

## 2. Product vision

> **Charts Studio turns a completed OrbitLab analysis into submission-ready figures, without the
> user needing to know which figure they need.**

| Tool | What it asks the user to already know |
|---|---|
| Excel | Chart type, data range, formatting, everything |
| GraphPad Prism | Chart type, correct data layout, statistical pairing |
| R / ggplot2 | All of the above, plus code |
| **Charts Studio** | **Nothing — it knows what you analysed** |

The wedge is that OrbitLab already holds the study design, variable metadata, descriptive
statistics and computed tests. No general-purpose chart tool has that context, so none of them
can recommend anything. Charts Studio is not a chart builder that added AI; it is an
**analysis-aware figure generator**, and that ordering is the whole product.

**Success looks like:** a medical student with a cleaned dataset and three computed results
opens Charts Studio and, within ninety seconds, has four publication-quality figures with
correct legends, correct *n* and correct p-values — none of which they configured.

---

## 3. User workflow

1. **Enter** — from the Research Lab nav, or contextually from a computed result ("Chart this
   result"), which pre-binds and skips ahead.
2. **Select project** — with dataset presence, variable count, computed-result count and a
   readiness chip. Projects without a dataset are visible but disabled *with a stated reason*.
3. **Load & prepare** (deterministic, no AI) — build the `AnalysisContext`; show a compact
   summary proving the project was understood before anything is recommended.
4. **Readiness gate** — see §1.6.
5. **Suggested figures** — deterministic candidates appear **immediately**, ranked, each with a
   live thumbnail and one-line rationale. This must not wait on a network call. If AI
   enhancement is available it refines order and wording in place; if it fails, nothing visibly
   breaks.
6. **Materialise** — clicking a suggestion opens the editor with the chart fully formed. No
   separate "AI mode".
7. **Edit** — variables, chart type, scales, labels, grouping, palette, error-bar convention
   (SD / SEM / 95% CI as an explicit, stated choice), figure size and font sizing for final
   column width.
8. **Export** — per figure or batch, with an optional auto-generated caption.
9. **Persist** — figures save with the project, reopen intact, and go **stale** when underlying
   data or a bound result changes, reusing the existing stale-chip pattern.

---

## 4. Backend architecture

**Almost nothing goes server-side, and that is the correct answer.** OrbitLab is local-first by
deliberate design: data lives in `research_projects.json`; the backend handles licensing,
updates and AI proxying only. Charts Studio must not change this. Chart specs, rendering and
export are entirely local.

The backend's role is limited to:

1. **AI proxy** — one endpoint alongside the existing Phase 8 proxy, same server-held key, same
   forced model, same usage metering.
2. **Usage accounting** — Charts Studio operates on an *existing* project and must **not**
   consume a project slot from the reservation system. Getting this wrong would let charting
   silently burn a user's allowance.
3. **Entitlement** — whether the module (or just its AI layer) is plan-gated, reusing the
   `EndsAtUtc`-aware resolution from `fix/subscription-resolution`.

**Payload boundary — non-negotiable:** the proxy receives variable *names*, kinds, roles,
cardinalities, counts, summary statistics, design and objectives. It **never** receives raw data
rows. This must be enforced by construction — the request DTO should make raw values
*unrepresentable*, not merely omitted by convention.

---

## 5. AI architecture

```
┌──────────────────────────────────────────────────┐
│ L3  AI ENHANCEMENT (optional, online, metered)   │
│     re-rank · reword rationale · draft captions  │
│     Fails → silently falls back to L2            │
├──────────────────────────────────────────────────┤
│ L2  DETERMINISTIC RECOMMENDER (always, offline)  │
│     enumerate · score · rank · template rationale│
├──────────────────────────────────────────────────┤
│ L1  DETERMINISTIC CONTEXT (always, offline)      │
│     projected from Research Lab, never re-derived│
└──────────────────────────────────────────────────┘
```

**AI may:** reorder candidates, rewrite prose, draft captions, suggest which figures suit the
stated objective.

**AI may never:** compute or state any number; choose bin widths or axis ranges; determine *n*;
decide significance; invent a chart type absent from the deterministic candidate set.

That last constraint matters most: **AI selects from a whitelist the engine produced.** Validate
every response against the candidate set and discard anything unrecognised.

**Captions** should extend the narrative generator rather than going to AI. *"Figure 1.
Distribution of HbA1c by treatment group (n = 84). Box plots show median and IQR; whiskers
1.5×IQR. Welch t = 3.21, p = 0.002."* — every element is a known number. A deterministic
template will be more accurate than any LLM.

---

## 6. Required project data

**Per variable:** display name, `RecoVarKind`, ordinality, `RecoRoleClass`, matched column, valid
n, missing n, observed category count and labels, units, and for continuous variables the
descriptive summary.

**Project level:** title, objective, `TwoByTwoStudyDesignKind` plus raw study-type text, arm
structure, primary and secondary outcomes, row count, `StatisticsReadinessState`.

**Analysis level:** every `SavedComputedResult` with test, variables, effect size, CI, p-value,
n and staleness.

**Gap:** OrbitLab has no explicit *time* or *event/censoring* variable concept, and no
paired/repeated-measures marker. Both are needed for survival and paired designs and are absent
today — a real dependency for the roadmap, not something Charts Studio can paper over.

---

## 7. How recommendations are made

**Candidate generation** enumerates variable combinations and emits every structurally valid
chart:

| Structure | Primary | Alternatives | Rejected |
|---|---|---|---|
| 1 × Continuous | Histogram | Density, box, violin | — |
| 1 × Binary/Nominal | **Bar** | Stacked bar, dot | **Pie — de-ranked (§1.3)** |
| 1 × Ordinal | Ordered bar | Stacked bar | Pie |
| Continuous × Binary | **Box plot** | Violin, bar+errorbar, dot | Bar alone at small n |
| Continuous × Nominal (3+) | Box plot, grouped | Violin, strip | Line |
| Continuous × Continuous | **Scatter** (± fit) | Hexbin at high n | Line without time order |
| Categorical × Categorical | Grouped/stacked bar | Mosaic | Pie pairs |

**Scoring:**

1. **Bound to an existing computed result** — heaviest weight
2. **Primary outcome involvement**
3. **Objective relevance** — deterministic keyword overlap; AI refines at L3
4. **Statistical honesty** — penalise bar-of-means at small n, scatter below n = 10, thin cells
5. **Publication convention** — penalise pie; prefer distribution-revealing plots
6. **Table 1 redundancy** — de-rank figures duplicating a baseline table

**Guardrails, always deterministic:**

- Never chart a variable the engine marked `Excluded` or `Unsupported`
- Never produce a group below the small-n threshold without a visible warning
- Never draw a fit line without stating the model and its assumptions
- **Never annotate significance on a figure not bound to an actual computed test** — the single
  most dangerous thing this feature could do

---

## 8. MVP

**In:** project selection, deterministic context, readiness gate, deterministic recommender with
rationale, five chart types (histogram, box, bar, scatter, bar-with-error-bars), single canvas
plus inspector, two-layer spec with reset, binding to `SavedComputedResult` with inherited
staleness, deterministic captions, export at 300/600 DPI plus one vector format, persistence
with stale chips, full offline operation.

**Out of MVP, deliberately:**

- **AI enhancement** — ship L1+L2 first and validate the deterministic recommender against real
  projects. This also de-risks the release: no network dependency in v1.
- Multi-panel composite figures
- Themes beyond one good publication default
- **Kaplan-Meier, ROC, forest plots** — blocked on engines that don't exist

**Dependency worth surfacing now:** the three figure types medical researchers ask for most
after the basics are Kaplan-Meier, ROC and forest plots. OrbitLab has no survival engine, no
ROC/AUC engine and no regression or meta-analysis engine. Charts Studio will *create demand* for
those; plan the sequencing deliberately rather than discovering it from complaints.

---

## 9. Roadmap

| Version | Adds |
|---|---|
| v1.1 | AI enhancement (L3) — objective-aware re-ranking, plain-language rationale, captions |
| v1.2 | Composite multi-panel figures with shared legends and A/B/C labelling |
| v1.3 | Report Builder integration — figures flow into the DOCX/PDF exporters with numbering and cross-references |
| v1.4 | Journal profiles — per-journal presets for column width, fonts, DPI, colour policy |
| v2.0 | New engines and their figures: survival → Kaplan-Meier, ROC/AUC → ROC curves, regression → forest and coefficient plots |
| v2.x | Accessibility and reproducibility — colourblind-safe defaults, greyscale-safe encoding, figure provenance records |

v1.3 is arguably the highest-value item, since it completes a workflow no competitor spans.

---

## 10. Risks and edge cases

**Product risks**

- *Confidently wrong figures* — the gravest risk. Mitigated by readiness gating, never charting
  excluded variables, small-n warnings, and never annotating uncomputed statistics.
- *Automation complacency* — users accepting suggestions without understanding them, the exact
  failure mode for a low-statistics audience. Mitigated by treating the rationale as the
  product, not decoration.
- *Scope gravity* — chart builders expand without limit. Mitigated by five types in MVP and
  expansion gated on statistical engines rather than requests.

**Technical edge cases**

Single-category variables; all-missing variables; constant continuous variables (zero variance
breaks scaling); extreme cardinality; extreme n (downsampling or hexbin, never a frozen UI);
very small n; outliers dominating axis range; long or non-Latin labels; RTL text; unicode in
variable names reaching export; **differing missing-data patterns across variables in one
figure, making the figure's stated n ambiguous** — a genuinely hard specification problem worth
solving explicitly; stale bindings; 600 DPI memory and time; palettes failing in greyscale or
for colourblind readers; **DPI/WYSIWYG drift**.

**Operational**

AI latency or failure (must degrade invisibly); cost per project if L3 runs on every change
(debounce, cache by context hash); users on old versions never seeing the feature.

---

## 11. Implementation strategy

**Core abstraction: a serialisable, versioned `ChartSpec`.** Everything — recommendation,
rendering, editing, persistence, export — operates on this one object, carrying a schema version
from day one.

```
ChartSpec
  ├ SchemaVersion
  ├ ChartType
  ├ VariableBindings (x, y, group, facet)
  ├ Aggregation & error-bar convention
  ├ Scales, axes, limits
  ├ Labels, caption, annotations
  ├ Style ref (palette, fonts, sizing)
  ├ BoundResultId?  ──► SavedComputedResult
  └ UserPatch (overlay layer, §1.7)
```

**Build order:** spec + persistence + versioning → renderer abstraction → recommendation engine
→ studio UI → export pipeline → captions → *(v1.1)* AI layer.

**Rendering library** needs a spike, not a decision on paper. Criteria: permissive licence
(commercial product), true high-DPI and vector export, scientific-plot correctness, WPF/.NET 8
support, no external runtime. **Run a spike that renders one box plot to 600-DPI raster and
vector and compares it pixel-for-pixel to the on-screen render.** WYSIWYG fidelity is the
acceptance criterion and the thing most likely to eliminate a candidate.

**Testing:** golden tests pinning candidate sets and ranking (only possible because L2 is
deterministic); spec round-trip and migration; patch re-application after regeneration; golden
*model* comparison rather than brittle pixel diffs; every §10 edge case as an explicit test.

---

## Summary of decisions

| Original proposal | Decision | Why |
|---|---|---|
| AI extracts project context | Deterministic C# extraction | App already knows it; AI adds hallucination risk and a second source of truth |
| AI recommends charts | Deterministic recommender, AI re-ranks | Reproducible, offline, testable, free |
| Gender → pie chart | Bar chart; pie de-ranked | Pie reads as naïve and encodes quantity poorly |
| AI Mode / Manual Mode | One canvas, AI as starting state | Halves surface area; no dead end |
| Charts are editable | Two-layer spec: base + user patch | Regeneration must not destroy edits |
| Charts are exportable | Same spec → screen and 600 DPI | WYSIWYG drift is discovered only after submission |
| Select project → recommend | Insert readiness gate | Never launder unready data into an authoritative figure |
| — | Chart↔result binding in MVP | The actual moat |

**The strongest version of this feature is not "AI makes charts". It is: OrbitLab already knows
what you analysed, so it already knows what your figures should be — and it can prove the
figure, the statistics and the Results paragraph agree.**
