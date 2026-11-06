---
name: prepare-github-release
description: Prepare a GitHub release for UCU — bump the version, draft the user-facing changelog, and run the packaging script. Trigger when the user says "Prepare a Github Release" (or close variants: "prepare a release", "cut a release", "make a release build").
---

# Prepare a GitHub Release (UCU)

Follow wiki/operations/release-process.md as the source of truth; this skill
operationalizes its checklist steps 1–4. Standing repo rules apply throughout:
no Co-Authored-By trailer, commit message = short title + structured wrapped
body, never push, localization "<TODO>" sweep before any release commit.

## 0. Confirm first — ALWAYS

Before touching anything, ask the user (AskUserQuestion) to confirm: show the
current version, the proposed next version, and the three actions about to
happen (bump+commit, changelog draft, pack.ps1 build). The trigger phrase may
have been said by accident; proceed only on an explicit yes.

## 1. Version check, working tree, bump

- Already-bumped check FIRST: `git log --oneline --grep="<current version>" -1`
  — if that bump commit is HEAD (no commits after the latest version-related
  commit), the user already incremented. Do NOT bump again; use the existing
  version and treat the previous bump commit as the changelog base.
- Working-tree check: `git status` — if there are uncommitted changes,
  summarize them briefly and ask the user whether to include them in this
  release (commit them before the bump) or ship without them. Never silently
  bundle the user's unstaged work.
- Version rule: 3-part `a.b.c`, one increment = +0.0.1; each digit rolls over
  at 10 (0.9.9 → 0.10.0, 0.10.9 → 0.11.0, 9.9.9 → 10.0.0). Velopack rejects
  4-part versions.
- Bump `<Version>` in the single source of truth:
  - `Directory.Build.props` (repository root)
    It is the only version field: pack.ps1 reads it through the app project,
    and the SDK derives the assembly/file/product version of every project
    (app, UCU.Client/Contracts/Core/Hardware, the engine, tests) from it. The
    per-project `<Version>` fields, and the retired `UCU.Guardian` project,
    are gone - touching one file is the whole bump.
- If any localization catalogs contain `"<TODO>"` placeholders, replace them
  with real translations for all 21+ languages first (standing rule), and
  include that in the release work.
- Commit the bump (plus the translation sweep / included work if any) with a
  message like `Bump version to 0.9.6` + body. Do not push.
- Precondition: the user has given the green light that the working tree is
  tested (no-untested-changes rule).

## 2. Draft the changelog

- Find the changelog base: the bump commit of the version BEFORE the release
  version — `git log --oneline --grep=<version-before-release> -1`
  (e.g. base for a 0.9.6 release is the "Bump version to 0.9.5" commit;
  works identically when the user bumped 0.9.6 themselves).
- List everything since: `git log <base>..HEAD --oneline`.
- Write a short-and-sweet, user-facing changelog: group as New / Improved /
  Fixed, translate commit-speak into user outcomes (no file paths, no internal
  names), keep it under ~15 bullets.
- Add a "Migration" section when the release changes on-disk state (settings
  upgrade, install layout moves, scheduled-task paths) — see the release
  checklist. Omit the section when nothing moved.
- Present the draft to the user; they paste it into the GitHub release body.

## 3. Build the release assets

- `dotnet tool restore`
- `powershell -NoProfile -File build/pack.ps1` (machine policy — never
  `-ExecutionPolicy Bypass`, it is denied).
- The script publishes, stages native payload, runs `dotnet vpk pack`
  (per-machine MSI + full/delta nupkgs + releases.win.json) and zips the
  portable artifact into `Releases\`.
- Report the printed upload manifest to the user.
- The script asserts the staged engine payload is fresh (step 2c': the engine
  dll/exe in `artifacts\publish\Service\` must not be older than the app exe,
  and its file version must equal the release version). If it throws, the
  staging target skipped its copy: fix that, never ship the payload.
- After packing, hand the user wiki/operations/release-process.md section 5
  (Release validation): the MSI pass, the portable pass and the in-app update
  flow are the release-scenario gate, and the debug F5 layout covers none of
  them.

## 4. Hand off

Never push and never create the GitHub release yourself. Tell the user: tag
`v<version>`, upload the manifest assets, paste the changelog. Expect the
first pack on a fresh Releases\ to have no delta (built once a previous full
nupkg is on GitHub).
