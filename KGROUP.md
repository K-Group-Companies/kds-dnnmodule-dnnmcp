# DNN MCP — K Group fork

This repo is K Group's fork of [sachatrauwaen/AIChat](https://github.com/sachatrauwaen/AIChat),
a DNN extension that adds an AI chat to the PersonaBar and exposes the portal as an
**MCP server** so Claude Code (and other MCP clients) can read and edit site content.

We keep the fork to ship our own MCP fixes and tools while still pulling the original
author's improvements. Everything upstream documents in [README.md](README.md) still
applies. This file covers only what is different here and how we use it.

- **Part 1** — what the fork changes and how to maintain it
- **Part 2** — using the MCP server from Claude Code: setup, tools, pitfalls

The companion skill, `dnn-content-management`, lives in
[kds-claude-config](https://github.com/K-Group-Companies/kds-claude-config/tree/main/skills/dnn-content-management).
It teaches Claude the content workflows (OpenContent modules, settings, images, site
rebuilds). This file covers the server; the skill covers how to drive it.

---

# Part 1 — The fork

## What we changed, and why

All changes are on `master`. The fix commits were also offered upstream: PR
[#5](https://github.com/sachatrauwaen/AIChat/pull/5) merged;
[#9](https://github.com/sachatrauwaen/AIChat/pull/9) and
[#10](https://github.com/sachatrauwaen/AIChat/pull/10) are still open. We don't plan to
send further changes upstream.

### Fixes to the MCP server

| Commit | Change | Why |
|---|---|---|
| `e715600` | Correct controller name/namespace in the route registration | The MCP endpoint returned 404. Merged upstream (#5). |
| `7b6711d` | Establish a DNN user context for API-key requests | API-key calls ran as anonymous, so every write tool failed with `InsufficientPermissions`. Requests now run as the user who generated the API key (see [API keys](#api-keys-and-identity)). |
| `fa899a4` | Make the transport JSON-RPC conformant | Unhandled exceptions escaped as HTML 500s and notifications returned a bare `null`. Strict clients, Claude Code among them, treat both as a dead transport and drop the session. Errors now come back as JSON-RPC errors inside a 200, and notifications return 202. |
| `1f12d56` | Correct `tools/list` output | Tool titles were dropped. Parameters typed `integer`/`number`/`boolean` silently degraded to `string`. Min/max were only applied to two type spellings. |
| `da00781` | `IMcpProviderOverride` marker interface | Tool registration is last-write-wins in an order a satellite assembly can't control, so a separate DLL couldn't replace a built-in tool. Providers implementing the marker register last. Nothing in this repo needs it any more (see below); it remains as an extension point. |
| `a477be5` | Match category casing on the new tools | MCP Settings groups tools by the raw category string, so `files` and `Files` showed up as two sections. |

### Tools we added or replaced (`48f7456`)

These used to live in a separate, hand-copied `KGroup.DnnMcp.Bridge.dll`. They now ship
inside the normal install packages, so a site needs nothing extra.

**In `AIChat` (core package)**

| Tool | New/replaced | What it does | Why we needed it |
|---|---|---|---|
| `write-file` | Replaced | Writes a portal file through DNN's `FileManager`. Accepts `utf8` or `base64` content and can overwrite. Blocks `.exe .dll .config .asax .ashx .aspx .cs`. | The upstream version was text-only and refused to overwrite, so images couldn't be uploaded at all. Writing through `FileManager` keeps DNN's Files/Folders tables consistent. |
| `delete-file` | New | Deletes the physical file and its DNN Files record. | There was no way to clean up. |
| `set-module-settings` | New | Writes module settings. On OpenContent modules, keys declared in `template-schema.json` are merged into the `data` JSON blob that OpenContent actually reads, type-coerced and enum-validated. Unknown keys are rejected. `raw: true` writes straight to `ModuleSettings`. | OpenContent ignores individual module settings, so a naive write "succeeds" and changes nothing. |
| `update-module` | New | Sets the module title and the **Display Container?** flag. | Duplicate headings (container title plus template heading) could only be fixed in the UI. |
| `move-module` | New | Changes pane and/or order. | Fixing placement used to mean delete and recreate, which orphans the module's image folder. |
| `delete-module` | New | Soft-deletes to the Recycle Bin by default; `permanent: true` bypasses it. | Cleanup after probes and mistakes. |

All module tools go through `ModuleController`, so DNN's cache is cleared. Direct SQL
writes need an app-pool recycle before they show up.

**In `OpenContentMcp` (OpenContentAI package)**

| Tool | New/replaced | What it does | Why we needed it |
|---|---|---|---|
| `update-opencontent-items` | Replaced | Sets module content, and creates the data record if the module has none. | A new module was stuck: update said `No item found to update` and add said `Not a multi items template`. The only way through was pressing Save in the editor. |
| `init-opencontent-module` | Replaced | Assigns a template to an existing module and seeds data. Detects `.hbs` vs `.cshtml` and never overwrites existing content. | It always crashed with `Value cannot be null. Parameter name: s` (no template ships a `data.json`), and it hard-coded `template.hbs`. |
| `add-opencontent-module` | Replaced | Creates the module, assigns the template and seeds data. Validates the template first and rolls the module back on failure. | It shared the init bug above and left an orphan module behind on every failure. |

Tool and parameter names are unchanged from upstream, so existing prompts and the enabled-tools
setting keep working.

## Repo layout (the parts that matter)

| Path | What |
|---|---|
| `AIChat/` | Core module: PersonaBar UI (`ClientSideVite/`), MCP server (`Mcp/`), built-in tools (`Tools/`). |
| `AIChat/Tools/ToolKit.cs` | Shared helpers for our tools: parameter builders, `Guard` (exception → tool error), arg parsing. Use it for new tools. |
| `OpenContentMcp/` | OpenContent tools — separate package, requires OpenContent on the site. |
| `SeoTools/` | SeoAI package (an alternative `get-url-seo`; core AIChat also ships one). |
| `AIChat/Tools/TOOLS.md` | Upstream's tool reference. **Partly stale for this fork:** it documents `delete-page` (the class exists but isn't registered) and the old `write-file`. Trust Part 2 below. |

## Building

**Clean builds don't work out of the box.** This is pre-existing upstream and also why the
GitHub Actions build fails. Two problems:

1. `AIChat.csproj` references `..\..\DNN910\bin\Dnn.PersonaBar.Extensions.dll`, a path
   *outside* the repo, and the code needs a **9.x** copy (it uses
   `Dnn.PersonaBar.Pages`). Any DNN 9 site's `bin` folder has one.
2. `BuildScripts\MSBuild.Community.Tasks.Targets` looks for the tasks DLL at a path that
   doesn't exist, so packaging fails after a successful compile. The DLL is at
   `packages\MSBuildTasks.1.4.0.88\tools\` after a NuGet restore.

Working command (put the DNN 9 DLL in a folder of its own and point `ReferencePath` at it.
`ReferencePath` is searched before `HintPath`):

```powershell
nuget restore AIChat.sln
msbuild AIChat.sln /t:Rebuild /p:Configuration=Release `
  /p:ReferencePath=C:\path\to\folder-with-dnn9-PersonaBar.Extensions `
  /p:MSBuildCommunityTasksLib=$PWD\packages\MSBuildTasks.1.4.0.88\tools\MSBuild.Community.Tasks.dll
```

Output: `Install\AIChat_*_Install.zip`, `OpenContentAI_*_Install.zip`,
`SeoAI_*_Install.zip`. `Install\` is gitignored. Install them on the site as normal
extensions (Settings → Extensions → Install Extension), **AIChat first**.

The `Debug` configuration also copies the DLLs into a local site via
`ModuleDeploy.targets`. Its `WebsitePath` is upstream's machine path, so build `Release`
unless you override it.

There is a unit test project, but it contains no tests. The build is the only automated
check.

**Version numbers** still match upstream (AIChat `01.02.01`). Bump them (the
`dnn-version-bump` skill does this) when you ship a build, so you can tell which build a
site is running.

## Pulling upstream changes

```bash
git fetch upstream
git merge upstream/master
```

Expect conflicts mainly in `AIChat/Tools/ToolsExtensions.cs`,
`OpenContentMcp/Tools/ToolsExtensions.cs` and the `.csproj` files, where our tools are
registered. Keep both sides. If upstream fixes one of the bugs our replaced tools fixed,
consider dropping ours in favour of theirs.

## Adding a tool

1. Add a class implementing `IMcpProvider` in `AIChat/Tools/` (or `OpenContentMcp/Tools/`
   for OpenContent), following `ModuleTools.cs`: `ToolKit.Param` for parameters,
   `ToolKit.Guard` around the handler.
2. Use a category that matches an existing one exactly (`Pages`, `Modules`, `Files`,
   `Open Content`, …). Casing matters.
3. Register it in that project's `ToolsExtensions.cs` and add the file to the `.csproj`
   (old-style project — files aren't picked up automatically).
4. Build, install, then **enable it in MCP Settings**. New tools are off by default (see
   pitfalls).
5. Add it to the tables in this file.

---

# Part 2 — Using the MCP server from Claude Code

## Setup

1. **Install the fork's packages on the site** (see [Building](#building)). Sites running
   upstream AIChat are missing every fix and tool in Part 1.
2. **Generate a key:** log in as a SuperUser → PersonaBar → **MCP** → generate an API key,
   choose its validity, and **tick the tools to expose** (see the list below) → Save. The
   page shows the server URL.
3. **Add it to Claude Code** (one-time per site). Name it `dnn-<site>`: the skill keys on
   the `dnn-` prefix.

   ```bash
   claude mcp add --transport http --scope user dnn-<site> https://<site-host>/API/Dnn/McpWebApi/Mcp --header "Authorization: Bearer <api-key>"
   ```

   Examples:

   | Site | Name | URL |
   |---|---|---|
   | Your local site | `dnn-<yoursite>-local` | `https://<yoursite>.dnndev.me/API/Dnn/McpWebApi/Mcp` |
   | Wahlfield dev | `dnn-wahlfield-dev` | `https://dev.wahlfielddrilling.com/API/Dnn/McpWebApi/Mcp` |

   `--scope user` makes it available from any directory and keeps the key in
   `~/.claude.json`. **Don't** add it to a repo's `.mcp.json`: that file is committed, and
   the key would go with it.
4. **Verify:** start `claude`, run `/mcp`, and confirm the server is connected and lists
   tools. Then ask Claude to run `list-opencontent-templates`.
5. **Install the skill** from kds-claude-config (instructions in that repo's README) so
   Claude follows the content workflow and verification rules.

Claude Desktop needs `mcp-remote` instead. The MCP Settings page in DNN shows that config.

## API keys and identity

- **One key per portal.** Generating a new key replaces the old one for everyone using
  that site. Coordinate before regenerating on a shared site like dev.
- **Requests run as whoever generated the key** (stored in the `DnnMcp_ApiKeyCreatedBy`
  portal setting). Every MCP edit is attributed to that user, with that user's
  permissions. If that user is deleted, writes start failing (a warning is logged).
- Keys can expire (the validity setting). An expired key looks like a connection failure
  in `/mcp`.

## Tools

Tools only appear if they are **ticked in MCP Settings on that site**. Categories match
the MCP Settings page. ★ = added or replaced by this fork.

**Pages**

| Tool | Does |
|---|---|
| `get-pages` | List all pages: id, name, title, URL, parent. |
| `get-page` | Full page record: permissions, modules, SEO fields, theme. |
| `add-page` | Create a page (`pageName`, `pageTitle`, `description`, `parentId` (`-1` = root), `visible`). |
| `update-page` | Change name, title, description, parent. **Not** `visible`, and no other SEO fields. |

There is no `delete-page`. Pages made through MCP must be deleted in the UI.

**Modules**

| Tool | Does |
|---|---|
| `get-modules` | Modules on a page. The only call that reports `PaneName`. |
| `add-module` | Add a module by definition name. |
| `update-module` ★ | Title and container on/off. |
| `move-module` ★ | Pane and order. |
| `delete-module` ★ | Recycle Bin by default. |
| `set-module-settings` ★ | Template settings (schema-validated) or `raw` module settings. |
| `get-html` / `update-html` | Read/write an HTML module's content. |

**Files**

| Tool | Does |
|---|---|
| `get-folders` / `get-files` | Browse the portal file system (gives `FileId`s). |
| `read-file` | Read a portal file. |
| `write-file` ★ | Write text or base64 (images), overwrite allowed. |
| `delete-file` ★ | Delete file and its record. |
| `get-system-files` / `read-system-file` / `write-system-file` | Files outside the portal folder (skins, etc.). **`write-system-file` can break the site — leave it off unless needed.** |

**Open Content**

| Tool | Does |
|---|---|
| `list-opencontent-templates` | Templates available on the site. Good first probe. |
| `get-opencontent-template` / `save-opencontent-template` | Read/write a template's markup, schema, options, CSS, JS. |
| `get-opencontent-items` | Module content, including merged `Settings`. |
| `add-opencontent-item` | Add one item to a **multi-item** template. |
| `update-opencontent-items` ★ | Replace module content (creates the record if missing). |
| `add-opencontent-module` ★ | Create + template + seed, rolled back on failure. |
| `init-opencontent-module` ★ | Template + seed for an existing module. |

**Other**

| Tool | Does |
|---|---|
| `get-url-html` | Fetch any URL's HTML. |
| `get-url-seo` | SEO audit of any URL: meta, headings, OG, alt text, links. |
| `send-email` | Sends real email from the site. **Leave off** unless you need it. |

## Pitfalls

These are the ones that cost real time. The skill's
[`references/mcp-tool-behavior.md`](https://github.com/K-Group-Companies/kds-claude-config/blob/main/skills/dnn-content-management/references/mcp-tool-behavior.md)
has the full list with worked examples.

1. **A success message is not proof.** Write tools can report success for writes that
   had no effect. Check the rendered page (`curl` the canonical URL and grep for what
   should have changed) or read the data back. Requesting a URL with a query string can
   301 to a tiny body that greps clean and looks like success.
2. **New tools are invisible until ticked.** The MCP Settings tool list is an allowlist.
   After installing a new build, tick the new tools, save, then reconnect in Claude Code
   (`/mcp`, or restart `claude`). Claude reads the tool list at connect time.
3. **`update-opencontent-items` replaces the whole blob.** Read first, edit the full
   object, send it all back. Omitted fields are deleted.
4. **Partial template settings fail silently.** Templates wrap CSS in
   `{{#if Settings.X}}`, so a missing field drops a style with no error. Copy the full
   settings from a module that already renders correctly. Razor templates are worse: unset
   settings crash the **whole page** (`Cannot convert null to 'bool'`). Set settings in the
   same session you create the module.
5. **Images go in the module's own folder:** `OpenContent/Files/<moduleId>/`. Pointing at
   another module's folder renders fine but makes the content uneditable in the UI.
6. **Don't delete and recreate modules to move them.** The module ID is part of image
   paths. Use `move-module`.
7. **Sites run different builds.** A site on upstream AIChat (or an older fork build)
   lacks the ★ tools and the user-context fix, so writes fail with `InsufficientPermissions`.
   On a new site, probe first: `list-opencontent-templates` → `get-pages` → `get-modules`,
   and test `add-opencontent-module` on a throwaway page if you'll need it.
8. **MCP-created pages inherit the site's indexing default.** On dev that's usually
   noindex, and MCP can't change it. Track which pages you create so they get flipped at
   launch.
9. **The dev site is shared.** Edits are live for everyone looking at it, and module
   deletes land in the Recycle Bin, not a sandbox. Do experiments on your local site.

## Tips

- Ask Claude to "read the template's `template-schema.json` first" before any settings
  change. Guessed field names are the main cause of rejections.
- "Find a module already using this template and copy its settings" is the fastest
  route to a correctly styled module.
- For a multi-page job, have Claude write an inventory/mapping table first and approve it
  before it creates anything (the skill's `legacy-site-rebuild` reference covers this).
