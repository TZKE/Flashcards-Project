# Charts Studio — Template Gallery & Live Preview

**Phase 0 design document 2 of 4.** Product and UX only.

---

## 0. A tension worth resolving first

Two goals pull against each other:

> "Users should **not** start by choosing a chart."
> "Users should see a **gallery of templates** — Histogram, Box Plot, Scatter Plot…"

A template gallery *is* choosing a chart. If the gallery is the primary surface, the thing it set
out to replace has been rebuilt, just prettier. The medical student who doesn't know whether they
need a box plot or a violin plot is no better off staring at eleven beautiful cards than at a
dropdown.

**Resolution, which shapes everything below:**

1. **The default surface is question-framed, not chart-framed.** Users land on "Recommended for
   this project", where cards read *"Compare BMI between treatment groups"* — not *"Box Plot"*.
2. **The gallery is the secondary, browse surface** — for exploring, disagreeing with the
   recommendation, or knowing exactly what you want.
3. **Even the browse surface is organised by research question**, not chart vocabulary.

Gallery yes — but never as the front door, and never organised around words the target user
doesn't have.

---

## 1. Challenges

### 1.1 The "template" metaphor comes from the wrong industry

Canva and PowerPoint templates are **aesthetic** — interchangeable skins. Swapping one always
works; it just looks different.

Chart types are **not** interchangeable. A histogram of a categorical variable isn't ugly, it's
*meaningless*. The template metaphor quietly teaches *"these are all valid options, pick the one
you like"* — the most dangerous possible lesson for users with weak statistical grounding.

But the underlying instinct — visual, browsable, low-intimidation — is right. The fix is
splitting a conflated concept:

| | **Form** | **Style** |
|---|---|---|
| What it is | Histogram, box plot, scatter | Colours, fonts, grid, sizing |
| Determined by | Your data structure | Your taste / target journal |
| Freely swappable? | **No** — validity-gated | **Yes** — this is the Canva part |
| Where it lives | Template Gallery | Theme picker + Inspector |

Separating them lets the gallery be honest about validity *and* gives a genuinely Canva-like
surface where aesthetic freedom is appropriate. Users get "make it beautiful instantly" without
"make it wrong instantly".

### 1.2 Drop "Difficulty" — it measures nothing useful

Difficulty of *what*?

- **Making it?** Charts Studio makes it for you. Difficulty is zero by design — advertising it
  contradicts the value proposition.
- **Interpreting it?** A different axis, and not what a badge reading "Intermediate" conveys.
- **Defending it to a reviewer?** Different again.

The real risk: a student's data calls for a box plot, they see "Difficulty: Intermediate", and
pick the bar chart marked "Easy" — which hides the distribution and is the weaker figure. **A
difficulty badge would actively push users toward worse choices.**

**Replace with two fields that answer what users actually need:**

- **"Best for"** — *"Comparing a continuous measure across 2–5 groups."*
- **"Avoid when"** — *"Groups have fewer than ~5 observations — use a dot plot instead."*

Same reassurance function, pointed at appropriateness rather than intimidation.

### 1.3 Two gallery sections isn't enough — three tiers

"Recommended" and "All Templates" leaves the biggest question unanswered: within *All*, which
work with my data? Undifferentiated browsing invites invalid selections.

1. **Recommended for this project** — valid, and tied to your variables or a computed result
2. **Available for your data** — structurally valid, not specifically recommended
3. **Not applicable to this project** — **shown, greyed, with a stated reason**

Tier 3 is the one usually cut, and the most valuable. *"Kaplan-Meier — needs a time-to-event
variable and a censoring indicator, which this project doesn't have"* teaches something real at
the moment of curiosity. Hiding it makes the app feel arbitrary. **It is a teaching surface, not
a failure state** — and for this audience, teaching is much of the product.

### 1.4 Line Chart is a trap

A line chart implies a meaningfully ordered x-axis, almost always time. **OrbitLab has no
longitudinal or time-variable concept**, and no paired/repeated-measures marker. Offering it
means users will apply it to categorical x and connect unrelated categories — a misuse reviewers
notice immediately.

**Recommendation:** gate it hard behind an explicitly ordered variable, or defer. Given the
current model, defer.

### 1.5 Be disciplined about "(future)" templates

Forest Plot and Kaplan-Meier are correctly future — both blocked on statistical engines that
don't exist. Showing them has two outcomes:

- **Good:** a "Coming soon" card that captures demand — users click, register interest, and you
  learn what to build first.
- **Bad:** permanent vaporware furniture that makes the gallery feel like a mockup.

The difference is whether clicking does something. **If a future card cannot record interest and
set an expectation, omit it.** Never let one be indistinguishable at a glance from a real card.

### 1.6 Live previews are the strongest idea here

Stated plainly because it deserves it: **generic template thumbnails are a solved, boring
problem; previews rendered from the user's own variables are a genuine product moment.** The
instant a researcher sees *"Histogram of HbA1c"* — their variable, their distribution — the app
has proven it understands the project. No competitor does this, because none holds the analysis
context.

It is also the hardest thing here, and §7 treats it as a first-class system.

---

## 2. Vision

> **Charts Studio opens on figures that are already about your research.**

Not a blank canvas. Not a chart-type menu. A gallery where the previews are *your* variables and
the reasons are written in *your* project's terms.

The emotional target for the first five seconds is recognition: **"It already knows what I'm
working on."** That moment converts a trial user into a paying one.

The functional target is that a user with no statistical training produces a figure a reviewer
won't object to — and understands *why* it was right.

---

## 3. Information architecture

```
Charts Studio
├─ Project context (loaded once, deterministic)
│    variables · types · design · objectives · results · readiness
├─ FORM layer ── Template Gallery
│    validity-gated · question-organised · live previews
├─ STYLE layer ── Themes  (the Canva-like part)
│    palette · typography · grid · sizing · journal presets
└─ Figure workspace
     canvas · inspector · figure tray · export
```

**Changing style can never invalidate a figure; changing form always revalidates it.** Users can
experiment freely with appearance and never break correctness — the safety that makes a
Canva-like surface appropriate here.

---

## 4. Template categories

Organised by **research question**, since that is what users have in their heads.

| Category | Question it answers | Templates |
|---|---|---|
| **Distribution** | "What does this variable look like?" | Histogram, density, dot plot, violin |
| **Comparison** | "Does this differ between groups?" | Box plot, violin by group, grouped bar + error bars, dot plot by group |
| **Relationship** | "Are these two things related?" | Scatter (± fit), hexbin, correlation heatmap |
| **Composition** | "How do the parts break down?" | Stacked bar, 100% stacked bar, grouped bar |
| **Change / order** | "How does this change across an ordered scale?" | Line *(gated — §1.4)* |
| **Effect summary** | "What's the overall effect?" | Forest plot *(future)* |
| **Time-to-event** | "How long until the outcome?" | Kaplan-Meier *(future)* |

Category headers are phrased as questions. A user who doesn't know what a box plot is *does* know
they want to compare two groups — and lands in the right category without learning any chart
vocabulary.

**Pie is absent.** It should exist in manual mode but never be surfaced as recommended in a
publication tool.

---

## 5. Template card anatomy

```
┌─────────────────────────────────┐
│      [ LIVE PREVIEW ]           │  ← user's own data where possible
│                          ✦ AI   │  ← recommendation badge
├─────────────────────────────────┤
│ Box Plot                        │  ← form name (secondary)
│ Compare BMI across treatment    │  ← what it does HERE (primary)
│ groups                          │
│                                 │
│ Best for   2–5 groups, continuous
│ Avoid when groups under ~5 obs  │
│                                 │
│ [comparison] [continuous] [n=84]│
│ ⬤ Bound to Welch t-test result  │
└─────────────────────────────────┘
```

**Deliberate choices:**

- **Project-specific phrasing is primary; the chart name is secondary.** "Compare BMI across
  treatment groups" is what the user is trying to do. Inverting the usual hierarchy is what makes
  the gallery question-first even while browsing.
- **`Best for` / `Avoid when`** replace Difficulty (§1.2).
- **Provenance chip** — when a template maps onto an existing computed result, say so. The moat,
  surfaced at card level.
- **The AI badge is a small mark, not a banner.** If it dominates, users click badges instead of
  reading — automation complacency by design.

Not-applicable cards keep the same skeleton, greyed, with the reason replacing tags:

```
┌─────────────────────────────────┐
│   [ dimmed generic preview ]    │
├─────────────────────────────────┤
│ Kaplan-Meier                    │
│ Not applicable to this project  │
│ Needs a time-to-event variable  │
│ and a censoring indicator.      │
│ [ What's this? ]                │
└─────────────────────────────────┘
```

---

## 6. The gallery

```
┌──────────────────────────────────────────────────────────────┐
│  Charts Studio          Project: Diabetes Cohort 2026  [▾]   │
│  18 variables · 84 rows · 4 computed results · Ready ✓        │
├──────────────────────────────────────────────────────────────┤
│  [ Search ]   Category ▾   Variable ▾   ☐ Only my variables  │
├──────────────────────────────────────────────────────────────┤
│  RECOMMENDED FOR THIS PROJECT                          (5)   │
│  Based on your variables and the analyses you've run.        │
│  ┌────────┐ ┌────────┐ ┌────────┐ ┌────────┐                │
│  │ HbA1c  │ │ BMI by │ │ HbA1c  │ │ Outcome│                │
│  │ distrib│ │ group  │ │ vs Age │ │ by arm │                │
│  │  ✦ AI  │ │ ✦ AI ⬤│ │   ⬤   │ │        │                │
│  └────────┘ └────────┘ └────────┘ └────────┘                │
│                                                              │
│  AVAILABLE FOR YOUR DATA                              (7)   │
│  Valid for your variables, not specifically suggested.       │
│                                                              │
│  NOT APPLICABLE TO THIS PROJECT                       (3)   │
│  Shown so you can see what these need.  (dimmed + reasons)   │
└──────────────────────────────────────────────────────────────┘
```

**Filtering by variable is the underrated control.** Selecting "BMI" reframes the gallery around
one variable — every valid figure involving it, ranked. That matches how researchers work ("I
need a figure for my primary outcome") and turns the gallery from a chart catalogue into a
**per-variable figure finder**.

The header context strip is load-bearing: it proves the project was understood before anything is
recommended.

**Scrolling note:** this is a long scrollable surface of card grids — precisely the structure that
produced the wheel dead-zones fixed in `b31415c`, and the deferred non-virtualising
`ItemsControls` issue from that commit applies directly. Flag it in the UX spec, not just the
technical one: the failure mode is a *feel* problem.

---

## 7. Live preview system

### 7.1 Three fidelities

| Level | Shows | When |
|---|---|---|
| **L1 — Live** | Real render, user's variables, real data | A valid combination exists |
| **L2 — Representative** | Real render, user's variables, reduced or sampled | Data too large for a fast thumbnail |
| **L3 — Generic** | Stylised illustrative example, clearly labelled | No valid combination exists |

**L3 must be unmistakably not-your-data.** If a generic example looks like a real render, users
will believe it reflects their project and later feel deceived. Use a visibly schematic treatment,
labelled "Example".

### 7.2 Which variable does the preview choose?

The hardest question here, and the one that decides whether previews feel smart or dumb.

**Deterministic priority:**

1. A variable involved in an **existing computed result** — strongest evidence of interest
2. The **primary outcome** by role
3. The variable named in the **research objective**
4. Highest **completeness** among valid candidates
5. Stable tiebreak (declaration order) — **never random**

**Previews must be stable across sessions.** If the histogram card shows Age today and HbA1c
tomorrow, the gallery feels unreliable and users stop trusting anything they saw. Determinism here
is a UX requirement, not just an engineering one.

**Cards should also be swappable** — a small variable control lets the user cycle the preview
subject without leaving the gallery, turning each card into a lightweight exploration surface.

### 7.3 Preview honesty

- Previews are **visually framed as previews**, never presented as final output
- **Never annotate significance in a preview.** A p-value glimpsed on a thumbnail will be quoted.
- Small-n and readiness warnings are **visible on the card**, not deferred to the editor
- If readiness is `NeedsReview`, previews carry a review marker throughout

### 7.4 Progressive rendering

```
open → skeleton cards (instant, correct titles/labels)
     → recommended tier renders first (visible viewport only)
     → remaining tiers render on scroll
     → cache by (project version × template × variables)
```

Titles and descriptions are text and appear immediately, so the gallery is **readable and
meaningful before a single chart has drawn**. Invalidate the cache on data change, variable
metadata change, or new computed result. A stale preview is worse than a slow one.

---

## 8. Screens

### 8.1 Project picker

Every project appears; unusable ones are visibly disabled **with the specific next action**
(Import data / Review data), never hidden. Same principle as gallery tier 3.

### 8.2 Workspace

```
┌──────────────────────────────────────────────────────────────┐
│ ← Gallery    Diabetes Cohort 2026 · Figure 2      [Export ▾] │
├──────────┬────────────────────────────────────┬──────────────┤
│ FIGURES  │      ┌──────────────────────┐      │ INSPECTOR    │
│ [F1]     │      │       figure         │      │ ▾ Data       │
│ [F2] ◄   │      └──────────────────────┘      │ ▾ Statistics │
│ [F3]     │  ⬤ Bound: Welch t = 3.21, p = .002 │ ▾ Labels     │
│ + New    │  ⚠ Data changed — [Refresh]        │ ▾ Style      │
│ THEME    │  Figure 2. Distribution of BMI by  │ [Reset]      │
│ ○ Journal│  treatment group (n = 84)…         │              │
└──────────┴────────────────────────────────────┴──────────────┘
```

Left rail carries the figure tray **and** the theme switcher — style changes apply across
figures, reinforcing that a paper's figures should look like a set. The caption sits beneath the
canvas where it will be read and edited.

### 8.3 Template detail (optional)

Opened from a card's info affordance rather than by default, since forcing it would slow the
confident user. Includes a large live preview, the description, `Best for` / `Avoid when`, a
**"Reviewers expect…"** line, and the in-project binding with swappable variables.

---

## 9. Navigation

**Charts Studio must not be a tollgate.** Three entries for three intents:

| Entry | Intent | Lands on |
|---|---|---|
| Nav → Charts Studio | "I need figures" | Project picker → gallery |
| Computed result → "Chart this" | "I want *this* figure" | Workspace, pre-bound, gallery skipped |
| Variable → "Show figures for this" | "I need a figure for my outcome" | Gallery, pre-filtered |

The second is the **highest-intent path in the product** and should be at least as prominent as
the gallery.

---

## 10. Journeys

**Layla — 4th-year medical student, first paper.** Opens Charts Studio, sees five cards showing
*her* variables. Reads *"Compare BMI across treatment groups"*, not chart names. Clicks. A
finished figure appears, noting "Bound to Welch t-test" — the test she ran. Reads `Avoid when:
groups under ~5 observations`, checks n = 84, feels reassured. Exports at 300 DPI. **She never
chose a chart type, and she learned why box plots suit her data.**

**Dr. Adel — clinician, specialty journal.** Opens a computed result, clicks **"Chart this
result"**, lands in the workspace pre-bound. Adjusts error bars to 95% CI, picks greyscale-safe
because the journal prints mono, exports 600 DPI TIFF. **Never sees the gallery** — which is why
it must not be mandatory.

**Prof. Haddad — supervisor.** Sees provenance chips naming test and p-value, and one figure
marked stale because data changed. Clicks Refresh; the student's label edits survive. **Provenance
and staleness are what make the tool trustworthy to the person who signs off** — a segment worth
designing for, since supervisors drive institutional adoption.

---

## 11. What to take from other tools

**Canva — instant materialisation.** The real lesson isn't templates; it's that clicking produces
something *finished*, with defaults good enough to ship. **Take:** one click → complete figure.
**Leave:** unbounded aesthetic freedom — Canva's users want differentiation; yours want conformity
to publication norms.

**Figma — non-destructive overrides.** Component/instance overrides that can be reset
individually map exactly onto base-spec + user-patch. Its properties panel is the cleanest
Inspector model. **Leave:** infinite nesting.

**Power BI — role-based field wells.** Dragging a variable into a named role (Axis, Legend,
Values) teaches structure implicitly. **Leave:** its willingness to let you build meaningless
visuals with no warning.

**GraphPad Prism — structure before chart.** The incumbent, and its central decision validates
this whole document: you choose your **data table structure first**, and available graphs follow.
Form is determined by structure — exactly §1.1. Its stats-and-graph coupling is the closest thing
to the provenance model. **Leave:** the dated interface and steep ramp. *The gap between Prism's
correctness and Canva's approachability is precisely where Charts Studio should sit* — and nobody
occupies it.

**Adobe — export presets and artboards.** Named one-click presets covering format, DPI, colour
space and dimensions are the right model for journal profiles. Artboards are the right mental
model for multi-panel figures later.

**Vega-Lite / grammar of graphics.** Its core claim — chart form is *derivable* from data types
and intent — is the academic backing for a deterministic recommender.

---

## 12. Edge cases and degraded states

Each needs a designed screen, not a default: no projects; no dataset; readiness `Blocked`;
readiness `NeedsReview`; all templates not-applicable; one valid variable only; very high
cardinality (refuse with explanation); very large n (L2 previews, hexbin); very small n (warnings
on the card); constant variable; long or non-Latin labels; data changing with figures open;
offline (full gallery, previews and export — only AI ordering degrades, silently).

**The pattern throughout: never hide, never fail silently, always state the reason and the next
action.**

---

## 13. What to cut from v1

| Cut | Why |
|---|---|
| Difficulty ratings | Measures nothing useful; pushes users toward worse figures |
| Line Chart | No time/longitudinal data model to make it valid |
| Pie Chart in gallery | Wrong signal for a publication tool; manual mode only |
| Forest / K-M cards | Only if clicking records interest; otherwise vaporware |
| Multi-panel composites | High value, but after the single-figure loop is proven |
| Custom theme authoring | Three good themes beat a theme editor at this stage |

**Protect at all costs:** live previews using the user's own variables. Everything else is good
product design; that one is what people screenshot and tell colleagues about. If schedule
pressure hits, cut a chart type, not the previews.

---

**The gallery's job is not to present options. It is to demonstrate understanding.**
