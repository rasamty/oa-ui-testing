# Objective Alignment — user spec (US-01 … US-45)

Plain-language acceptance criteria for the whole page. Each story becomes at least one
Playwright test, tagged `@US-xx`. "Maps to" points at the matching entry in the
functionality inventory (the "Objective Alignment Test Spec" artifact).

App under test: `app/Objective Alignment v11.html` (served at `/app/...`).

---

## A.1 Getting started & persistence

### US-01 — Open the page
- **Given** a browser and the page's address
- **When** I open the page
- **Then** I see two panes (Metrics on the left, Objectives on the right), the mode toggle set to "Performance-metrics", one metric row, one objective row, and a JSON block at the bottom showing the current state.
- _Maps to: PERSIST-1, MODE-1_

### US-02 — My work is remembered
- **Given** I have added or changed something
- **When** I refresh the page
- **Then** everything I changed is still there.
- _Maps to: PERSIST-3_

### US-03 — Reset everything
- **Given** the page in any state
- **When** I click "Reset" and confirm the dialog
- **Then** the page returns to its defaults (two portfolios, one metric, one objective) and all saved data is cleared.
- _Maps to: PF-4_

## A.2 Organisation name & theme

### US-04 — Rename the organisation
- **Given** the page is open
- **When** I click the organisation name, type a new name, and press Enter
- **Then** the heading and the browser tab title show the new name, and it is still shown after I refresh.
- _Maps to: TOP-1_

### US-05 — Empty name falls back to a default
- **Given** I am editing the organisation name
- **When** I clear it and press Enter
- **Then** it shows "My Organisation".
- _Maps to: TOP-1_

### US-06 — Cancel a rename
- **Given** I have started editing the organisation name
- **When** I press Escape
- **Then** the previous name is restored unchanged.
- _Maps to: TOP-1_

### US-07 — Switch theme
- **Given** the page is in dark theme
- **When** I click the theme toggle
- **Then** the page switches to light theme, the icon changes, and the choice persists after a refresh.
- _Maps to: TOP-2_

## A.3 Modes & portfolio selection

### US-08 — Switch to Objectives mode
- **Given** the page is in Performance-metrics mode
- **When** I click "Objectives"
- **Then** both panes show objectives, the left-hand total badge appears, and the two portfolio dropdowns show two different portfolios.
- _Maps to: MODE-2_

### US-09 — Switch back to Performance mode
- **Given** the page is in Objectives mode
- **When** I click "Performance-metrics"
- **Then** the left pane shows metrics, the left total badge is hidden, and both dropdowns show the same portfolio.
- _Maps to: MODE-1_

### US-10 — Change portfolio in Performance mode
- **Given** the page is in Performance mode
- **When** I pick a different portfolio in either dropdown
- **Then** both dropdowns change to it and both panes show that portfolio's metrics and objectives.
- _Maps to: MODE-3_

### US-11 — Portfolios stay distinct in Objectives mode
- **Given** the page is in Objectives mode
- **When** I pick, for one side, the portfolio already selected on the other side
- **Then** the other side automatically moves to a different portfolio so the two are never the same.
- _Maps to: MODE-3_

### US-12 — Add a portfolio
- **Given** either portfolio dropdown is open
- **When** I choose "+ Add portfolio", type a name, and click Save
- **Then** the portfolio is added to the list and selected for that side.
- _Maps to: PF-1, MODE-4_

### US-13 — Reject a duplicate portfolio name
- **Given** the Create Portfolio dialog is open
- **When** I type a name that already exists (any capitalisation) and click Save
- **Then** I see a "names must be unique" message and no portfolio is added.
- _Maps to: PF-1_

## A.4 Managing portfolios

### US-14 — Rename a portfolio from its pane title
- **Given** the page is open
- **When** I double-click the portfolio name in a pane heading, type a new name, and press Enter
- **Then** the portfolio is renamed everywhere, and its metrics, objectives and links move with it.
- _Maps to: PF-2_

### US-15 — Reject a conflicting portfolio rename
- **Given** I am renaming a portfolio via its pane title
- **When** I enter a name another portfolio already uses
- **Then** a conflict dialog appears and the rename is not applied.
- _Maps to: PF-2_

### US-16 — View & Edit portfolios
- **Given** the page is open
- **When** I open "View & Edit", rename one portfolio, mark another for deletion, and click Save
- **Then** the rename is applied (with its data) and the marked portfolio and its data are removed.
- _Maps to: PF-3_

### US-17 — Always keep at least two portfolios
- **Given** the View & Edit dialog is open
- **When** I try to save with fewer than two portfolios remaining
- **Then** I see a message and nothing is deleted.
- _Maps to: PF-3_

## A.5 Metric & objective rows

### US-18 — Add a metric
- **Given** the page is in Performance mode
- **When** I click the "+" under the Metrics pane
- **Then** a new metric row appears with an automatically unique name, and it is still there after a refresh.
- _Maps to: PANE-3_

### US-19 — Add an objective
- **Given** the page is open
- **When** I click the "+" under the Objectives pane
- **Then** a new objective row appears at 0% weight.
- _Maps to: PANE-3_

### US-20 — Rename a row
- **Given** a metric or objective row is active
- **When** I double-click its label, type a new name, and click away
- **Then** the new name is saved.
- _Maps to: ROW-1_

### US-21 — Reject a duplicate row name
- **Given** two rows exist in the same pane
- **When** I rename one to match the other
- **Then** I see a "must be unique" message and the label reverts.
- _Maps to: ROW-1_

### US-22 — Deactivate a row
- **Given** a row is active
- **When** I click its tick box
- **Then** the row greys out, cannot be edited or dragged, its slider is disabled, and any links touching it are hidden (not deleted).
- _Maps to: ROW-2_

### US-23 — Delete a metric
- **Given** a metric has one or more links
- **When** I click its cross
- **Then** the metric row and all of its links disappear from the page and the state.
- _Maps to: ROW-3_

### US-24 — Delete an objective
- **Given** an objective has one or more links
- **When** I click its cross
- **Then** the objective row and all of its links disappear from the page and the state.
- _Maps to: ROW-3_

### US-25 — Set an objective's weight
- **Given** an objective row is active
- **When** I move its weight slider
- **Then** its percentage label updates and the pane's total badge recalculates.
- _Maps to: ROW-4, PANE-1_

### US-26 — Weights never exceed 100%
- **Given** the objectives in a pane already total 100%
- **When** I try to increase one objective's weight
- **Then** the slider is capped so the pane total stays at 100%.
- _Maps to: PANE-1_

### US-27 — Total badge colour
- **Given** objectives have weights
- **When** the pane total is exactly 100%
- **Then** the total badge is green; at any other value it is red.
- _Maps to: PANE-1_

## A.6 Linking — Performance mode

### US-28 — See link targets on hover
- **Given** a metric and some objectives are active and the metric has spare capacity
- **When** I hover the metric row
- **Then** a "+" appears on the metric and on every objective it could still be linked to.
- _Maps to: LINK-1_

### US-29 — Create a metric–objective link
- **Given** a metric is hovered and an objective shows a "+"
- **When** I click the objective's "+"
- **Then** a line is drawn between them starting at 5% strength, and the link is recorded in the state.
- _Maps to: LINK-2_

### US-30 — No link when the metric is full
- **Given** a metric's links already total 100%
- **When** I hover the metric
- **Then** no "+" appears and no new link can be made.
- _Maps to: LINK-1, LINK-2_

### US-31 — Balance warning flag
- **Given** at least one linked metric's strengths do not add up to 100%
- **When** I look at the left pane heading
- **Then** a warning "!" is shown, with a tooltip naming how many metrics are off.
- _Maps to: PANE-2_

## A.7 Linking — Objectives mode

### US-32 — Create an objective–objective link
- **Given** the page is in Objectives mode with two different portfolios
- **When** I hover a left objective and click the "+" on a right objective
- **Then** a link is created between the two portfolios' objectives and recorded under that portfolio pair.
- _Maps to: LINK-1, LINK-2_

### US-33 — Reverse-relationship guard
- **Given** portfolio B already has links pointing to portfolio A
- **When** I try to link A to B
- **Then** I am asked to confirm reversing; on confirm the old B→A links are removed and the new A→B link is created; on cancel nothing changes.
- _Maps to: LINK-3_

## A.8 Editing & deleting links

### US-34 — Change a link's strength
- **Given** a link exists
- **When** I click its line
- **Then** a strength control appears; moving it updates the link's strength and redraws the line in a new colour.
- _Maps to: LR-2_

### US-35 — Strength zero deletes the link
- **Given** the strength control is open for a link
- **When** I move it to 0%
- **Then** I am asked to confirm deleting the link.
- _Maps to: LR-2, LR-3_

### US-36 — Delete a link
- **Given** a link exists
- **When** I Ctrl+click its line and confirm the dialog
- **Then** the link is removed and the line disappears.
- _Maps to: LR-3_

### US-37 — Links hide when an endpoint scrolls away
- **Given** a link's two rows are far apart
- **When** I scroll one row out of its pane
- **Then** the line is not drawn; it reappears when the row is visible again.
- _Maps to: LR-1_

## A.9 Reordering & resizing

### US-38 — Reorder rows by dragging
- **Given** a pane has two or more active rows
- **When** I drag a row by its handle above or below another
- **Then** the order changes and is still in the new order after a refresh.
- _Maps to: DND-1_

### US-39 — Resize the left pane
- **Given** the page is open
- **When** I drag the divider on the right edge of the left pane
- **Then** the left pane gets wider or narrower within its limits, and the link lines fade while I am dragging.
- _Maps to: DND-2_

## A.10 Navigation

### US-40 — Go to the Performance page
- **Given** the page is open
- **When** I click the arrow, pick a portfolio, review the confirmation screen, and click Confirm
- **Then** the chosen portfolio is saved and the browser navigates to the Performance page.
- _Maps to: NAV-1_

### US-41 — Cancel navigation
- **Given** the portfolio picker or confirmation screen is open
- **When** I click Cancel or click outside it
- **Then** nothing is saved and I stay on the page.
- _Maps to: NAV-1_

## A.11 Organisation map

### US-42 — View the relationship map
- **Given** portfolios are linked to each other
- **When** I click the eye icon
- **Then** a map opens showing each portfolio as a node, a "Root Organisation" node at the top connected to the parentless portfolios, and a legend table.
- _Maps to: MAP-1_

### US-43 — Empty map message
- **Given** no objective links exist between portfolios
- **When** I open the map
- **Then** it says there is nothing to display.
- _Maps to: MAP-1_

## A.12 Excel export

### US-44 — Export the workbook
- **Given** the map is open
- **When** I click the green Excel icon
- **Then** an .xlsx file downloads, named after my organisation, containing the sheets: OBJ-OBJ Align, Metric-OBJ Align, ReadMe, Portfolio Mind Map, and OBJ Weight.
- _Maps to: XLS-1_

## A.13 Robustness

### US-45 — No script errors during normal use
- **Given** the page is open
- **When** I perform any of the actions above
- **Then** the hidden error bar never appears and the JSON state block at the bottom always stays valid JSON.
- _Maps to: global error handler_
