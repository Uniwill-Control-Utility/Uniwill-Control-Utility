# AGENTS.md

## Never commit user-story / planning documents

User-story documents, backlogs and plan drafts are **local working notes**, never repository content (user rule, 2026-11-02). They belong in the agent plan directory outside the repo (`~/.opencode/plan/`) or in an ignored local folder (`artifacts/notes/`); `.gitignore` matches working-document names (`*user-stories*`, `*backlog*`, `*draft*`, `*-wip*`, `*plan-*`, `plan.md`) so an accidental `git add -A` cannot sweep one in. If a story needs to be shared, write a sanitised wiki page instead of committing the story file.

A `git add -A` in this repo is dangerous for the same reason: check `git status --short` before staging and exclude untracked strays explicitly.

## Localization sync — required before every commit

Any user-visible text **addition, change, or removal** (XAML `Text`/`Content`,
toasts, message boxes, tray labels — everything routed through
`LocalizationService.Get`/`Format`) must be reflected in the localization
files **in the same commit** that changes the string. Shipping a commit where
the code string and the catalogs disagree is a regression.

### How this app localizes

- The English string **is the lookup key**: `LocalizationService.Get(text)` /
  XAML text walked by `LocalizationService.RefreshElement`. There are no
  resource IDs.
- Shipped catalogs: `Uniwill Control Utility\Localization\*.json` (26
  cultures), embedded via `<EmbeddedResource Include="Localization\*.json" />`.
  The English files (`en-US`, `en-GB`) map keys to themselves.
- Translator work queue: `Localization\todo\*.json` — same key, value =
  English placeholder. When a key changes, re-key these too (self-mapped).
- `Localization\out\*.json` / `*.p2.json` / `*.p3.json` are the translation
  pipeline's historical batch output. **Never edit**; not embedded.

### Editing the JSON safely

- Files are UTF-8 **no BOM**, one key per line; preserve line shape and the
  trailing comma when replacing a key's line.
- Windows PowerShell 5.1 corrupts non-ASCII literals in scripts
  (codepage-dependent parsing). Put translations in a separate UTF-8 JSON
  map (write with a tool, not the shell) and run an **ASCII-only** patch
  script that reads it with `[IO.File]::ReadAllText`.
- Validate each patched catalog with **System.Text.Json**
  (`JsonSerializer.Deserialize<Dictionary<string,string>>`) — **not** PS 5.1
  `ConvertFrom-Json`, which rejects legal case-differing keys the catalogs
  already contain (e.g. `Auto-Connect on Startup` vs `Auto-connect on startup`).
  The app's serializer is the authoritative parse gate.

### Checklist per string change

1. Update the source string.
2. Re-key all 26 shipped catalogs (key = new English text; per-language
   translation; identity for en-US/en-GB).
3. Re-key the matching `Localization\todo\*.json` entries (self-mapped).
4. Verify the old key is gone from live + todo dirs (`out\` may keep it).
5. Parse-verify every catalog and print the resolved string per culture —
   CJK/Arabic/Hindi/Thai must come back uncorrupted.
6. Then, and only then, run the normal gates (clean Release build at the
   warning baseline, all five suites green) and commit.

## Project conventions live in the wiki

Release/field procedure, staging mechanics, the field-verified hardware
findings (PnP/CM/WMI, engine service), and the acceptance checklist:
`wiki\operations\operations-runbook.md`. Gates per change: clean
`dotnet build "Uniwill Control Utility.sln" -c Release` with 0 errors at the
known-warning baseline, and all five test suites green.
