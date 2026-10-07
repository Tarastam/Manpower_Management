# Workforce Overview – Workshop Filter Redesign Brief

## Objective

Redesign the **Workshop filter** on the Workforce Overview dashboard so that hierarchical production/workshop data is easier to navigate and does not create an excessively long dropdown.

This document is intended to be used directly as an implementation brief for an IDE / coding agent.

---

## Current UI

Dashboard header:

- Page: **Workforce overview**
- Month filter
- Workshop filter
- Apply filters button
- KPI cards such as Active Headcount, Absence Rate, Turnover Rate

The current Workshop dropdown stores/displays hierarchy as a full text path.

Examples:

```text
Total Production
Total Production / Total Assembly Line
Total Production / Total Assembly Line / FPS
Total Production / Total Assembly Line / FPS / FPSA
Total Production / Total Assembly Line / FPS / FPSB
Total Production / Total Assembly Line / GPSP2
Total Production / Total Assembly Line / PSL
Total Production / Total Assembly Line / PSL / PSLA
Total Production / Total Assembly Line / PSL / PSLB
Total Production / Total Chemical Line
Total Production / Total Chemical Line / Anodization
...
```

---

## Problems With Current Filter

### 1. Too many options

Every possible hierarchical path becomes a separate dropdown option.

As the organization/workshop structure grows, the dropdown becomes increasingly difficult to use.

### 2. Repeated text

Parent names such as:

```text
Total Production
Total Assembly Line
```

are repeated in many options.

This adds visual noise without helping the user identify the target workshop.

### 3. Full paths are difficult to scan

Example:

```text
Total Production / Total Assembly Line / FPS / FPSA
```

Users normally care about the final workshop, such as **FPSA**, rather than repeatedly reading the entire hierarchy.

### 4. Slash-separated hierarchy is visually heavy

Do not use `/` as the main navigation mechanism for the hierarchy.

---

# Proposed Solution

Replace the existing flat dropdown with a:

## Searchable Hierarchical Drill-down Selector

Keep **one Workshop filter field** on the dashboard, but let the user navigate the hierarchy one level at a time.

Concept:

```text
WORKSHOP
┌───────────────────────────────┐
│ Search workshop...            │
├───────────────────────────────┤
│ Total Production           ›  │
│ Other Production           ›  │
│ Utility & Support          ›  │
└───────────────────────────────┘
```

Selecting `Total Production` opens the next level:

```text
WORKSHOP
┌───────────────────────────────┐
│ ‹ Total Production            │
├───────────────────────────────┤
│ Total Assembly Line        ›  │
│ Total Chemical Line        ›  │
│ Others                     ›  │
└───────────────────────────────┘
```

Selecting `Total Assembly Line`:

```text
┌───────────────────────────────┐
│ ‹ Total Assembly Line         │
├───────────────────────────────┤
│ FPS                        ›  │
│ GPSP2                      ›  │
│ PSL                        ›  │
└───────────────────────────────┘
```

Selecting `FPS`:

```text
┌───────────────────────────────┐
│ ‹ FPS                         │
├───────────────────────────────┤
│ FPSA                          │
│ FPSB                          │
└───────────────────────────────┘
```

---

# Example Hierarchy

Use a tree-like data structure rather than storing the display value as a concatenated path.

```text
Total Production
├── Total Assembly Line
│   ├── FPS
│   │   ├── FPSA
│   │   └── FPSB
│   ├── GPSP2
│   └── PSL
│       ├── PSLA
│       └── PSLB
│
└── Total Chemical Line
    ├── Anodization
    └── ...
```

Suggested data model:

```ts
type WorkshopNode = {
  id: string;
  name: string;
  parentId?: string;
  children?: WorkshopNode[];
};
```

Do not rely on parsing strings such as:

```text
Total Production / Total Assembly Line / FPS / FPSA
```

for UI navigation if the underlying data can be represented hierarchically.

---

# Interaction Requirements

## 1. Drill-down

Only show children of the current hierarchy level.

Do NOT display all descendants simultaneously.

Example:

```text
Total Production
    ↓
Total Assembly Line
    ↓
FPS
    ↓
FPSA
```

---

## 2. Back Navigation

Users must be able to return to the previous hierarchy level.

Example:

```text
‹ Total Assembly Line
```

The transition should happen inside the same dropdown/popover.

Do not open multiple dropdowns side-by-side.

---

## 3. Parent Levels Should Be Selectable

Users may need to view aggregated data.

Therefore these should all be valid filter selections:

```text
Total Production
Total Assembly Line
FPS
FPSA
```

Recommended interaction:

```text
FPS                         ›
```

Clicking the **row/name** can select FPS.

Clicking the **chevron** can drill into FPS children.

Alternatively, provide a clear action such as:

```text
View all FPS
```

inside the FPS level.

Choose whichever interaction is clearer with the existing frontend framework.

---

# Search Behavior

The search box should search across the entire hierarchy.

Example query:

```text
FPSA
```

Result:

```text
FPSA
Assembly Line › FPS
```

The primary label should emphasize the actual workshop.

The hierarchy should appear as secondary/context text.

Do NOT show:

```text
Total Production / Total Assembly Line / FPS / FPSA
```

as the primary label.

Search should ideally match:

- workshop name
- parent group
- assembly line
- aliases if available

---

# Selected State

After selecting FPSA, the main Workshop field should display:

```text
WORKSHOP
FPSA                                  ▾
```

Do NOT display:

```text
Total Production / Total Assembly Line / FPS / FPSA
```

inside the main filter.

If hierarchy context is useful, show it as subtle secondary text:

```text
FPSA
Assembly Line › FPS
```

or:

```text
Total Production › Assembly Line › FPS › FPSA
```

The breadcrumb is optional and should remain visually secondary.

---

# Applied Filter Chips

After filters are applied, optionally show compact chips:

```text
[ September 2026 × ]   [ FPSA × ]
```

This makes the current dashboard scope immediately visible.

Users should be able to remove a filter from its chip if that behavior fits the existing application.

---

# Recommended Dashboard Filter Bar

Target structure:

```text
┌─────────────────────┐
│ MONTH               │
│ September 2026      │
└─────────────────────┘

┌───────────────────────────────┐
│ WORKSHOP                      │
│ FPSA                       ▾  │
└───────────────────────────────┘

┌─────────────────────┐
│   Apply filters     │
└─────────────────────┘
```

Keep the existing general dashboard style.

---

# Visual Direction

Match the current Workforce Overview dashboard:

- clean modern enterprise dashboard
- white cards
- soft borders
- subtle shadows
- rounded corners
- purple/indigo primary accent
- dark navy text
- muted secondary text
- generous whitespace
- clear hover states
- selected item can use a subtle purple background
- chevron-right indicates additional hierarchy
- chevron-left indicates back navigation

Avoid making the filter look like a traditional native HTML `<select>`.

Prefer a custom:

- popover
- command menu
- combobox
- tree selector
- hierarchical picker

depending on the existing frontend stack.

---

# Suggested Component Structure

Possible architecture:

```text
WorkshopFilter
├── FilterTrigger
├── FilterPopover
│   ├── SearchInput
│   ├── Breadcrumb / BackButton
│   └── WorkshopList
│       └── WorkshopItem
└── SelectedValue
```

Possible state:

```ts
selectedWorkshop
currentNode
navigationStack
searchQuery
isOpen
```

Example:

```ts
selectedWorkshop = {
  id: "fpsa",
  name: "FPSA"
};

navigationStack = [
  "total-production",
  "total-assembly-line",
  "fps"
];
```

---

# UX Behavior Example

Initial state:

```text
Workshop
All workshops
```

Open filter:

```text
Search workshop...

Total Production        ›
Other Production        ›
Utility & Support       ›
```

Navigate:

```text
Total Production
    ↓
Total Assembly Line
    ↓
FPS
    ↓
FPSA
```

Final filter:

```text
Workshop
FPSA
```

Dashboard scope:

```text
September 2026 • FPSA
```

---

# Important Requirements

1. Remove the current flat list of full hierarchical paths.
2. Do not use `/` as the primary hierarchy separator.
3. Do not show every hierarchy combination in one dropdown.
4. Navigate hierarchy progressively.
5. Keep the Workshop filter as one compact dashboard control.
6. Support selecting parent levels for aggregated views.
7. Add search across the hierarchy.
8. Search results should prioritize the workshop name.
9. Show parent hierarchy only as secondary context.
10. Preserve the existing dashboard visual language.
11. The component must remain usable with a large number of workshops.
12. Keyboard navigation should be supported where practical.
13. Clicking outside / pressing Escape should close the popover.
14. Current selection should be visually obvious when reopening the filter.

---

# Acceptance Criteria

The redesign is successful when:

- The user no longer sees dozens of long `/`-separated options.
- The user can reach FPSA using:

```text
Total Production
→ Total Assembly Line
→ FPS
→ FPSA
```

- FPSA appears simply as `FPSA` after selection.
- A user can search `FPSA` directly without manually navigating all parent levels.
- A parent such as `FPS` can also be selected to view aggregated FPS data.
- The filter works cleanly even when the hierarchy contains many additional production lines and workshops.
- The new component visually matches the existing Workforce Overview dashboard.

---

# Implementation Priority

## P0

- Replace flat dropdown
- Hierarchical drill-down
- Select workshop
- Select parent/group
- Back navigation
- Correct filtering of dashboard data

## P1

- Global hierarchy search
- Secondary hierarchy context
- Applied-filter chips
- Keyboard navigation

## P2

- Recent selections
- Favorite workshops
- Persist last selected workshop
- Optional animation between hierarchy levels

---

# Desired End Result

The Workshop filter should feel like navigating folders rather than browsing a giant list.

Core concept:

```text
ONE FILTER
   +
SEARCH
   +
DRILL DOWN
   +
SELECT ANY LEVEL
```

The UI should optimize for quickly answering:

> “Which workshop or production group do I want to see?”

rather than forcing the user to understand or read the entire hierarchy every time.
