# Final Set of User Acceptance Criteria — Objective Alignment

Single authoritative list of what the page must do and how it is verified.
Supersedes `user-spec.md`. Each item keeps its `US-xx` id (used as the `@US-xx`
tag on the matching Playwright test) and points at the functionality inventory
("Objective Alignment Test Spec" artifact).

**App under test:** `app/Objective Alignment v11.html`, served at `/app/…`.

## Status legend

| Mark | Meaning |
|---|---|
| ✅ | Automated and merged to `main` |
| ▢ | Agreed, test still to be written |
| ⚙ | Enforced as a cross-cutting invariant (checked after every test), not a standalone test |
| ⚠ | Automated with a stated limit — the part in brackets is out of scope |

## Test file map

| Area | Spec file | Stories |
|---|---|---|
| Persistence & startup | `01-persistence.spec.ts` | US-01 – US-03 |
| Top bar (name, theme) | `02-topbar.spec.ts` | US-04 – US-07 |
| Modes & portfolio selection | `03-modes.spec.ts` | US-08 – US-13 |
| Portfolio management | `04-portfolios.spec.ts` | US-14 – US-17 |
| Metric & objective rows | `05-rows.spec.ts` | US-18 – US-27 |
| Linking (both modes) & link editing | `06-linking.spec.ts` | US-28 – US-37 |
| Reorder, resize, navigation, map, export | `07-journeys.spec.ts` | US-38 – US-44 |
| No-error invariant | fixture in `helpers.ts` | US-45 |

---

## 1 — Persistence & startup

**US-01 — Opens with the default layout.**
Given a fresh browser · When I open the page · Then I see the Metrics and Objectives panes, the mode toggle on "Performance-metrics", one metric row, one objective row, and the live JSON state block.
_Inventory: PERSIST-1, MODE-1 · Status: ✅_

**US-02 — Changes survive a refresh.**
Given I have changed something · When I refresh · Then the change is still there.
_Inventory: PERSIST-3 · Status: ✅_

**US-03 — Reset returns to defaults and clears saved data.**
Given the page in any state · When I click Reset and confirm · Then it returns to two portfolios / one metric / one objective and all saved data is cleared, and stays cleared after a reload.
_Inventory: PF-4 · Status: ✅ (fixed a real autosave/reload race found here)_

## 2 — Organisation name & theme

**US-04 — Rename the organisation, and it persists.**
Given the page is open · When I click the org name, type a new one, press Enter · Then the heading and tab title update and survive a refresh.
_Inventory: TOP-1 · Status: ✅_

**US-05 — An empty name falls back to "My Organisation".**
_Inventory: TOP-1 · Status: ✅_

**US-06 — Escape cancels a rename.**
_Inventory: TOP-1 · Status: ✅_

**US-07 — Theme toggle switches dark/light and persists.**
_Inventory: TOP-2 · Status: ✅_

## 3 — Modes & portfolio selection

**US-08 — Switch to Objectives mode.**
Then both panes show objectives, the left total badge appears, the two dropdowns hold different portfolios.
_Inventory: MODE-2 · Status: ✅_

**US-09 — Switch back to Performance mode.**
Then the left pane shows metrics, the left total badge hides, both dropdowns hold the same portfolio.
_Inventory: MODE-1 · Status: ✅_

**US-10 — Changing portfolio in Performance mode moves both sides.**
_Inventory: MODE-3 · Status: ✅_

**US-11 — The two portfolios stay distinct in Objectives mode.**
Neither dropdown offers the portfolio the other side is on; `leftPortfolio ≠ rightPortfolio` always.
_Inventory: MODE-3 · Status: ✅_

**US-12 — Add a portfolio via the dropdown "+ Add portfolio".**
_Inventory: PF-1, MODE-4 · Status: ✅_

**US-13 — Reject a duplicate portfolio name (case-insensitive) with a message.**
_Inventory: PF-1 · Status: ✅_

## 4 — Portfolio management

**US-14 — Rename a portfolio from its pane title; its metrics, objectives and links move with it.**
Given the page is open · When I double-click the pane's portfolio name, type a new one, press Enter · Then the portfolio and all its data are renamed.
_Inventory: PF-2 · Status: ▢_

**US-15 — Reject a conflicting portfolio rename with a conflict dialog; the rename does not apply.**
_Inventory: PF-2 · Status: ▢_

**US-16 — View & Edit: rename one portfolio and delete another in one Save.**
Then the rename is applied with its data and the deleted portfolio and its data are removed.
_Inventory: PF-3 · Status: ▢_

**US-17 — Always keep at least two portfolios.**
When a Save would leave fewer than two · Then a message shows and nothing is deleted.
_Inventory: PF-3 · Status: ▢_

## 5 — Metric & objective rows

**US-18 — Add a metric; it gets a unique name and survives a refresh.**
_Inventory: PANE-3 · Status: ▢_

**US-19 — Add an objective; it starts at 0% weight.**
_Inventory: PANE-3 · Status: ▢_

**US-20 — Rename a row; the new name is saved.**
_Inventory: ROW-1 · Status: ▢_

**US-21 — Reject a duplicate row name with a message; the label reverts.**
_Inventory: ROW-1 · Status: ▢_

**US-22 — Deactivate a row: it greys out, can't be edited/dragged, slider disabled, links to it hidden (not deleted).**
_Inventory: ROW-2 · Status: ▢_

**US-23 — Delete a metric: the row and all its links disappear from page and state.**
_Inventory: ROW-3 · Status: ▢_

**US-24 — Delete an objective: the row and all its links disappear from page and state.**
_Inventory: ROW-3 · Status: ▢_

**US-25 — Move an objective's weight slider: the % label and the pane total badge update.**
_Inventory: ROW-4, PANE-1 · Status: ▢_

**US-26 — Weights never exceed 100%: an increase past a pane total of 100 is capped.**
_Inventory: PANE-1 · Status: ▢_

**US-27 — Total badge is green at exactly 100%, red otherwise.**
_Inventory: PANE-1 · Status: ▢_

## 6 — Linking & link editing

**US-28 — Hovering an eligible metric shows a "+" on it and on every objective it can still link to.**
_Inventory: LINK-1 · Status: ▢_

**US-29 — Clicking an objective's "+" creates a link at 5% strength, drawn and recorded.**
_Inventory: LINK-2 · Status: ▢_

**US-30 — A metric whose links already total 100% shows no "+" and cannot be linked further.**
_Inventory: LINK-1, LINK-2 · Status: ▢_

**US-31 — A warning "!" shows near the left pane heading when a linked metric's strengths ≠ 100%.**
_Inventory: PANE-2 · Status: ▢_

**US-32 — In Objectives mode, hover a left objective and click a right objective's "+" to link the two portfolios' objectives.**
_Inventory: LINK-1, LINK-2 · Status: ▢_

**US-33 — Reverse-relationship guard: linking A→B while B→A exists prompts to confirm; confirm removes B→A and creates A→B, cancel changes nothing.**
_Inventory: LINK-3 · Status: ▢_

**US-34 — Clicking a link opens a strength control; moving it updates the link's strength and redraws it. ⚠ [the ~850ms "sticky" then cursor-follow behaviour of the popover is not asserted].**
_Inventory: LR-2 · Status: ▢_

**US-35 — Moving a link's strength to 0% prompts to confirm deletion.**
_Inventory: LR-2, LR-3 · Status: ▢_

**US-36 — Ctrl+click a link and confirm: the link is removed and the line disappears.**
_Inventory: LR-3 · Status: ▢_

**US-37 — A link's line is not drawn while one of its rows is scrolled out of its pane, and returns when the row is visible again.**
_Inventory: LR-1 · Status: ▢_

## 7 — Reorder, resize, navigation, map, export

**US-38 — Drag a row by its handle to reorder it; the order persists. ⚠ [driven with synthetic drag events, not a raw mouse drag].**
_Inventory: DND-1 · Status: ▢_

**US-39 — Drag the left-pane divider: the pane resizes within its limits and the link lines fade during the drag.**
_Inventory: DND-2 · Status: ▢_

**US-40 — Navigate to the Performance page: click the arrow, pick a portfolio, confirm; the choice is saved and the browser navigates to `Performance page 2_V3.html`.**
_Inventory: NAV-1 · Status: ▢_

**US-41 — Cancel navigation at the picker or confirmation: nothing is saved, stay on the page.**
_Inventory: NAV-1 · Status: ▢_

**US-42 — Open the relationship map: it shows the "Organization Relationship Map" modal with an SVG graph and a legend table listing every portfolio. ⚠ [the "Root Organisation" node exists in the layout but v11's active renderer does not draw it or list it, so it is not asserted].**
_Inventory: MAP-1 · Status: ▢_

**US-43 — With no objective-to-objective links, the map says there is nothing to display.**
_Inventory: MAP-1 · Status: ▢_

**US-44 — Export the workbook: an `.xlsx` downloads named after the organisation, containing the sheets OBJ-OBJ Align, Metric-OBJ Align, ReadMe, Portfolio Mind Map, OBJ Weight.**
_Inventory: XLS-1 · Status: ▢_

## 8 — Cross-cutting invariant

**US-45 — No script errors during normal use.**
After every test in the suite: the hidden `#err` bar never became visible, and the `#jsonView` state block is still valid JSON.
_Inventory: global error handler · Status: ⚙ (auto-fixture in `helpers.ts`, applied to every test)_

---

## Out of scope (documented, not tested)

- The exact pixel position / cursor-follow of the strength popover (US-34) — behaviour, not correctness.
- The 3-second autosave heartbeat's timing — persistence is covered by explicit flush + reload instead.
- Real prose grammar in UI copy — covered separately by the responsiveness/layout/English audit sweep, not by these ACs.
- Visual appearance / styling — no pixel snapshots in this set; the audit sweep flags layout breakage only.
