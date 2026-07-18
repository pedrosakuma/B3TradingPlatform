---
name: B3TradingPlatform
description: A dense, calm participant-side trading console built for operational confidence.
colors:
  canvas: "#0f1218"
  surface: "#1a1f2a"
  surface-raised: "#232a39"
  border: "#2a3242"
  text: "#d6dce8"
  text-muted: "#8a93a6"
  accent: "#4f9eff"
  on-accent: "#0b1320"
  success: "#36c46f"
  info: "#38b2ac"
  danger: "#ef5350"
  warning: "#f5a623"
typography:
  title:
    fontFamily: "system-ui, -apple-system, Segoe UI, Roboto, sans-serif"
    fontSize: "1.3rem"
    fontWeight: 600
    lineHeight: 1.4
  body:
    fontFamily: "system-ui, -apple-system, Segoe UI, Roboto, sans-serif"
    fontSize: "13px"
    fontWeight: 400
    lineHeight: 1.4
  label:
    fontFamily: "system-ui, -apple-system, Segoe UI, Roboto, sans-serif"
    fontSize: "0.8rem"
    fontWeight: 600
    lineHeight: 1.2
rounded:
  sm: "3px"
  md: "4px"
  lg: "6px"
  xl: "8px"
  pill: "999px"
spacing:
  1: "0.125rem"
  2: "0.25rem"
  3: "0.375rem"
  4: "0.5rem"
  5: "0.75rem"
  6: "1rem"
  7: "1.5rem"
  8: "2rem"
components:
  button-primary:
    backgroundColor: "{colors.accent}"
    textColor: "{colors.on-accent}"
    rounded: "{rounded.md}"
    padding: "0.25rem 1rem"
    height: "2rem"
  button-secondary:
    backgroundColor: "{colors.surface-raised}"
    textColor: "{colors.text}"
    rounded: "{rounded.md}"
    padding: "0.25rem 1rem"
    height: "2rem"
  input:
    backgroundColor: "{colors.surface-raised}"
    textColor: "{colors.text}"
    rounded: "{rounded.md}"
    padding: "0.25rem 0.5rem"
    height: "2rem"
  panel:
    backgroundColor: "{colors.surface}"
    textColor: "{colors.text}"
    rounded: "{rounded.lg}"
    padding: "0.75rem"
---

# Design System: B3TradingPlatform

## 1. Overview

**Creative North Star: "The Quiet Trading Desk"**

The interface is dense, layered, and controlled: a professional workstation
where market state and risk information remain legible without visual noise.
Dark tonal layers separate tools and data while a restrained blue accent marks
the current action, focus, or selection.

The design should feel calm under pressure and familiar to experienced trading
users. It explicitly rejects forced SaaS tours, decorative celebration,
consumer-investing simplification, and tutorial surfaces disconnected from the
real workflow.

**Key Characteristics:**
- Compact, information-rich composition with clear operational hierarchy.
- Restrained accent use and explicit semantic state colors.
- Flat bordered surfaces separated by tone rather than decoration.
- Familiar controls, strong keyboard focus, and tabular numeric alignment.

## 2. Colors

The palette is a cool, low-glare workstation foundation with one clear action
blue and a complete semantic vocabulary.

### Primary
- **Action Blue** (`#4f9eff`): Primary actions, selected tabs, keyboard focus, and current context only.

### Secondary
- **Execution Green** (`#36c46f`): Confirmed success and positive execution state.
- **Market Teal** (`#38b2ac`): Informational market state that must remain distinct from action blue.
- **Risk Amber** (`#f5a623`): Warnings and conditions that require review before action.
- **Reject Red** (`#ef5350`): Errors, destructive actions, and failed or dangerous states.

### Neutral
- **Desk Canvas** (`#0f1218`): Full-viewport background and deepest layer.
- **Work Surface** (`#1a1f2a`): Primary panels, cards, and the application shell.
- **Raised Control** (`#232a39`): Inputs, selected neutral controls, and secondary surfaces.
- **Structure Line** (`#2a3242`): Borders and dividers.
- **Primary Ink** (`#d6dce8`): Default readable text.
- **Secondary Ink** (`#8a93a6`): Supporting labels and metadata, never critical instructions.

### Named Rules
**The One Action Blue Rule.** Use `#4f9eff` for actions, selection, and focus;
never as ambient decoration.

**The State Must Read Twice Rule.** Status meaning must be present in text or
iconography as well as color.

## 3. Typography

**Display Font:** system-ui (with Segoe UI and Roboto fallbacks)  
**Body Font:** system-ui (with Segoe UI and Roboto fallbacks)  
**Label/Mono Font:** inherit the UI stack; use native monospace only for identifiers.

**Character:** A single neutral sans family keeps controls familiar and dense.
Weight, case, spacing, and tabular numerals establish hierarchy without adding a
decorative type voice.

### Hierarchy
- **Title** (600, `1.3rem`, 1.4): Login and rare page-level titles.
- **Panel title** (600, `0.85rem`, 1.4): Compact uppercase panel labels with `0.06em` tracking.
- **Body** (400, `13px`, 1.4): Interface copy and table content.
- **Label** (600, `0.8rem`, 1.2): Buttons and actionable field labels.
- **Metadata** (400, `0.7-0.75rem`, 1.4): Badges and secondary operational detail.

### Named Rules
**The Data Holds Still Rule.** Use tabular numerals for prices, quantities,
balances, counts, and changing market values.

## 4. Elevation

The system is flat by default. Depth comes from the three dark surface tones and
one-pixel borders, not persistent shadows. Focus rings may sit above this flat
stack because they communicate interaction state.

### Named Rules
**The Tonal Layers Rule.** Separate canvas, panel, and control layers with
`#0f1218`, `#1a1f2a`, and `#232a39`; do not simulate hierarchy with decorative
drop shadows or glass effects.

## 5. Components

Components are compact, predictable, and state-complete.

### Buttons
- **Shape:** Tight rectangular controls with a `4px` radius.
- **Primary:** Action Blue background, Desk Canvas text, `2rem` minimum height.
- **Hover / Focus:** Slight brightness increase on hover; two-pixel Action Blue focus ring with `2px` offset.
- **Secondary / Outline:** Raised Control fill or transparent fill with a semantic border; destructive controls use Reject Red.

### Chips
- **Style:** Compact pills for status, square badges for categorical data.
- **State:** Semantic tint plus readable text; uppercase tracking is reserved for terse machine states.

### Cards / Containers
- **Corner Style:** `6px` panels and `8px` exceptional containers.
- **Background:** Work Surface over Desk Canvas.
- **Shadow Strategy:** None at rest.
- **Border:** One-pixel Structure Line.
- **Internal Padding:** Usually `0.75rem`, increasing to `1rem` only for low-density content.

### Inputs / Fields
- **Style:** Raised Control fill, Structure Line border, `4px` radius, compact padding.
- **Focus:** Two-pixel Action Blue outline with `2px` offset.
- **Error / Disabled:** Explicit supporting text; disabled controls reduce opacity but retain readable labels.

### Navigation
- **Style:** Familiar top-level tabs and compact bordered sub-tabs.
- **State:** Active items use Action Blue; inactive items remain quiet until hover or focus.
- **Mobile:** The primary tablist moves into a drawer while preserving labels and role visibility.

### Trading Ticket
- **Style:** A compact horizontal strip that keeps Symbol, Side, Type, Quantity,
  Price, and Submit visible. Advanced execution fields disclose in place.
- **Behavior:** Validation and risk warnings appear adjacent to the real form;
  the ticket never moves into a separate tutorial mode.

## 6. Do's and Don'ts

### Do:
- **Do** preserve the compact `13px` operational scale and established spacing tokens.
- **Do** use Action Blue only for the next action, focus, or current selection.
- **Do** make every onboarding action operate the real ticket and Working Orders surface.
- **Do** keep every first-run step keyboard operable and compatible with reduced motion.
- **Do** explain warnings at the point where they affect submission.

### Don't:
- **Don't** add forced SaaS tours, modal step carousels, decorative celebration, or repeated popups.
- **Don't** create a tutorial mode disconnected from the real trading workflow.
- **Don't** simplify the interface into a consumer investing app or hide meaningful risk.
- **Don't** use glassmorphism, gradient text, accent side stripes, or decorative shadows.
- **Don't** use muted `#8a93a6` for critical instructions or required action text.
