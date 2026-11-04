using System.Collections.Generic;
using System.Linq;
using Dnn.Mcp.WebApi;
using Dnn.Mcp.WebApi.Models;
using Dnn.Mcp.WebApi.Services;
using DotNetNuke.Entities.Portals;
using DotNetNuke.Security.Permissions;
using Newtonsoft.Json.Linq;
using Satrabel.AIChat.Tools;

namespace KGroup.LiveFormsMcp.Tools
{
    /// <summary>
    /// Read-only Live Forms tools: <c>get-liveforms</c>, <c>get-liveform</c> and
    /// <c>get-liveform-submission-stats</c>.
    /// </summary>
    /// <remarks>
    /// Only forms the caller can edit are visible. Submissions are never returned, only
    /// counts. Addresses in actions are masked and captcha keys are redacted, because the
    /// vendor stores the reCAPTCHA secret key in plain text inside the form definition.
    /// </remarks>
    public class FormReadTools : IMcpProvider
    {
        private const string Category = "LiveForms";

        /// <inheritdoc />
        public void Register(IMcpRegistry registry)
        {
            registry.RegisterTool(new ToolDefinition
            {
                Name = "get-liveforms",
                Title = "Get Live Forms",
                Description =
                    "List the Live Forms modules on the current portal that you can edit: page, title, "
                    + "number of input fields, action types (Email, ShowMessage, Redirect) and submission count.",
                Category = Category,
                ReadOnly = true,
                Parameters = new List<ToolParameter>(),
                Handler = ToolKit.Guard("get-liveforms", GetForms),
            });

            registry.RegisterTool(new ToolDefinition
            {
                Name = "get-liveform",
                Title = "Get Live Form",
                Description =
                    "Show one Live Forms form: its fields (token, type, label, required, options, layout) "
                    + "and its submit actions with field tokens shown as [field:Token]. Email addresses "
                    + "are masked and captcha keys are not returned.",
                Category = Category,
                ReadOnly = true,
                Parameters = new List<ToolParameter>
                {
                    ToolKit.Param("moduleId", "int", "Live Forms module ID (see get-liveforms).", true),
                },
                Handler = ToolKit.Guard("get-liveform", GetForm),
            });

            registry.RegisterTool(new ToolDefinition
            {
                Name = "get-liveform-submission-stats",
                Title = "Get Live Form submission stats",
                Description =
                    "Submission counts for Live Forms: total, drafts, last 30 days, first and last "
                    + "submission date. Never returns submission contents.",
                Category = Category,
                ReadOnly = true,
                Parameters = new List<ToolParameter>
                {
                    ToolKit.Param("moduleId", "int", "Live Forms module ID. Omit for every form you can edit.", false),
                },
                Handler = ToolKit.Guard("get-liveform-submission-stats", GetStats),
            });
        }

        private static List<DotNetNuke.Entities.Modules.ModuleInfo> EditableForms(int portalId)
        {
            return FormsDb.FormModules(portalId).Where(ModulePermissionController.CanEditModuleContent).ToList();
        }

        private static CallToolResult GetForms(Dictionary<string, object> args)
        {
            var modules = EditableForms(PortalSettings.Current.PortalId);
            var ids = modules.Select(m => m.ModuleID).ToList();
            var forms = FormsDb.LoadForms(ids).ToDictionary(f => f.ModuleId);
            var stats = FormsDb.SubmissionStats(ids);

            var result = modules.OrderBy(m => m.TabID).Select(m =>
            {
                forms.TryGetValue(m.ModuleID, out var form);
                return new
                {
                    moduleId = m.ModuleID,
                    tabId = m.TabID,
                    page = m.ParentTab?.TabName,
                    title = m.ModuleTitle,
                    configured = form != null,
                    inputFields = form == null ? 0 : FormsDb.InputFields(FormsDb.Fields(form.FormDefinition)).Count(),
                    actions = form == null ? new List<string>() : ActionsXml.Types(form.Actions),
                    submissions = stats.TryGetValue(m.ModuleID, out var s) ? s.Total : 0,
                };
            });

            return ToolKit.Ok(FormsDb.Json(result));
        }

        private static CallToolResult GetForm(Dictionary<string, object> args)
        {
            if (!ToolKit.TryInt(args, "moduleId", out var moduleId))
            {
                return ToolKit.Fail("moduleId is required.");
            }

            var module = FormsDb.ResolveForm(PortalSettings.Current.PortalId, moduleId, out var error);
            if (module == null)
            {
                return ToolKit.Fail(error);
            }

            var form = FormsDb.LoadForm(moduleId);
            if (form == null)
            {
                return ToolKit.Ok("Module " + moduleId + " has no form yet. Create one with add-liveform or copy-liveform.");
            }

            var fields = FormsDb.Fields(form.FormDefinition);
            var result = new
            {
                moduleId,
                tabId = module.TabID,
                title = module.ModuleTitle,
                fields = fields.OfType<JObject>().Select(Describe).ToList(),
                actions = ActionsXml.Summarize(form.Actions, fields),
                localized = !string.IsNullOrWhiteSpace(form.Localization),
            };

            return ToolKit.Ok(FormsDb.Json(result));
        }

        private static object Describe(JObject f)
        {
            var type = (string)f["Type"];
            var options = f["Options"] as JObject;
            string html = null;
            if (type == "Text")
            {
                html = (string)f["Html"];
                if (html != null && html.Length > 300)
                {
                    html = html.Substring(0, 300) + "...";
                }
            }

            return new
            {
                guid = (string)f["GUID"],
                token = (string)f["UniqueToken"],
                type,
                label = (string)f["Label"]?["Text"] ?? (string)f["Text"],
                input = (bool?)f["IResponse"] ?? false,
                required = (bool?)f["IsRequired"],
                options = options?.Properties().Select(p => (string)p.Value).ToList(),
                html,
                provider = type == "Captcha" ? (string)f["Provider"] : null,
                row = (int?)f["RowIndex"],
                colSpan = (int?)f["ColSpan"],
                hidden = (bool?)f["Hide"] == true ? true : (bool?)null,
            };
        }

        private static CallToolResult GetStats(Dictionary<string, object> args)
        {
            var portalId = PortalSettings.Current.PortalId;
            List<DotNetNuke.Entities.Modules.ModuleInfo> modules;
            if (ToolKit.TryInt(args, "moduleId", out var moduleId))
            {
                var module = FormsDb.ResolveForm(portalId, moduleId, out var error);
                if (module == null)
                {
                    return ToolKit.Fail(error);
                }

                modules = new List<DotNetNuke.Entities.Modules.ModuleInfo> { module };
            }
            else
            {
                modules = EditableForms(portalId);
            }

            var stats = FormsDb.SubmissionStats(modules.Select(m => m.ModuleID).ToList());
            var result = modules.OrderBy(m => m.TabID).Select(m =>
            {
                stats.TryGetValue(m.ModuleID, out var s);
                return new
                {
                    moduleId = m.ModuleID,
                    page = m.ParentTab?.TabName,
                    title = m.ModuleTitle,
                    total = s?.Total ?? 0,
                    drafts = s?.Drafts ?? 0,
                    last30Days = s?.Last30Days ?? 0,
                    first = s?.FirstOn,
                    last = s?.LastOn,
                };
            });

            return ToolKit.Ok(FormsDb.Json(result));
        }
    }
}
