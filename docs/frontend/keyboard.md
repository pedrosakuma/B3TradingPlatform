# Keyboard shortcuts

Global shortcuts wired in `frontend/js/keyboard.js` and bound from
`frontend/js/app.js` (Fase 5 / #401).

## Tabs

| Shortcut          | Action                                    |
| ----------------- | ----------------------------------------- |
| `Alt`+`1`         | Switch to Trader tab                      |
| `Alt`+`2`         | Switch to Algos tab                       |
| `Alt`+`3`         | Switch to History tab                     |
| `Alt`+`4`         | Switch to Settings tab                    |
| `Alt`+`5`         | Switch to Admin tab                       |
| `Alt`+`6`         | Switch to Compliance tab                  |

## Trader sub-navigation

| Shortcut                 | Action                                    |
| ------------------------ | ----------------------------------------- |
| `Alt`+`Shift`+`1`        | Trader → Markets sub-tab                  |
| `Alt`+`Shift`+`2`        | Trader → Watchlist sub-tab                |
| `Alt`+`Shift`+`3`        | Trader → Auctions sub-tab                 |
| `Alt`+`B`                | Lower band → Working orders               |
| `Alt`+`E`                | Lower band → Executions                   |

## Ticket / focus

| Shortcut | Action                                                          |
| -------- | --------------------------------------------------------------- |
| `/`      | Focus the ticket symbol selector                                |
| `B`      | Set Quick-ticket side to **Buy**  (outside of text fields)      |
| `S`      | Set Quick-ticket side to **Sell** (outside of text fields)      |

## Global

| Shortcut | Action                                |
| -------- | ------------------------------------- |
| `Esc`    | Close the currently open modal/drawer |

## Rules

* **Alt-combos** fire from anywhere, including while a text input has
  focus — they don't collide with standard text-editing shortcuts.
* **Plain letters** (`B`, `S`, `/`) are suppressed while a text field
  is focused (`<input type="text">`, `<textarea>`, `contenteditable`,
  `<select>`), so the trader can type a symbol or quantity without
  triggering shortcuts.
* **`Esc`** is always active.
* The browser's default action is `preventDefault()`-ed only when a
  shortcut matches — `/` does **not** open Firefox's quick-find when
  the trader is logged in, but `Ctrl`/`Cmd` combinations are otherwise
  untouched.

## Extending

All shortcut bindings live in `SHORTCUTS` in `frontend/js/keyboard.js`.
Handlers are wired in `bindKeyboardShortcuts({...})` near the top of
`frontend/js/app.js`. Add an entry to both tables, update this
document, and add a row to the Preferences sub-tab listing.
