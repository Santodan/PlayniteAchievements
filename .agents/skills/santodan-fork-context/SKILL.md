---
name: santodan-fork-context
description: Orient work in the Santodan PlayniteAchievements fork, identify fork-owned features and code, and apply its owner rules before investigating, reviewing, or changing this repository.
---

# Santodan fork context

Apply only to the Santodan PlayniteAchievements repository, normally at
`E:/Programs/Playnite/CustomExtension/PlayniteAchievements-main`. Locate the actual
checkout before using paths. All project paths below are relative to its root.
These are portable Markdown instructions; another agent can read them directly
even if its client does not support automatic skill discovery.

## Owner rules

- Respond in English unless the user explicitly requests another language.
- Never commit or push unless the user asks. Existing unstaged/staged work belongs
  to the user; preserve it, including changes already inside protected files.
- Fork product changes belong under **Santodan-Fork**. Do not change the behavior
  or UI of other settings tabs. Shared runtime files may contain necessary fork
  routing hooks; keep the change limited to the fork path and preserve upstream
  behavior outside it. Do not treat a shared filename as permission to redesign
  upstream features.
- Do not modify `tools/ForkMaintenance` unless the current user message asks for
  maintenance work. Ordinary code fixes do not authorize a bundle refresh.
- Do not edit `source/extension.yaml` or `InstallerManifest.yaml`. The owner's
  spelling variants refer to these actual files. Exception: when explicitly
  updating from upstream, set `source/extension.yaml` to the selected upstream
  version with `.1` appended, e.g. `4.2.1.1`. This exception does not authorize an
  installer manifest change.
- Build from `source/` with the owner's command:
  `msbuild PlayniteAchievements.csproj /p:Configuration=Release /t:Rebuild`.

Follow current user instructions and any applicable AGENTS.md over this snapshot.
Do not treat skill loading as permission to deploy, install a package, update
upstream, refresh a bundle, stage unrelated files, commit, or push.

## Establish the current state

Read `git status --short`, relevant diffs, and the applicable local instructions.
Check for a pending merge before operations that depend on a clean checkout.
Use `git diff --name-only` and focused `rg` searches to distinguish live source
from generated overlays and old validation snapshots.

Read [the source map](references/source-map.md) to find the right implementation,
documentation, and regression coverage. Read only the relevant feature sections
of `tools/ForkMaintenance/FORK_FEATURE_CHECKLIST.md`; it is the behavioral
inventory, not proof that every live check has passed.

Versions, overlay counts, test counts, defaults, and completion claims in old
maintenance notes are historical. Inspect current source, configuration, and
`tools/ForkMaintenance/bundle/bundle.json` for current values. The bundle manifest
records a replay baseline, not necessarily the latest upstream release.

## Route the work

- Feature changes, fixes, and validation: use `santodan-fork-development` if
  available, or read `.agents/skills/santodan-fork-development/SKILL.md`.
- Review/export/test/replay of the fork bundle: use
  `santodan-fork-maintenance`, when requested in the current message.
- An explicitly requested upstream integration: use
  `santodan-fork-upstream-update`.

Each task skill includes the essential boundaries so it can also be used alone.
