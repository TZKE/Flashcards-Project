# Charts Studio — The Experience

**Phase 0 design document 3 of 4.** Experience design only. This document deliberately replaces
parts of documents 1 and 2 where a better answer emerged, and says where.

---

## 1. The one idea

Every charting tool ever built — Excel, Prism, Canva, Power BI, and the Template Gallery of
document 2 — opens with the same question:

> **"What would you like to make?"**

That question is the problem. It assumes the user knows. It puts a blank surface between a
researcher and their paper. And it is *unnecessary*, because OrbitLab already holds the
variables, the design, the computed results and the objective.

So Charts Studio should never ask.

> ### OrbitLab doesn't ask you to make figures. It shows you the figures your paper needs, and you decide which ones make the cut.

**Creation becomes curation.** The user's job shifts from *building* (hard, requires expertise,
produces anxiety) to *choosing* (easy, requires only judgment, produces confidence).

---

## 2. What this replaces

| Document 2 said | Now | Why |
|---|---|---|
| **Template Gallery is the front door** | **Contact Sheet is the front door** | The gallery still asked "what do you want to make?" — the right answer to the wrong question |
| Gallery = primary surface | Gallery = "Add a figure", secondary | Still valuable for deliberate additions; wrong as an opening |
| Live previews of *templates* | Live **figures**, already made | A preview says "this is what it could look like". A figure says "this is yours". |
| Single figure is the unit | **The figure set** is the unit | Nobody publishes one figure |

---

## 3. First impression: the opening sequence

Four seconds decide whether this gets remembered.

**Beat 1 — Entry (0.0s).** With one recent project, open directly into it. The picker appears
only when the choice is real.

**Beat 2 — The read (0.0–1.2s).** Not a spinner. A spinner says "waiting"; this should say
**"understanding"**.

```
                Reading Diabetes Cohort 2026

        18 variables    ·    84 participants    ·    4 analyses

          ▸ 6 continuous   ▸ 9 categorical   ▸ 3 excluded
                    ▸ Two-arm · Cohort
             ▸ Primary outcome: HbA1c change
```

Each line arrives ~180ms after the last. It is honest — real work is happening — and it is the
first proof of comprehension. It costs a second and buys the entire framing.

**Beat 3 — The develop (1.2–2.6s).** Figures **develop onto the sheet**, photographic: frame,
then axes, then data settling. Staggered 80ms apart so it reads as a sequence.

**Beat 4 — Rest (2.6s).** Six finished figures. Numbered. Captioned. Real data. One click.

The metaphor is deliberate: a photographer's **contact sheet**. You don't shoot a contact sheet —
you review it and circle the keepers. That makes the interaction legible without instruction.

---

## 4. Three surfaces

Not tabs — depths. You move inward and back out.

```
   CONTACT SHEET  ──▶  CANVAS  ──▶  SHELF
   (survey)            (refine)     (assemble)
       ◀────────────────────────────────
```

### 4.1 Contact Sheet — survey

Cards in a grid, each showing the figure, its subject, n, any bound test, and two gestures: **♥
keeps, ✕ cuts.** Keeping animates the figure toward the Shelf; cutting fades and collapses it,
with undo lingering ~6 seconds.

**Space bar opens a full-size preview** — Quick Look, borrowed from Finder, and the best keyboard
borrow available. Arrow keys move through figures *inside* the preview, so the whole set can be
reviewed at full size without leaving the keyboard.

The last cell is always **Add a figure** → the Template Gallery, correctly positioned as a
deliberate secondary act.

### 4.2 Canvas — refine

Enter by clicking or pressing Enter. Not a new screen — the figure **expands in place**, the
sheet dissolving behind it. Escape returns you to exactly where you were, scroll position intact.

Three columns — variables left, figure centre, inspector right — but only the middle is loud, and
controls stay collapsed until touched. **A user who wants no options sees almost none.**

### 4.3 Shelf — assemble

Where kept figures live as **a set**. Drag to reorder; **numbers renumber themselves** with
neighbours sliding, and captions renumber with them.

**The set-consistency strip is the quiet killer feature.** No tool tells you Figure 3 uses SEM
while Figures 1, 2 and 4 use 95% CI. Reviewers catch that. Supervisors catch that. OrbitLab
catching it first — with one click to harmonise — is what a researcher mentions to a colleague
unprompted.

---

## 5. Variable Explorer & direct manipulation

The left rail is permanent, searchable, and **is the drag source for everything**.

Each variable shows a **sparkline of its own distribution** — 40×12px. Small, cheap, and
outsized in effect: the shape of every variable in the study at a glance, which no statistical
package shows without three clicks.

| Gesture | Result |
|---|---|
| Drag variable → empty canvas | Best single-variable figure for its type |
| Drag variable → existing figure | Figure **morphs** — axes animate to the new scale |
| Drag variable → an axis | Assigns to that role |
| Drag two variables together | The right bivariate form |
| Drag figure → Shelf | Keep |
| Drag figure → another figure | Offer to combine as a two-panel figure |

**The morph is worth the effort.** Blanking and redrawing feels like a page load. Axes easing
while points travel makes the figure feel like *a thing* rather than a picture of a thing.

**Invalid drops never fail silently.** Dragging a nominal variable onto a histogram's Y shows,
inline: *"Histogram needs a continuous variable — Sex has 2 categories. Bar chart instead?"* with
one-click accept. **The rejection teaches, and offers the fix.**

---

## 6. Command bar & keyboard

`⌘K` / `Ctrl+K`. One field: variables, figures, templates, themes, journals, actions — one ranked
list. Typing a variable name and pressing Enter produces a figure of it. **That is the fastest
path from thought to figure in any research tool.**

| | |
|---|---|
| `⌘K` | Command bar |
| `Space` | Quick Look preview |
| `←→` `JK` | Move between figures |
| `Enter` / `Esc` | Open / back |
| `F` / `X` / `⌘Z` | Keep / cut / undo |
| `1–9` | Jump to figure n |
| `⌘D` | Duplicate |
| `⌘E` / `⇧⌘E` | Export figure / set |
| `⌘⇧T` | Cycle theme across the set |
| `?` | Shortcut sheet |

Shortcuts are **discoverable in place** — hovering Keep shows `F`. Users graduate from mouse to
keyboard without reading documentation.

---

## 7. AI, handled with restraint

In a tool whose value is trustworthiness, **a quiet AI is a premium AI.**

- **Nowhere in the numbers.** Stated once, visibly: *"Every value in every figure is computed
  locally by OrbitLab. AI is never involved in calculation."* This sentence sells more than any
  feature.
- **Ordering** the contact sheet against the objective.
- **Writing the "why"** in one plain sentence.
- **One ambient suggestion at a time**, never a queue, never modal.
- **Caption polish** — offered, never automatic, always on top of an already-correct caption.

**Never:** deciding significance, choosing bins, computing n, generating an unvalidated form, or
writing anything a reviewer would read as a claim.

The AI indicator dims when offline and *nothing else changes*. Users who never notice the AI
still get the full product.

---

## 8. Provenance receipts

The trust moat, made physical. One line, expanding with a slide that feels like paper feeding out:

```
   ┌─ PROVENANCE ────────────────────────────┐
   │  Dataset   diabetes_2026.csv            │
   │            imported 14 Jul · 91 rows    │
   │  Rows used 84  ·  7 excluded (missing)  │
   │  Variables BMI (continuous)             │
   │            Group (2 categories)         │
   │  Test      Welch t-test                 │
   │            t = 3.21 · p = .002 · 95% CI │
   │  Computed  16 Jul 19:04                 │
   │  [ Copy for methods ]  [ Verify ▸ ]     │
   └─────────────────────────────────────────┘
```

**"Verify" walks back to the computed result and from there to the rows.** A supervisor can trace
any published figure to its source in two clicks. For anyone who has been asked *"where did this
number come from?"* three months later, that is worth the price of the software.

**Staleness is expressed on the figure itself, not as a badge.** When data changes, the figure
**desaturates** — visibly muted among vivid siblings. You see it instantly across the whole set
without reading anything.

---

## 9. Figure history

Every figure keeps a timeline; hovering a point previews that version in place, clicking restores
it non-destructively. A researcher who changed a figure three weeks ago and can't remember
whether the submitted version used SD or SEM has a real, stressful problem — and this solves it.

---

## 10. Themes & Journal mode

**Themes are the Canva part** — free experimentation, safe because it cannot affect correctness.
Three or four: **Journal**, **Mono** (greyscale-safe), **Presentation**, **Poster**.

`⌘⇧T` cycles them, and **every figure re-themes simultaneously** in one 250ms transition. Seeing
four figures change together teaches the "these are a set" model better than any onboarding copy.

**Journal mode is a mode, not an export setting** — one of the strongest ideas here. Choosing a
target journal makes the canvas show each figure **at its true printed size in that journal's
column width**, so an 8pt label that will be illegible in print *looks illegible now*. Fonts,
line weights and DPI match the target; mono-printing journals switch the theme automatically and
flag colour-only encodings; the Shelf warns if the figure count exceeds the limit.

**Users find out their figure won't work at submission size while they can still fix it.**

---

## 11. Submission workflow

`⇧⌘E` → **Export figure set**: a preset (e.g. "BMJ submission — 600 dpi TIFF, mono-safe"), and
components — figures, a figure-legends page (.docx), a provenance appendix (.pdf), optional
editable source.

One action produces **the complete figure package a journal actually asks for** — correctly
named, correctly numbered, at the right DPI, with legend numbering that matches the files.

This is where the narrative generator and DOCX/PDF exporters compound into something no chart tool
can match: not *"here's your PNG"* but ***"here's your submission."***

Completion is quiet — files assembling, then *"4 figures ready for BMJ."* No confetti. Confetti is
for consumer apps; researchers want competence.

---

## 12. Collections & reuse

**Template collections** organised by *study shape*: clinical trial primary paper,
cross-sectional survey, case-control, diagnostic accuracy *(future)*, cohort time-to-event
*(future)*. Opening one shows the five figures such a paper typically contains — **a curriculum
disguised as a menu**, and possibly the most educational surface in the product.

**Personal collections** let a researcher save a recurring set and apply it to a new project in
one action — turning a day into a minute for anyone publishing repeatedly, and the feature that
keeps a subscription alive between papers.

---

## 13. Empty and degraded states

- **No projects** — one warm line, one button, and a genuine **sample project** so a new user can
  experience the contact-sheet moment in ten seconds without owning data. *This is the demo that
  sells the feature*, and it costs one curated file.
- **No dataset** — routed to import, with the promise stated: *"Import your data and OrbitLab will
  propose your figures."*
- **Not ready** — no contact sheet; what's blocking, in plain language, Magic Fix one click away.
- **Everything cut** — offer to propose a different set. **A user should never reach a dead end.**
- **Offline** — full sheet, full figures, full export. Only AI ordering degrades, silently.
  Nothing is greyed out.

---

## 14. Micro-interactions worth building

Figures **develop** rather than appear · **Keep** arcs toward the Shelf, which pulses · **Cut**
collapses and closes the gap, undo lingering 6s · **Morph** on variable change · **Theme** sweeps
all figures at once · **Stale figures desaturate** · **Receipt** slides out like paper ·
**Reorder** slides neighbours, numbers roll like an odometer · **Sparklines** animate in ·
**Hovering a variable** highlights every figure using it · **Drag rejection** explains and offers
the fix · **Export** visibly assembles files.

**No sound. No confetti.** Premium desktop software for professionals is quiet. The most
delightful thing this app can do is feel *solid* — and everything must respect reduced-motion,
degrading to instant transitions with zero loss of function.

---

## 15. Accessibility — where it converges with publication quality

The most useful insight here: **for a publication tool, accessibility and journal requirements are
the same requirement.**

- Many journals still print greyscale → **greyscale-safe encoding is a submission requirement**
- Greyscale-safe is also colourblind-safe
- Distinguishing series by *shape and pattern*, not hue alone, satisfies both at once

So the accessible default is also the professionally correct default. **Colourblind-safe palettes
ship as the default, not as an option** — the alternative isn't just less accessible, it's less
publishable.

Beyond palette: complete keyboard operation, screen-reader labels that describe figures
meaningfully (*"box plot, BMI by treatment group, two groups, 84 observations"* — not *"image"*),
OS font scaling, focus indicators surviving the theme system, full reduced-motion support, and a
figure's underlying data always available as a table — which serves screen readers and reviewers
equally.

---

## 16. Large datasets

Never freeze, never lie. Contact sheet renders from **summaries first**; scatter plots
**auto-suggest hexbin** above a threshold, saying why; point counts are stated honestly
(*"showing 10,000 of 240,000 points — density preserved"*); rendering is always cancellable;
export runs at full fidelity regardless of what the screen showed.

**The app should feel identical at 84 rows and 840,000. The only acceptable difference is honest
labelling.**

---

## 17. Two users, one surface

**Layla — first paper.** Opens Charts Studio. Sees six figures of her own data. Reads
plain-language reasons. Hearts four. `⇧⌘E`. Submission-ready package in under three minutes,
having made zero statistical decisions and read four sentences explaining why each figure suits
her data.

**Dr. Adel — twelfth paper.** `⌘K`, "bmi by group", Enter. Figure exists. `⌘⇧T` to Mono. Drags
HbA1c onto a second figure. `⇧⌘E`. Ninety seconds, no mouse except the drag.

**Same surface. No modes, no beginner/advanced toggle.** The difference is entirely in which
affordances each reaches for. Progressive disclosure done properly means the expert's tools were
always there, unobtrusive, waiting to be discovered.

---

## 18. Collaboration — an honest constraint

**OrbitLab is local-first by deliberate architectural commitment.** Figma-style real-time
multiplayer would require putting research datasets on a server — contradicting the privacy
posture and, for clinical data, possibly the user's ethics approval. **That is not a gap to close;
it is a position to defend.** "Your patient data never leaves your machine" is a selling point to
an ethics committee.

Collaboration should be **asynchronous and artifact-based**, which is how academic collaboration
actually works:

- **Figure review package** — a self-contained file a supervisor opens, sees figures with
  provenance, and annotates; comments return as a file. Mirrors tracked-changes manuscripts.
- **Supervisor sign-off** — a set marked reviewed, with who and when recorded in provenance.
- **Set templates as the sharing unit** — a lab shares its standard figure set, not its data.

That last is the institutional wedge: **share the method, never the data.**

---

## 19. What makes someone tell a colleague

1. *"It had my figures already made when I opened it."* — the contact sheet
2. *"It told me my labels would be too small for the journal — before I submitted."*
3. *"It caught that one figure used different error bars."*
4. *"It exported the whole package — figures, legends, correctly numbered."*
5. *"I typed a variable name and got a figure."*
6. *"I could trace any figure back to the exact rows."*

Every one is possible **only because OrbitLab owns the analysis**. The delight and the moat are
the same thing.

---

## 20. What to cut

| Cut | Why |
|---|---|
| Real-time collaboration | Contradicts local-first; async artifacts fit academia better |
| Sound design, confetti | Wrong register for professional research software |
| Custom theme editor | Four excellent themes beat an editor |
| AI chat interface | A chat box invites users to ask AI for numbers. Never offer that surface. |
| Difficulty ratings | Already cut; still cut |
| More than 6 initial figures | Six is reviewable at a glance; twelve is a chore |

**Protect above everything: the opening sequence.** If schedule pressure comes, cut a chart type,
a theme, history, collections. **Do not cut the four seconds where the app reads the project and
develops the figures.** That moment *is* the product.

---

> **Open Charts Studio, and your paper's figures are already there. Keep the ones you want.
> Export the set.**
