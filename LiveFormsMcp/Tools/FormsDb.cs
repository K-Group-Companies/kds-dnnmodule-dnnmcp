using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text.RegularExpressions;
using DotNetNuke.Data;
using DotNetNuke.Entities.Modules;
using DotNetNuke.Security.Permissions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace KGroup.LiveFormsMcp.Tools
{
    /// <summary>
    /// Data access and shared helpers for the Live Forms tools.
    /// </summary>
    /// <remarks>
    /// Live Forms (Mandeeps) keeps one row per module in <c>LiveForms</c>: the form layout
    /// as JSON in <c>FormDefinition</c> (whose <c>Fields</c> property is itself a JSON
    /// string, not an array) and the submit actions as UTF-16 XmlSerializer output in
    /// <c>Actions</c>. The vendor assembly is obfuscated and has no public API, so the
    /// tools read and write that observed shape directly through DAL2, as the EasyDNNNews
    /// tools do. Every query is scoped to modules returned by <see cref="ModuleController"/>
    /// for the current portal, so a caller-supplied module ID can never reach another
    /// portal's form.
    /// </remarks>
    internal static class FormsDb
    {
        /// <summary>Desktop module name of Live Forms (its definition is named "Live Forms").</summary>
        public const string ModuleName = "LiveForms";

        public static string Sql(string sql)
        {
            var provider = DataProvider.Instance();

            return sql
                .Replace("{databaseOwner}", provider.DatabaseOwner)
                .Replace("{objectQualifier}", provider.ObjectQualifier);
        }

        /// <summary>
        /// Opens a DAL2 context. The parameterless <c>DataContext.Instance()</c> is obsolete
        /// from DNN 10.2.2 in favour of an overload taking <c>IHostSettings</c>, which lives in
        /// <c>DotNetNuke.Abstractions</c>, not referenced here. Kept in one place so the switch
        /// is a one-line change.
        /// </summary>
        private static IDataContext Open()
        {
#pragma warning disable CS0618 // see summary
            return DataContext.Instance();
#pragma warning restore CS0618
        }

        public static List<T> Query<T>(string sql, params object[] args)
        {
            using (var ctx = Open())
            {
                return ctx.ExecuteQuery<T>(CommandType.Text, Sql(sql), args).ToList();
            }
        }

        public static T Scalar<T>(string sql, params object[] args)
        {
            using (var ctx = Open())
            {
                return ctx.ExecuteScalar<T>(CommandType.Text, Sql(sql), args);
            }
        }

        public static T Scalar<T>(IDataContext ctx, string sql, params object[] args)
        {
            return ctx.ExecuteScalar<T>(CommandType.Text, Sql(sql), args);
        }

        public static void Exec(IDataContext ctx, string sql, params object[] args)
        {
            ctx.Execute(CommandType.Text, Sql(sql), args);
        }

        /// <summary>
        /// Runs <paramref name="work"/> in one transaction so the form row and its settings
        /// rows change together.
        /// </summary>
        public static void InTransaction(Action<IDataContext> work)
        {
            using (var ctx = Open())
            {
                ctx.BeginTransaction();
                try
                {
                    work(ctx);
                    ctx.Commit();
                }
                catch
                {
                    ctx.RollbackTransaction();
                    throw;
                }
            }
        }

        internal class FormRow
        {
            public int ModuleId { get; set; }

            public string FormDefinition { get; set; }

            public string Actions { get; set; }

            public string Localization { get; set; }
        }

        internal class SettingRow
        {
            public string Identifier { get; set; }

            public string Name { get; set; }

            public string Value { get; set; }
        }

        internal class StatsRow
        {
            public int ModuleID { get; set; }

            public int Total { get; set; }

            public int Drafts { get; set; }

            public int Last30Days { get; set; }

            public DateTime? FirstOn { get; set; }

            public DateTime? LastOn { get; set; }
        }

        /// <summary>
        /// The Live Forms modules on the portal (not deleted, not on a deleted page).
        /// </summary>
        /// <remarks>
        /// <c>GetModulesByDefinition</c> matches the module definition's name ("Live Forms"),
        /// not the desktop module name, so the definition names are read from the desktop
        /// module rather than hard-coded. The lookup's <c>IHostSettings</c> overload is not
        /// reachable without <c>DotNetNuke.Abstractions</c>, as with <see cref="Open"/>.
        /// </remarks>
        public static List<ModuleInfo> FormModules(int portalId)
        {
#pragma warning disable CS0618 // see remarks
            var desktopModule = DesktopModuleController.GetDesktopModuleByModuleName(ModuleName, portalId);
#pragma warning restore CS0618
            if (desktopModule == null)
            {
                return new List<ModuleInfo>();
            }

            return desktopModule.ModuleDefinitions.Values
                .SelectMany(d => ModuleController.Instance.GetModulesByDefinition(portalId, d.DefinitionName).Cast<ModuleInfo>())
                .Where(m => !m.IsDeleted && m.TabID > 0 && !(m.ParentTab?.IsDeleted ?? false))
                .GroupBy(m => m.ModuleID)
                .Select(g => g.First())
                .ToList();
        }

        /// <summary>
        /// Resolves a Live Forms module on the portal that the current user may edit.
        /// </summary>
        public static ModuleInfo ResolveForm(int portalId, int moduleId, out string error)
        {
            error = null;
            var module = FormModules(portalId).FirstOrDefault(m => m.ModuleID == moduleId);
            if (module == null)
            {
                error = "Module " + moduleId + " is not a Live Forms module on this portal. Use get-liveforms to list them.";
                return null;
            }

            if (!ModulePermissionController.CanEditModuleContent(module))
            {
                error = "You do not have edit rights on module " + moduleId + ".";
                return null;
            }

            return module;
        }

        public static FormRow LoadForm(int moduleId)
        {
            return Query<FormRow>(
                "SELECT ModuleId, FormDefinition, Actions, Localization FROM {databaseOwner}[{objectQualifier}LiveForms] WHERE ModuleId = @0",
                moduleId).FirstOrDefault();
        }

        /// <summary>
        /// Forms for several modules in one round trip. IDs come from
        /// <see cref="FormModules"/>, never the caller, so inlining them is safe.
        /// </summary>
        public static List<FormRow> LoadForms(ICollection<int> moduleIds)
        {
            if (moduleIds.Count == 0)
            {
                return new List<FormRow>();
            }

            return Query<FormRow>(
                "SELECT ModuleId, FormDefinition, Actions, Localization FROM {databaseOwner}[{objectQualifier}LiveForms] WHERE ModuleId IN ("
                + string.Join(",", moduleIds) + ")");
        }

        public static List<SettingRow> LoadSettings(int moduleId)
        {
            return Query<SettingRow>(
                "SELECT Identifier, Name, Value FROM {databaseOwner}[{objectQualifier}LiveForms_Setting] WHERE ModuleID = @0 ORDER BY SettingID",
                moduleId);
        }

        /// <summary>
        /// Submission counts per module. Counts only: submissions hold visitors' personal
        /// data, which these tools never return.
        /// </summary>
        public static Dictionary<int, StatsRow> SubmissionStats(ICollection<int> moduleIds)
        {
            if (moduleIds.Count == 0)
            {
                return new Dictionary<int, StatsRow>();
            }

            return Query<StatsRow>(
                "SELECT ModuleID, COUNT(*) AS Total, "
                + "SUM(CASE WHEN IsDraft = 1 THEN 1 ELSE 0 END) AS Drafts, "
                + "SUM(CASE WHEN CreatedOn >= DATEADD(day, -30, GETDATE()) THEN 1 ELSE 0 END) AS Last30Days, "
                + "MIN(CreatedOn) AS FirstOn, MAX(CreatedOn) AS LastOn "
                + "FROM {databaseOwner}[{objectQualifier}LiveForms_Submission] WHERE ModuleID IN ("
                + string.Join(",", moduleIds) + ") GROUP BY ModuleID")
                .ToDictionary(r => r.ModuleID);
        }

        /// <summary>
        /// The parsed <c>Fields</c> array of a form definition. The vendor stores it as a JSON
        /// string inside the definition object.
        /// </summary>
        public static JArray Fields(string formDefinition)
        {
            if (string.IsNullOrWhiteSpace(formDefinition))
            {
                return new JArray();
            }

            var raw = JObject.Parse(formDefinition)["Fields"];
            if (raw == null || raw.Type == JTokenType.Null)
            {
                return new JArray();
            }

            return raw.Type == JTokenType.Array ? (JArray)raw : JArray.Parse((string)raw);
        }

        /// <summary>Fields that collect a value (the vendor's <c>IResponse</c> flag).</summary>
        public static IEnumerable<JObject> InputFields(JArray fields)
        {
            return fields.OfType<JObject>().Where(f => (bool?)f["IResponse"] == true);
        }

        /// <summary>
        /// Maps <c>[FIELD-GUID]</c> tokens to readable <c>[field:Token]</c> form for output.
        /// </summary>
        public static string ReadableTokens(string text, JArray fields)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text;
            }

            foreach (var f in fields.OfType<JObject>())
            {
                var guid = (string)f["GUID"];
                if (!string.IsNullOrEmpty(guid))
                {
                    text = text.Replace("[" + guid + "]", "[field:" + ((string)f["UniqueToken"] ?? (string)f["Type"]) + "]");
                }
            }

            return text;
        }

        private static readonly Regex EmailPattern = new Regex(
            @"([A-Za-z0-9._%+-])[A-Za-z0-9._%+-]*@([A-Za-z0-9.-]+\.[A-Za-z]{2,})",
            RegexOptions.Compiled);

        /// <summary>
        /// Masks the local part of every address (<c>j***@example.com</c>) so the domain
        /// stays visible for checking a configuration without echoing full addresses.
        /// </summary>
        public static string MaskEmails(string text)
        {
            return string.IsNullOrEmpty(text) ? text : EmailPattern.Replace(text, "$1***@$2");
        }

        /// <summary>
        /// One address in the same character set <see cref="EmailPattern"/> masks, so every
        /// address accepted here is also masked when echoed back.
        /// </summary>
        private static readonly Regex SingleEmail = new Regex(
            @"^[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}$",
            RegexOptions.Compiled);

        /// <summary>True for one or more addresses separated by , or ; (";" alone is not a list).</summary>
        public static bool LooksLikeEmailList(string text)
        {
            var addresses = (text ?? string.Empty)
                .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(a => a.Trim())
                .Where(a => a.Length > 0)
                .ToList();
            return addresses.Count > 0 && addresses.All(SingleEmail.IsMatch);
        }

        /// <summary>
        /// Reads an argument that should be a JSON array, sent either as an array or as a
        /// JSON string.
        /// </summary>
        public static JArray JsonArray(IDictionary<string, object> args, string name)
        {
            if (args == null || !args.TryGetValue(name, out var raw) || raw == null)
            {
                return null;
            }

            if (raw is JArray array)
            {
                return array;
            }

            if (raw is string text)
            {
                return string.IsNullOrWhiteSpace(text) ? null : JArray.Parse(text);
            }

            return JArray.FromObject(raw);
        }

        public static string Json(object value)
        {
            return JsonConvert.SerializeObject(value, Formatting.Indented);
        }
    }
}
