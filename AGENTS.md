<!-- BEGIN public-repo (synced from the Projects hub agents/ — do not edit here; edits are reverted on the next sync) -->

# Agent standards — public repository

Rules that apply to **every** agent working in this repository, codex and Claude alike. This is
the managed `public-repo` block; its source of truth is `agents/scopes/public-repo.md` in the
maintainers' internal hub, and editing it here is reverted on the next sync. A repo's own
`AGENTS.md` (outside these markers) may add to it.

## This repository is public

Everything committed here is public and permanent: the code, the commit messages, the pull
request titles and bodies, the issues, the wiki, and the edit history of all of them. A
correction does not retract what was published — an edited pull-request body remains in GitHub's
edit history, and a rewritten commit remains in every fork, clone and API response.

Some contributors also work in a **separate private environment** where this software is
deployed. Nothing from that environment belongs here. Before writing a commit message, a pull
request, an issue or a code comment, check the remote (`git remote get-url origin`) and, if it is
`github.com`, treat everything below as binding.

**Never publish, in any file, message, comment or fixture:**

- Machine, server or workstation names, and any internal hostname or DNS domain
- Internal URLs, file shares, deployment paths, and the accounts used to reach them
- Names of staff, students, colleagues or vendor contacts — including first names alone
- References to an internal issue tracker, its identifiers, or links into it
- Asset tags, serial numbers, user names, or anything drawn from an inventory system
- Certificate subjects, tenant identifiers, organization names, and licence keys
- Which software titles are deployed, where, or in what quantity
- Internal discussions as a source — "as agreed in the meeting", "as discussed in chat"

**Write for a reader with no access to any of that.** This is a quality rule as much as a
disclosure one: a bug report that describes the *failure mode* is more useful than one that
describes the site it was noticed on. "A workstation re-hashing a ~9 GB cached package on every
run" tells a stranger what is wrong; a hostname tells them nothing they can act on. Where a real
value is genuinely needed for the software to run, put it in configuration or an environment
variable with a neutral default — never hardcode it.

If you find published material that breaks these rules, do not quietly delete it: report it to
the maintainers, because assessing the exposure matters more than tidying the file.

## Issues and tracking

**Work in this repository is tracked by GitHub issues in this repository.** Reference them as
`#<n>`, and close them from a pull request body with `Closes #<n>`.

Do not reference an internal tracker in a commit, pull request, issue or release note. Such
identifiers resolve nowhere for a public reader, communicate nothing, and disclose the shape of a
private backlog. If work needs tracking and no issue exists, open one (`gh issue create`) stating
the problem in public terms.

## Working agreement

1. **Start in a worktree off `origin/main`** — never branch off local `main`, which is routinely
   stale:

       git fetch origin main
       git worktree add .worktrees/<name> origin/main

   Worktrees live at `./.worktrees/<name>` inside the repo, excluded by `.gitignore`. Name
   branches `<type>/<slug>` — type being one of `feature`, `fix`, `chore`, `ci`, `docs`.

2. **Work and verify** in that worktree. Build and run the test suite before shipping, and say
   plainly in the pull request what you ran and what the result was, including pre-existing
   failures you did not cause.

3. **Ship**: commit with a descriptive, imperative subject, push the task branch, then open a
   pull request or append to the existing one for that branch — check first, never duplicate.

4. **Never push `main`/`master` directly.** The pull-request merge is what lands work.

5. **Clean up on merge**, without being asked — remove the merged branch, its stale tracking ref
   and its worktree, and say what was removed.

Commit subjects stay plain prose — no emoji, no `Co-Authored-By` trailers, no bracketed tags such
as `[hotfix]`, and no assistant session links or `Claude-Session:` trailers. Keep commits focused;
never commit test or debug artifacts, secrets, `*.env*`, `*.pem`, or credential files.

<!-- END public-repo -->
