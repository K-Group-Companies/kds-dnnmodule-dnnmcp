using System;
using System.Collections.Generic;
using System.Linq;
using Dnn.Mcp.WebApi;
using Dnn.Mcp.WebApi.Models;
using Dnn.Mcp.WebApi.Services;
using DotNetNuke.Common.Utilities;
using DotNetNuke.Entities.Modules;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Satrabel.AIChat.Tools
{
    /// <summary>
    /// Module-level tools the module does not provide: writing module settings, moving a
    /// module between panes, and deleting a module.
    /// </summary>
    /// <remarks>
    /// All three go through <see cref="ModuleController"/> rather than touching
    /// <c>dbo.ModuleSettings</c> directly, so DNN's cache is invalidated as a side effect.
    /// A direct SQL write leaves the module cache stale and needs an app-pool recycle
    /// before the change is visible.
    /// </remarks>
    public class ModuleTools : IMcpProvider
    {
        private const string OpenContentModuleName = "OpenContent";

        /// <summary>
        /// The module setting OpenContent keeps its template settings in, as a single JSON
        /// document rather than one setting per value.
        /// </summary>
        private const string DataSettingName = "data";

        /// <inheritdoc />
        public void Register(IMcpRegistry registry)
        {
            registry.RegisterTool(new ToolDefinition
            {
                Name = "set-module-settings",
                Title = "Set module settings",
                Description =
                    "Set module settings as key/value pairs. On an OpenContent module, settings "
                    + "declared in the template's template-schema.json (Rounded, AlignItems, …) "
                    + "are merged into the single 'data' JSON blob that OpenContent actually "
                    + "reads, with values coerced to the schema's types and validated against "
                    + "its enums. Writing them as individual module settings has no effect, so "
                    + "unrecognised names are rejected rather than reported as written. Routed "
                    + "through ModuleController so DNN's cache is cleared.",
                Category = "Modules",
                ReadOnly = false,
                Parameters = new List<ToolParameter>
                {
                    ToolKit.Param("moduleId", "int","The ID of the module to update.", true),
                    ToolKit.Param(
                        "settings",
                        "string",
                        "A JSON object of setting name/value pairs, e.g. "
                        + "{\"Rounded\":true,\"AlignItems\":\"center\"} for OpenContent template "
                        + "settings, or {\"DNNCacheTime\":\"0\"} for a plain module setting.",
                        true),
                    ToolKit.Param(
                        "raw",
                        "bool",
                        "Write every key straight to ModuleSettings with no OpenContent routing "
                        + "or validation. Defaults to false. Only needed for settings the "
                        + "template schema does not declare.",
                        false),
                },
                Handler = ToolKit.Guard("set-module-settings", SetModuleSettings),
            });

            registry.RegisterTool(new ToolDefinition
            {
                Name = "update-module",
                Title = "Update module title / container",
                Description =
                    "Update a module's page-settings: its title and whether its container "
                    + "(the skin wrapper with the title bar) is shown. 'displayContainer' maps to "
                    + "the 'Display Container?' checkbox in module settings — set it false to render "
                    + "the module bare, with no container chrome. Omitted fields are left unchanged. "
                    + "Routed through ModuleController so DNN's cache is cleared.",
                Category = "Modules",
                ReadOnly = false,
                Parameters = new List<ToolParameter>
                {
                    ToolKit.Param("tabId", "int", "The ID of the page containing the module.", true),
                    ToolKit.Param("moduleId", "int", "The ID of the module to update.", true),
                    ToolKit.Param(
                        "title",
                        "string",
                        "New module title. Pass an empty string to clear it (DNN then falls back "
                        + "to the module definition's friendly name). Omit to leave unchanged.",
                        false),
                    ToolKit.Param(
                        "displayContainer",
                        "bool",
                        "Whether to display the module's container. False renders the module with "
                        + "no container (no title bar or border). Omit to leave unchanged.",
                        false),
                },
                Handler = ToolKit.Guard("update-module", UpdateModule),
            });

            registry.RegisterTool(new ToolDefinition
            {
                Name = "move-module",
                Title = "Move module",
                Description =
                    "Move a module to a different pane and/or change its order within the pane. "
                    + "Use get-modules to discover current PaneName values for a page.",
                Category = "Modules",
                ReadOnly = false,
                Parameters = new List<ToolParameter>
                {
                    ToolKit.Param("tabId", "int","The ID of the page containing the module.", true),
                    ToolKit.Param("moduleId", "int","The ID of the module to move.", true),
                    ToolKit.Param(
                        "paneName",
                        "string",
                        "Target pane name, e.g. ContentPane or Full_Orange_Pane_Bottom. "
                        + "Omit to keep the current pane.",
                        false),
                    ToolKit.Param(
                        "moduleOrder",
                        "int",
                        "Order within the pane. Lower numbers appear first. DNN uses even "
                        + "numbers with gaps; omit to append at the end.",
                        false),
                },
                Handler = ToolKit.Guard("move-module", MoveModule),
            });

            registry.RegisterTool(new ToolDefinition
            {
                Name = "delete-module",
                Title = "Delete module",
                Description =
                    "Delete a module from a page. Soft-deletes to the DNN Recycle Bin by "
                    + "default, so it can be restored from the page's settings.",
                Category = "Modules",
                ReadOnly = false,
                Parameters = new List<ToolParameter>
                {
                    ToolKit.Param("tabId", "int","The ID of the page containing the module.", true),
                    ToolKit.Param("moduleId", "int","The ID of the module to delete.", true),
                    ToolKit.Param(
                        "permanent",
                        "bool",
                        "When true, bypasses the Recycle Bin and deletes permanently. "
                        + "Defaults to false.",
                        false),
                },
                Handler = ToolKit.Guard("delete-module", DeleteModule),
            });
        }

        /// <summary>
        /// Writes module settings, routing OpenContent template settings into the JSON blob
        /// that OpenContent actually reads.
        /// </summary>
        /// <remarks>
        /// OpenContent keeps template settings as a single JSON document in the <c>data</c>
        /// module setting, not as individual settings. Writing <c>Rounded</c> or
        /// <c>AlignItems</c> as their own keys stores rows nobody ever reads: the write
        /// succeeds at the DNN level while having no effect, and the module then renders
        /// nothing at all — no output, no error, no clue in the markup. Reporting that as
        /// "Updated 5 setting(s)" is worse than failing.
        ///
        /// So on an OpenContent module the incoming keys are partitioned using the template's
        /// <c>template-schema.json</c>, which is the authoritative list of what the template
        /// accepts. Schema-declared names are merged into <c>data</c> with their values coerced
        /// to the declared type and checked against any <c>enum</c>; module-level keys are
        /// written directly; anything else is rejected.
        ///
        /// Validation happens before any write, so a request either applies completely or
        /// changes nothing. Pass <c>raw: true</c> to bypass all of this.
        /// </remarks>
        private static CallToolResult SetModuleSettings(Dictionary<string, object> args)
        {
            if (!ToolKit.TryInt(args, "moduleId", out var moduleId))
            {
                return ToolKit.Fail("moduleId is required and must be an integer.");
            }

            var rawSettings = ToolKit.Str(args, "settings");
            if (rawSettings == null)
            {
                return ToolKit.Fail("settings is required: a JSON object of name/value pairs.");
            }

            JObject parsed;
            try
            {
                parsed = JObject.Parse(rawSettings);
            }
            catch (JsonException)
            {
                return ToolKit.Fail("settings is not valid JSON. Expected an object of name/value pairs.");
            }

            if (!parsed.Properties().Any())
            {
                return ToolKit.Fail("settings contained no properties to apply.");
            }

            var module = ModuleController.Instance.GetModule(moduleId, Null.NullInteger, true);
            if (module == null)
            {
                return ToolKit.Fail(
                    "Module " + moduleId + " was not found. Use get-modules to list modules on a page.");
            }

            var isOpenContent = module.DesktopModule != null
                                && module.DesktopModule.ModuleName == OpenContentModuleName;

            if (!isOpenContent || ToolKit.Bool(args, "raw"))
            {
                return ApplyDirect(module, moduleId, parsed.Properties());
            }

            var schema = TemplateSettingsSchema.ForModule(module);
            var direct = new List<JProperty>();
            var templateSettings = new JObject();
            var rejected = new List<string>();

            foreach (var property in parsed.Properties())
            {
                if (schema != null && schema.Declares(property.Name))
                {
                    if (!schema.TryCoerce(property, out var coerced, out var reason))
                    {
                        rejected.Add(property.Name + ": " + reason);
                        continue;
                    }

                    templateSettings[property.Name] = coerced;
                }
                else if (IsModuleLevelSetting(module, property.Name))
                {
                    direct.Add(property);
                }
                else
                {
                    rejected.Add(
                        property.Name + ": not declared in " + schema.Describe()
                        + " and not an existing module setting");
                }
            }

            if (rejected.Any())
            {
                return ToolKit.Fail(
                    "Nothing was written. OpenContent only reads template settings from the "
                    + "'data' blob, so these would have had no effect:" + Environment.NewLine
                    + "  " + string.Join(Environment.NewLine + "  ", rejected) + Environment.NewLine
                    + "Valid template settings for this module: " + schema.NamesForMessage()
                    + ". Pass raw:true to write them as plain module settings anyway.");
            }

            var summary = new List<string>();

            if (templateSettings.Properties().Any())
            {
                var merged = ReadDataBlob(module);

                foreach (var property in templateSettings.Properties())
                {
                    merged[property.Name] = property.Value;
                }

                ModuleController.Instance.UpdateModuleSetting(
                    moduleId, DataSettingName, merged.ToString(Formatting.None));

                summary.Add(
                    "merged into '" + DataSettingName + "': "
                    + string.Join(", ", templateSettings.Properties().Select(p => p.Name + "=" + p.Value)));
            }

            foreach (var property in direct)
            {
                ModuleController.Instance.UpdateModuleSetting(
                    moduleId, property.Name, ValueToString(property.Value));

                summary.Add("module setting " + property.Name + "=" + ValueToString(property.Value));
            }

            ModuleController.Instance.ClearCache(module.TabID);

            return ToolKit.Ok("Module " + moduleId + " updated — " + string.Join("; ", summary) + ".");
        }

        /// <summary>
        /// Writes every property straight to ModuleSettings, for non-OpenContent modules and
        /// for <c>raw: true</c>.
        /// </summary>
        private static CallToolResult ApplyDirect(
            ModuleInfo module, int moduleId, IEnumerable<JProperty> properties)
        {
            var applied = new List<string>();

            foreach (var property in properties)
            {
                var value = ValueToString(property.Value);
                ModuleController.Instance.UpdateModuleSetting(moduleId, property.Name, value);
                applied.Add(property.Name + "=" + value);
            }

            ModuleController.Instance.ClearCache(module.TabID);

            return ToolKit.Ok(
                "Updated " + applied.Count + " module setting(s) on module " + moduleId + ": "
                + string.Join(", ", applied));
        }

        /// <summary>
        /// True for keys that belong in ModuleSettings on an OpenContent module: the module's
        /// own known keys, and any setting that already exists so it can be updated.
        /// </summary>
        private static bool IsModuleLevelSetting(ModuleInfo module, string name)
        {
            if (name == "template" || name == DataSettingName || name == "detailtabid"
                || name.StartsWith("OpenContent", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return module.ModuleSettings != null && module.ModuleSettings.ContainsKey(name);
        }

        /// <summary>Reads the existing template-settings blob, or an empty object.</summary>
        private static JObject ReadDataBlob(ModuleInfo module)
        {
            var existing = module.ModuleSettings?[DataSettingName] as string;

            if (string.IsNullOrWhiteSpace(existing))
            {
                return new JObject();
            }

            try
            {
                return JObject.Parse(existing);
            }
            catch (JsonException)
            {
                // A malformed blob would otherwise take the whole call down; start fresh
                // rather than fail, since the caller is replacing values in it anyway.
                return new JObject();
            }
        }

        private static string ValueToString(JToken value)
        {
            return value == null || value.Type == JTokenType.Null
                ? string.Empty
                : value.Type == JTokenType.String ? value.Value<string>() : value.ToString(Formatting.None);
        }

        /// <summary>
        /// Updates a module's title and/or "Display Container?" flag — both live on
        /// <see cref="ModuleInfo"/> (the TabModule row), not in ModuleSettings.
        /// </summary>
        /// <remarks>
        /// The DNN field named <c>DisplayTitle</c> is the "Display Container?" checkbox: its
        /// label is "Display Container" and its help is "Check this box to display the module
        /// container." The name is a long-standing DNN misnomer, so this tool exposes it as
        /// <c>displayContainer</c> to match what the caller sees in the UI.
        ///
        /// Both fields are optional and tri-state on the wire — a field that was not sent is
        /// left as-is rather than reset. Presence is tested with <see cref="IDictionary{TKey,TValue}.ContainsKey"/>
        /// because <see cref="ToolKit.Bool"/> alone cannot tell "false" from "absent".
        /// </remarks>
        private static CallToolResult UpdateModule(Dictionary<string, object> args)
        {
            if (!ToolKit.TryInt(args, "tabId", out var tabId))
            {
                return ToolKit.Fail("tabId is required and must be an integer.");
            }

            if (!ToolKit.TryInt(args, "moduleId", out var moduleId))
            {
                return ToolKit.Fail("moduleId is required and must be an integer.");
            }

            var setTitle = args.ContainsKey("title");
            var setContainer = args.ContainsKey("displayContainer") && ToolKit.Str(args, "displayContainer") != null;

            if (!setTitle && !setContainer)
            {
                return ToolKit.Fail("Nothing to update: pass 'title' and/or 'displayContainer'.");
            }

            var module = ModuleController.Instance.GetModule(moduleId, tabId, true);
            if (module == null)
            {
                return ToolKit.Fail(
                    "Module " + moduleId + " was not found on page " + tabId
                    + ". Use get-modules to list modules on the page.");
            }

            var changes = new List<string>();

            if (setTitle)
            {
                // Read the raw value directly so an intentional empty string clears the title;
                // ToolKit.Str would collapse "" to null and hide the distinction.
                var title = args["title"] as string ?? string.Empty;
                module.ModuleTitle = title;
                changes.Add(title.Length == 0 ? "title cleared" : "title=\"" + title + "\"");
            }

            if (setContainer)
            {
                var display = ToolKit.Bool(args, "displayContainer");
                module.DisplayTitle = display;
                changes.Add("displayContainer=" + (display ? "true" : "false"));
            }

            ModuleController.Instance.UpdateModule(module);
            ModuleController.Instance.ClearCache(tabId);

            return ToolKit.Ok(
                "Updated module " + moduleId + " on page " + tabId + " — "
                + string.Join(", ", changes) + ".");
        }

        private static CallToolResult MoveModule(Dictionary<string, object> args)
        {
            if (!ToolKit.TryInt(args, "tabId", out var tabId))
            {
                return ToolKit.Fail("tabId is required and must be an integer.");
            }

            if (!ToolKit.TryInt(args, "moduleId", out var moduleId))
            {
                return ToolKit.Fail("moduleId is required and must be an integer.");
            }

            var module = ModuleController.Instance.GetModule(moduleId, tabId, true);
            if (module == null)
            {
                return ToolKit.Fail(
                    "Module " + moduleId + " was not found on page " + tabId
                    + ". Use get-modules to list modules on the page.");
            }

            var paneName = ToolKit.Str(args, "paneName") ?? module.PaneName;

            // -1 tells DNN to append at the end of the pane.
            var order = ToolKit.TryInt(args, "moduleOrder", out var requested) ? requested : -1;

            ModuleController.Instance.UpdateModuleOrder(tabId, moduleId, order, paneName);
            ModuleController.Instance.UpdateTabModuleOrder(tabId);
            ModuleController.Instance.ClearCache(tabId);

            return ToolKit.Ok(
                "Moved module " + moduleId + " to pane '" + paneName + "'"
                + (order >= 0 ? " at order " + order : " (appended)") + " on page " + tabId + ".");
        }

        private static CallToolResult DeleteModule(Dictionary<string, object> args)
        {
            if (!ToolKit.TryInt(args, "tabId", out var tabId))
            {
                return ToolKit.Fail("tabId is required and must be an integer.");
            }

            if (!ToolKit.TryInt(args, "moduleId", out var moduleId))
            {
                return ToolKit.Fail("moduleId is required and must be an integer.");
            }

            var module = ModuleController.Instance.GetModule(moduleId, tabId, true);
            if (module == null)
            {
                return ToolKit.Fail(
                    "Module " + moduleId + " was not found on page " + tabId + ".");
            }

            var title = module.ModuleTitle;
            var permanent = ToolKit.Bool(args, "permanent");

            ModuleController.Instance.DeleteTabModule(tabId, moduleId, !permanent);
            ModuleController.Instance.ClearCache(tabId);

            return ToolKit.Ok(
                "Deleted module " + moduleId + " ('" + title + "') from page " + tabId
                + (permanent ? " permanently." : " to the Recycle Bin."));
        }
    }
}
