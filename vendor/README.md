# Vendored STS2MCP source

Everything in `sts2mcp/` is a **verbatim copy** of source files from
[STS2MCP](https://github.com/Lucien2714/STS2MCP), compiled straight into
`STS2_Recorder.dll`.

## Why copies rather than a reference

The recorder's core promise is that a recorded `state` object is *identical* to
what STS2MCP's `GET /api/v1/singleplayer` returns — same fields, same shapes,
same edge-case handling. The only way to guarantee that without ongoing effort
is to run the same code, so `BuildGameState()` is compiled from STS2MCP's own
source instead of being reimplemented or approximated here.

A mod DLL cannot reference another mod's DLL at load time, so the reuse has to
happen at compile time: either a submodule or a vendored copy. This repo uses a
vendored copy, with a recorded upstream commit standing in for the pin a
submodule would give.

## Do not edit these files

A local edit silently breaks the identical-output guarantee — the recorder keeps
working, and its output quietly stops matching STS2MCP's. Every vendored file
carries a do-not-edit header, and `scripts/check-drift.ps1` flags edits.

**Fix bugs upstream in STS2MCP, then re-sync.**

## Refreshing

```powershell
.\scripts\sync-upstream.ps1 -Source E:\path\to\STS2MCP
```

This re-copies every file in `scripts/vendor-manifest.txt` and rewrites
`UPSTREAM.json` with the upstream repo, branch, commit, and a per-file SHA-256.
It refuses to run when a file it would copy has uncommitted upstream changes,
because a recording stamped with a commit that does not contain the copied code
is worse than no stamp at all. `-AllowDirty` overrides, recording the commit as
`<sha>-dirty`.

## Checking for staleness

```powershell
.\scripts\check-drift.ps1 -Source E:\path\to\STS2MCP        # warn
.\scripts\check-drift.ps1 -Source E:\path\to\STS2MCP -Strict  # exit 1, for CI
```

The build runs this automatically as a warning; it never fails a build, so a
missing or moved STS2MCP checkout can't block you.

It distinguishes three cases:

| Result   | Meaning                                                        | Action |
|----------|----------------------------------------------------------------|--------|
| `edited` | A vendored copy was modified locally. Output no longer matches. | Re-sync; move the fix upstream. |
| `stale`  | Upstream changed a file we vendor.                              | Re-sync when you want the change. |
| `behind` | Upstream moved, but no vendored file changed.                   | Nothing. |

## Provenance

`UPSTREAM.json` records which commit produced these copies. The build reads it
(`STS2_Recorder.csproj` → `StateBuilderCommit`) and stamps it onto the assembly,
and every recording carries it as `state_builder_commit`. That field is what
makes a dataset collected across a schema change interpretable after the fact.

## Which files, and why so many

`BuildGameState()` is one method on a `partial class` whose pieces call freely
across each other, so the compilable unit is nearly the whole class rather than
one file. `scripts/vendor-manifest.txt` documents, per file, exactly why it is
in the list — and which two files are deliberately excluded.
