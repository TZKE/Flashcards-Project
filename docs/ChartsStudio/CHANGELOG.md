# Charts Studio — Changelog

All notable changes to the Charts Studio module. Dates are when the work landed on
`charts-studio-phase1-foundation-v1`. Commits are local to that branch.

## Release candidate — 2026-07-20

Final quality-hardening and release-candidate verification. No new features.

- **Fixed:** the AI ("Publication Assistant") panel opened empty — its task buttons bind
  visibility to a computed property that never raised change notification, so they stayed
  collapsed. (`1bd3c01`)
- **Fixed:** an empty status line always showed in the AI panel (a string was bound through a
  boolean-to-visibility converter). (`1bd3c01`)
- **Fixed:** Escape now closes every modal (Export, AI assistant, Add Figure) — previously only
  the editor and shelf had keyboard handling; overlays also now take focus on open, so keyboard
  shortcuts work immediately. (`bf313e4`)
- **Added:** placeholder text on the search and export-destination fields (discoverability).
  (`bf313e4`)
- **Fixed:** the render cache was unbounded — a long editing session accumulated one image per
  edit. Now capped at 96 entries with oldest-first eviction; memory stays flat. (`b8a9644`)
- **Removed:** a dead disabled "Move to…" shelf button, and a stale shell comment. (`b8a9644`)
- **Verified:** full end-to-end pass — every chart type, every editable option changing the live
  preview, undo/redo, duplicate/delete/reorder, persistence across restart, PNG/SVG/PDF export,
  all four AI advisory tasks, offline degradation, project switching, Escape on modals, and a
  WPF binding-error trace across the whole run showing zero real binding failures.

## Phases 1–6 — 2026-07-19 → 2026-07-20

- **Phase 1 — Foundation** (`12953d1`): module skeleton (Domain/Application/Infrastructure/
  Presentation), project picker, session lifecycle, per-project sidecar persistence. The
  Research Lab adapter is the sole point of contact with Research Lab.
- **Phase 2 — Contact Sheet** (`0e3e158`): ScottPlot 5 rendering (chosen by a WYSIWYG spike over
  OxyPlot and LiveCharts2), deterministic figure recommendations, keep/remove, add-figure, an
  off-UI-thread render queue.
- **Phase 3 — Figure Editor** (`27ba766`): non-destructive patch layer, unlimited undo/redo,
  live preview, validation.
- **Phase 4 — Figure Shelf** (`27ba766`): curated set, multi-select, drag-reorder, duplicate.
- **Phase 5 — Export** (`29afe13`): PNG, SVG (true vector), and a dependency-free single-page
  PDF; immutable publication profiles; batch export; captions; reproducibility manifest; WYSIWYG
  guaranteed by a shared logical canvas.
- **Phase 6 — AI Advisory Assistant** (`67e1447`): a shared Core AI module wrapping the app's
  existing AI transport, and a Charts Studio advisory layer (caption, critique, accessibility,
  consistency). Deterministic-first: the reviews run fully offline; AI is optional and only adds
  prose. The prompt payload is data-free by construction. **Research Lab was left byte-for-byte
  unchanged.**

## Known limitations

- Histogram, scatter, and grouped-comparison figures are blocked on Research Lab persisting more
  aggregates (bin counts, paired values, per-group summaries); Kaplan-Meier/ROC/forest need
  statistical engines that do not exist yet. These are statistics dependencies, not charting work.
- The PDF embeds a high-resolution raster, not vector art (documented on the format). SVG is the
  true-vector path.
- No command palette / rich keyboard-only navigation beyond Escape, undo/redo and delete.
