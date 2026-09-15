using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dnn.Mcp.WebApi;
using Dnn.Mcp.WebApi.Models;
using Dnn.Mcp.WebApi.Services;
using DotNetNuke.Entities.Modules;
using DotNetNuke.Entities.Portals;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Satrabel.AIChat.Tools;
using Satrabel.OpenContent.Components;
using Satrabel.OpenContent.Components.Datasource;

namespace Satrabel.OpenContentMcp.Tools
{
    /// <summary>
    /// Creates OpenContent modules and populates them:
    /// <c>update-opencontent-items</c>, <c>init-opencontent-module</c> and
    /// <c>add-opencontent-module</c>.
    /// </summary>
    /// <remarks>
    /// Three separate defects, all reachable from one ordinary task (create an OpenContent
    /// module and give it content):
    ///
    /// <c>update-opencontent-items</c> deadlocked on a module with no data record — it
    /// returned <c>Error: No item found to update</c> because <c>IDataSource.Get</c> yields
    /// null, while <c>add-opencontent-item</c> refused with <c>Not a multi items template</c>.
    /// The only way through was to open the editor and press Save. Fixed by calling
    /// <c>IDataSource.Add</c> on that path instead of failing.
    ///
    /// <c>init-opencontent-module</c> passed the template's <c>data.json</c> contents
    /// straight to <c>JObject.Parse</c>. No template here ships one, so it always threw
    /// <c>ArgumentNullException</c> — surfacing as
    /// <c>Value cannot be null. Parameter name: s</c> for every template, Handlebars or
    /// Razor. It also hardcoded <c>template.hbs</c>, so a Razor-only template got a
    /// <c>template</c> setting pointing at a file that does not exist.
    ///
    /// <c>add-opencontent-module</c> called that same init logic internally — not through
    /// the tool registry — so replacing the tool alone did not reach it. It also created the
    /// module before initialising, leaving an orphan behind on every failure.
    ///
    /// All three supersede earlier implementations that lived in
    /// <see cref="ManageTemplatesTool"/> and <c>UpdateOpenContentTool</c>. The tool names and
    /// parameter names are unchanged, so callers and the <c>DnnMcp_Tools</c> portal setting
    /// need no edit.
    ///
    /// These depend on the caller having a DNN identity: data contexts are built from
    /// <c>PortalSettings.Current.UserInfo.UserID</c>, which is anonymous unless
    /// <c>McpController</c> has established the user context.
    /// </remarks>
    public class OpenContentModuleTools : IMcpProvider
    {
        private const string OpenContentModuleName = "OpenContent";
        private const string DefaultPaneName = "ContentPane";

        /// <inheritdoc />
        public void Register(IMcpRegistry registry)
        {
            registry.RegisterTool(new ToolDefinition
            {
                Name = "update-opencontent-items",
                Title = "Update OpenContent items",
                Description =
                    "Set the content of an OpenContent module. In list mode the supplied JSON "
                    + "array replaces the whole collection. In single-item mode the JSON object "
                    + "is written to the module's item, creating it if the module has no data "
                    + "record yet — so a newly created module can be populated without opening "
                    + "the editor.",
                Category = "Open Content",
                ReadOnly = false,
                Parameters = new List<ToolParameter>
                {
                    ToolKit.Param("tabId", "int", "The ID of the page containing the module.", true),
                    ToolKit.Param("moduleId", "int", "The ID of the OpenContent module.", true),
                    ToolKit.Param(
                        "json",
                        "string",
                        "The JSON data to write: an array in list mode, an object in single-item mode.",
                        true),
                },
                Handler = ToolKit.Guard("update-opencontent-items", UpdateItems),
            });

            registry.RegisterTool(new ToolDefinition
            {
                Name = "init-opencontent-module",
                Title = "Init OpenContent module",
                Description =
                    "Assign a template to an existing OpenContent module and give it an initial "
                    + "data record so it is immediately usable. Detects Handlebars "
                    + "(template.hbs) or Razor (template.cshtml), and seeds from the template's "
                    + "data.json when present. Never overwrites content the module already has.",
                Category = "Open Content",
                ReadOnly = false,
                Parameters = new List<ToolParameter>
                {
                    ToolKit.Param("tabId", "int", "The ID of the page containing the module.", true),
                    ToolKit.Param("moduleId", "int", "The ID of the OpenContent module.", true),
                    ToolKit.Param(
                        "templateName",
                        "string",
                        "Template folder name, e.g. 'Featured Employees'. "
                        + "Use list-opencontent-templates to see the options.",
                        true),
                },
                Handler = ToolKit.Guard("init-opencontent-module", InitModule),
            });

            registry.RegisterTool(new ToolDefinition
            {
                Name = "add-opencontent-module",
                Title = "Add OpenContent module",
                Description =
                    "Create an OpenContent module on a page, assign a template and give it an "
                    + "initial data record — ready to populate with update-opencontent-items. "
                    + "The template is validated before the module is created, and the module is "
                    + "removed again if initialisation fails, so a failure leaves nothing behind.",
                Category = "Open Content",
                ReadOnly = false,
                Parameters = new List<ToolParameter>
                {
                    ToolKit.Param("tabId", "int", "The ID of the page to add the module to.", true),
                    ToolKit.Param(
                        "templateName",
                        "string",
                        "Template folder name, e.g. 'Featured Employees'. "
                        + "Use list-opencontent-templates to see the options.",
                        true),
                    ToolKit.Param(
                        "paneName",
                        "string",
                        "Pane to place the module in. Defaults to " + DefaultPaneName
                        + ". Use get-modules to see the panes a page uses.",
                        false),
                    ToolKit.Param("title", "string", "Module title.", false),
                },
                Handler = ToolKit.Guard("add-opencontent-module", AddOpenContentModule),
            });
        }

        private static CallToolResult AddOpenContentModule(Dictionary<string, object> args)
        {
            if (!ToolKit.TryInt(args, "tabId", out var tabId))
            {
                return ToolKit.Fail("tabId is required and must be an integer.");
            }

            var templateName = ToolKit.Str(args, "templateName");
            if (templateName == null)
            {
                return ToolKit.Fail("templateName is required.");
            }

            var paneName = ToolKit.Str(args, "paneName") ?? DefaultPaneName;
            var title = ToolKit.Str(args, "title") ?? string.Empty;

            // Resolve the template first. The module's version creates the module and only
            // then initialises, so a bad template name — or the null data.json — left an
            // orphan module on the page every time it failed.
            var setup = TemplateSetup.Resolve(templateName, out var setupError);
            if (setup == null)
            {
                return ToolKit.Fail(setupError);
            }

            var module = AddModuleTool.AddModule(tabId, OpenContentModuleName, paneName, title);
            if (module == null)
            {
                return ToolKit.Fail(
                    "Could not create an OpenContent module on page " + tabId
                    + ". Confirm the page exists and the OpenContent module is installed.");
            }

            try
            {
                var note = ApplyTemplate(module, tabId, setup, out var templatePath, out var listMode);

                return ToolKit.Ok(
                    "Created OpenContent module " + module.ModuleID + " on page " + tabId
                    + " in pane '" + paneName + "', template '" + templateName + "' as "
                    + templatePath + " (" + (listMode ? "list" : "single-item") + " mode). " + note);
            }
            catch (Exception ex)
            {
                var rollback = TryRemove(tabId, module.ModuleID)
                    ? "The module was removed again."
                    : "WARNING: module " + module.ModuleID
                      + " could not be removed and is still on the page.";

                return ToolKit.Fail(
                    "Created module " + module.ModuleID + " but applying template '"
                    + templateName + "' failed: " + ex.Message + " " + rollback);
            }
        }

        private static CallToolResult InitModule(Dictionary<string, object> args)
        {
            if (!ToolKit.TryInt(args, "tabId", out var tabId))
            {
                return ToolKit.Fail("tabId is required and must be an integer.");
            }

            if (!ToolKit.TryInt(args, "moduleId", out var moduleId))
            {
                return ToolKit.Fail("moduleId is required and must be an integer.");
            }

            var templateName = ToolKit.Str(args, "templateName");
            if (templateName == null)
            {
                return ToolKit.Fail("templateName is required.");
            }

            var module = ResolveOpenContentModule(moduleId, tabId, out var moduleError);
            if (module == null)
            {
                return ToolKit.Fail(moduleError);
            }

            var setup = TemplateSetup.Resolve(templateName, out var setupError);
            if (setup == null)
            {
                return ToolKit.Fail(setupError);
            }

            var note = ApplyTemplate(module, tabId, setup, out var templatePath, out var listMode);

            return ToolKit.Ok(
                "Template '" + templateName + "' assigned to module " + moduleId + " as "
                + templatePath + " (" + (listMode ? "list" : "single-item") + " mode). " + note);
        }

        private static CallToolResult UpdateItems(Dictionary<string, object> args)
        {
            if (!ToolKit.TryInt(args, "tabId", out var tabId))
            {
                return ToolKit.Fail("tabId is required and must be an integer.");
            }

            if (!ToolKit.TryInt(args, "moduleId", out var moduleId))
            {
                return ToolKit.Fail("moduleId is required and must be an integer.");
            }

            var raw = ToolKit.Str(args, "json");
            if (raw == null)
            {
                return ToolKit.Fail("json is required.");
            }

            var module = ResolveOpenContentModule(moduleId, tabId, out var moduleError);
            if (module == null)
            {
                return ToolKit.Fail(moduleError);
            }

            var config = OpenContentModuleConfig.Create(module, PortalSettings.Current);
            var manifest = config.Settings?.Manifest;

            if (manifest == null)
            {
                return ToolKit.Fail(
                    "Module " + moduleId + " has no template assigned, so its data source is "
                    + "unknown. Assign one first with init-opencontent-module.");
            }

            var dataSource = DataSourceManager.GetDataSource(manifest.DataSource);
            if (dataSource == null)
            {
                return ToolKit.Fail("Unknown OpenContent data source '" + manifest.DataSource + "'.");
            }

            var userId = PortalSettings.Current.UserInfo.UserID;

            return config.IsListMode()
                ? ReplaceList(dataSource, config, userId, raw)
                : UpsertSingle(dataSource, config, userId, raw, moduleId);
        }

        /// <summary>
        /// Resolves a module and confirms it is an OpenContent module.
        /// </summary>
        private static ModuleInfo ResolveOpenContentModule(int moduleId, int tabId, out string error)
        {
            var module = ModuleController.Instance.GetModule(moduleId, tabId, true);

            if (module == null)
            {
                error = "Module " + moduleId + " was not found on page " + tabId
                        + ". Use get-modules to list modules on the page.";

                return null;
            }

            if (module.DesktopModule == null
                || module.DesktopModule.ModuleName != OpenContentModuleName)
            {
                error = "Module " + moduleId + " is a '"
                        + (module.DesktopModule == null ? "unknown" : module.DesktopModule.ModuleName)
                        + "' module, not OpenContent.";

                return null;
            }

            error = null;

            return module;
        }

        /// <summary>
        /// Assigns the template setting and creates an initial data record if the module has
        /// none. Shared by init-opencontent-module and add-opencontent-module.
        /// </summary>
        private static string ApplyTemplate(
            ModuleInfo module,
            int tabId,
            TemplateSetup setup,
            out string templatePath,
            out bool listMode)
        {
            templatePath = FileUri.FromPath(Path.Combine(setup.Directory, setup.MainFile)).FilePath;

            ModuleController.Instance.UpdateModuleSetting(module.ModuleID, "template", templatePath);
            module.ModuleSettings["template"] = templatePath;
            ModuleController.Instance.ClearCache(tabId);

            var config = OpenContentModuleConfig.Create(module, PortalSettings.Current);
            var manifest = config.Settings?.Manifest;

            if (manifest == null)
            {
                listMode = false;

                return "Template assigned, but the module reports no manifest, so no data record "
                       + "was created.";
            }

            var dataSource = DataSourceManager.GetDataSource(manifest.DataSource);
            var userId = PortalSettings.Current.UserInfo.UserID;
            listMode = config.IsListMode();

            return SeedData(dataSource, config, userId, listMode, setup);
        }

        /// <summary>
        /// Creates an initial data record when the module has none.
        /// </summary>
        /// <remarks>
        /// Deliberately different from the module's version, which calls
        /// <c>IDataSource.Update</c> with the seed when a record already exists — that would
        /// overwrite real content, and with an empty seed it would wipe it. Existing content
        /// is left alone here.
        ///
        /// In list mode an empty seed is skipped rather than added, to avoid leaving a blank
        /// item behind; single-item mode does create the empty record, because that is what
        /// makes the module editable without opening the editor first.
        /// </remarks>
        private static string SeedData(
            IDataSource dataSource,
            OpenContentModuleConfig config,
            int userId,
            bool listMode,
            TemplateSetup setup)
        {
            if (listMode)
            {
                var context = OpenContentUtils.CreateDataContext(config, userId, false, null);
                var existing = dataSource.GetAll(context, null);

                if (existing?.Items != null && existing.Items.Any())
                {
                    return "Existing list content left untouched.";
                }

                if (!setup.HasSeedFile)
                {
                    return "No data.json to seed from; list left empty.";
                }

                dataSource.Add(context, setup.Seed);

                return "Seeded one item from data.json.";
            }

            var singleContext = OpenContentUtils.CreateDataContext(config, userId, true, null);

            if (dataSource.Get(singleContext, null) != null)
            {
                return "Existing content left untouched.";
            }

            dataSource.Add(singleContext, setup.Seed);

            return setup.HasSeedFile
                ? "Created the content item from data.json."
                : "Created an empty content item (template ships no data.json).";
        }

        /// <summary>
        /// List mode: the supplied array replaces the entire collection, matching the
        /// module's own behaviour.
        /// </summary>
        private static CallToolResult ReplaceList(
            IDataSource dataSource, OpenContentModuleConfig config, int userId, string raw)
        {
            JArray items;
            try
            {
                items = JArray.Parse(raw);
            }
            catch (JsonException ex)
            {
                return ToolKit.Fail(
                    "This is a list-mode module, so json must be a JSON array. Parse error: "
                    + ex.Message);
            }

            var context = OpenContentUtils.CreateDataContext(config, userId, false, null);
            var existing = dataSource.GetAll(context, null);
            var removed = 0;

            if (existing?.Items != null)
            {
                foreach (var item in existing.Items.ToList())
                {
                    dataSource.Delete(context, item);
                    removed++;
                }
            }

            foreach (var item in items)
            {
                dataSource.Add(context, item);
            }

            return ToolKit.Ok(
                "Replaced list content: removed " + removed + " existing item(s), added "
                + items.Count + ".");
        }

        /// <summary>
        /// Single-item mode: update the existing item, or create it when the module has no
        /// data record yet. Creating is the behaviour the module lacks.
        /// </summary>
        private static CallToolResult UpsertSingle(
            IDataSource dataSource,
            OpenContentModuleConfig config,
            int userId,
            string raw,
            int moduleId)
        {
            JToken data;
            try
            {
                data = JToken.Parse(raw);
            }
            catch (JsonException ex)
            {
                return ToolKit.Fail(
                    "This is a single-item module, so json must be a JSON object. Parse error: "
                    + ex.Message);
            }

            var context = OpenContentUtils.CreateDataContext(config, userId, true, null);
            var item = dataSource.Get(context, null);

            if (item == null)
            {
                dataSource.Add(context, data);

                return ToolKit.Ok(
                    "Created the content item for module " + moduleId
                    + " (it had no data record yet).");
            }

            dataSource.Update(context, item, data);

            return ToolKit.Ok("Updated the content item for module " + moduleId + ".");
        }

        /// <summary>
        /// Best-effort removal used to roll back a partially created module.
        /// </summary>
        private static bool TryRemove(int tabId, int moduleId)
        {
            try
            {
                ModuleController.Instance.DeleteTabModule(tabId, moduleId, false);
                ModuleController.Instance.ClearCache(tabId);

                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// A validated template folder: which main template file it uses, and its seed data.
        /// </summary>
        private class TemplateSetup
        {
            public string Directory { get; private set; }

            public string MainFile { get; private set; }

            public JObject Seed { get; private set; }

            public bool HasSeedFile { get; private set; }

            /// <summary>
            /// Validates a template folder and reads its seed data, returning null with a
            /// message when it cannot be used.
            /// </summary>
            public static TemplateSetup Resolve(string templateName, out string error)
            {
                var root = Path.Combine(
                    PortalSettings.Current.HomeDirectoryMapPath, "OpenContent", "Templates");
                var directory = Path.Combine(root, templateName);

                if (!System.IO.Directory.Exists(directory))
                {
                    error = "Template '" + templateName + "' was not found under " + root
                            + ". Use list-opencontent-templates to see the available names.";

                    return null;
                }

                // The module hardcodes template.hbs, so a Razor-only template gets a setting
                // pointing at a file that does not exist. Detect whichever is present.
                var mainFile = new[] { "template.hbs", "template.cshtml" }
                    .FirstOrDefault(f => File.Exists(Path.Combine(directory, f)));

                if (mainFile == null)
                {
                    error = "Template '" + templateName
                            + "' contains neither template.hbs nor template.cshtml.";

                    return null;
                }

                // The module passes data.json's contents straight to JObject.Parse, which
                // throws ArgumentNullException for every template that ships without one.
                var dataFile = Path.Combine(directory, "data.json");
                var hasSeedFile = File.Exists(dataFile);
                var seedText = hasSeedFile ? File.ReadAllText(dataFile) : null;

                JObject seed;
                try
                {
                    seed = string.IsNullOrWhiteSpace(seedText)
                        ? new JObject()
                        : JObject.Parse(seedText);
                }
                catch (JsonException ex)
                {
                    error = "Template '" + templateName
                            + "' has a data.json that is not valid JSON: " + ex.Message;

                    return null;
                }

                error = null;

                return new TemplateSetup
                {
                    Directory = directory,
                    MainFile = mainFile,
                    Seed = seed,
                    HasSeedFile = hasSeedFile,
                };
            }
        }
    }
}
