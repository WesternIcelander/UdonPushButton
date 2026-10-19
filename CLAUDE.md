# CLAUDE.md

## Git workflow

- Commit after each step is complete (e.g. each step of `PLAN.md`), unless told otherwise.
- Push after each commit, unless told otherwise.

## Plan notes

- When a step of `PLAN.md` is done, write its notes (what was done, how it was checked) to `plan-done/<step>-<short-summary>.md` (e.g. `plan-done/8.4-make-enabling-and-disabling-safe.md`), starting with a `# <step> <title>` heading.
- In `PLAN.md`, tick the step and add only `Done: [notes](plan-done/<file>.md).` under it. Keep the notes out of `PLAN.md`.
- Read a step's notes file only when the current work needs it.
