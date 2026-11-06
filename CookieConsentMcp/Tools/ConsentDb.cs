using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using DotNetNuke.Common.Utilities;
using DotNetNuke.Data;
using DotNetNuke.Entities.Modules;
using DotNetNuke.Entities.Portals;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace KGroup.CookieConsentMcp.Tools
{
    /// <summary>
    /// Data access and shared helpers for the Cookie Consent tools.
    /// </summary>
    /// <remarks>
    /// EasyDNN Cookie Consent keeps one settings row per portal in
    /// <c>EasyDNNCookieConsent_Settings</c> and per-portal rows in <c>_Categories</c>,
    /// <c>_CookieCatalog</c> and <c>_CookieRules</c>. The vendor has no public API for them,
    /// so the tools read and write those tables directly through DAL2, as the EasyDNNNews and
    /// Live Forms tools do. Every statement is filtered on the current portal's ID, never a
    /// caller-supplied one. The vendor creates the settings row and its default categories,
    /// catalog entries and rules itself, the first time its module renders.
    /// </remarks>
    internal static class ConsentDb
    {
        /// <summary>Desktop module name of EasyDNN Cookie Consent.</summary>
        public const string ModuleName = "EasyDNNCookieConsent";

        /// <summary>
        /// Prefix of every DataCache key the vendor uses (settings, rendered banner HTML,
        /// activation data), from <c>CookieModuleGlobals.CacheKeyPrefix</c>.
        /// </summary>
        public const string CachePrefix = "eds_cookiemodule_";

        public const string Table = "{databaseOwner}{objectQualifier}EasyDNNCookieConsent_";

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
        /// Runs <paramref name="work"/> in one transaction so settings, categories, catalog
        /// and rules change together or not at all.
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

        /// <summary>
        /// Reads the allowlisted settings columns for a portal as camelCase name/value pairs,
        /// or null when the vendor has not created the row yet. A data reader is used rather
        /// than a POCO so the column list stays driven by <see cref="ConsentFields.All"/>; the
        /// encrypted OpenAI key is only ever tested for presence in SQL and never selected.
        /// </summary>
        public static Dictionary<string, object> LoadSettings(int portalId)
        {
            var columns = string.Join(", ", ConsentFields.All.Select(f => "[" + f.Column + "]"));
            var sql = Sql(
                "SELECT " + columns + ", [SettingsSaved], [UpdatedOnUtc], [AiAssistantEnabled], "
                + "CASE WHEN ISNULL([AiOpenAiApiKeyEncrypted], '') = '' THEN 0 ELSE 1 END AS [AiApiKeySet] "
                + "FROM " + Table + "Settings WHERE [PortalId] = @PortalId");

            using (var reader = DataProvider.Instance().ExecuteSQL(sql, new SqlParameter("@PortalId", portalId)))
            {
                if (reader == null || !reader.Read())
                {
                    return null;
                }

                var row = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                foreach (var field in ConsentFields.All)
                {
                    var value = reader[field.Column];
                    row[field.Name] = value == DBNull.Value ? null : value;
                }

                row["settingsSaved"] = Convert.ToBoolean(reader["SettingsSaved"]);
                row["updatedOnUtc"] = reader["UpdatedOnUtc"];
                row["aiAssistantEnabled"] = Convert.ToBoolean(reader["AiAssistantEnabled"]);
                row["aiApiKeySet"] = Convert.ToInt32(reader["AiApiKeySet"]) == 1;

                return row;
            }
        }

        /// <summary>
        /// Updates the given settings columns and marks the configuration as saved, which is
        /// what the vendor's own Save button does. Column names come only from
        /// <see cref="ConsentFields"/>; values are always parameters.
        /// </summary>
        public static void UpdateSettings(IDataContext ctx, int portalId, IDictionary<string, object> values)
        {
            if (values.Count == 0)
            {
                return;
            }

            var sets = new List<string>();
            var args = new List<object>();
            foreach (var pair in values)
            {
                var field = ConsentFields.Find(pair.Key) ?? throw new ArgumentException("Unknown setting " + pair.Key);
                sets.Add("[" + field.Column + "] = @" + args.Count);
                args.Add(pair.Value);
            }

            args.Add(portalId);
            Exec(
                ctx,
                "UPDATE " + Table + "Settings SET " + string.Join(", ", sets)
                + ", [SettingsSaved] = 1, [UpdatedOnUtc] = GETUTCDATE() WHERE [PortalId] = @" + (args.Count - 1),
                args.ToArray());
        }

        public static List<CategoryRow> Categories(int portalId)
        {
            return Query<CategoryRow>(
                "SELECT CategoryId, CategoryKey, Name, Description, IsRequired, Enabled, SortOrder FROM "
                + Table + "Categories WHERE PortalId = @0 ORDER BY SortOrder, CategoryKey",
                portalId);
        }

        public static List<CatalogRow> Catalog(int portalId)
        {
            return Query<CatalogRow>(
                "SELECT CookieCatalogId, CookieName, Provider, Purpose, Duration, CategoryKey, IsThirdParty, "
                + "ProviderPolicyUrl, Description, Enabled, SortOrder FROM " + Table
                + "CookieCatalog WHERE PortalId = @0 ORDER BY SortOrder, CookieName",
                portalId);
        }

        public static List<RuleRow> Rules(int portalId)
        {
            return Query<RuleRow>(
                "SELECT CookieRuleId, CookieName, MatchType, CategoryKey, Path, Domain, HttpOnly, Secure, "
                + "DeleteOnReject, DeleteOnWithdraw, DeleteWhenCategoryDisabled, Enabled, Description, SortOrder FROM "
                + Table + "CookieRules WHERE PortalId = @0 ORDER BY SortOrder, CookieName",
                portalId);
        }

        public static int ScriptCount(int portalId)
        {
            return Scalar<int>("SELECT COUNT(*) FROM " + Table + "Scripts WHERE PortalId = @0", portalId);
        }

        /// <summary>Consent log size only; individual consent records are never returned.</summary>
        public static LogCounts ConsentLog(int portalId)
        {
            return Query<LogCounts>(
                "SELECT COUNT(*) AS Total, "
                + "SUM(CASE WHEN CreatedOnUtc >= DATEADD(day, -30, GETUTCDATE()) THEN 1 ELSE 0 END) AS Last30Days, "
                + "MAX(CreatedOnUtc) AS LastOnUtc FROM " + Table + "ConsentLog WHERE PortalId = @0",
                portalId).Single();
        }

        /// <summary>
        /// Every non-deleted Cookie Consent module on the portal, with how many pages it is on
        /// and on how many of those its container is shown (<c>DisplayTitle</c> is the
        /// "Display Container?" checkbox).
        /// </summary>
        public static List<PlacementRow> Placements(int portalId)
        {
            return Query<PlacementRow>(
                "SELECT m.ModuleID AS ModuleId, m.AllTabs, "
                + "MIN(tm.TabID) AS FirstTabId, COUNT(tm.TabModuleID) AS PageCount, "
                + "SUM(CASE WHEN tm.DisplayTitle = 1 THEN 1 ELSE 0 END) AS ContainerShownCount, "
                + "MIN(tm.PaneName) AS PaneName "
                + "FROM {databaseOwner}{objectQualifier}Modules m "
                + "JOIN {databaseOwner}{objectQualifier}ModuleDefinitions md ON md.ModuleDefID = m.ModuleDefID "
                + "JOIN {databaseOwner}{objectQualifier}DesktopModules dm ON dm.DesktopModuleID = md.DesktopModuleID "
                + "JOIN {databaseOwner}{objectQualifier}TabModules tm ON tm.ModuleID = m.ModuleID AND tm.IsDeleted = 0 "
                + "WHERE m.PortalID = @0 AND m.IsDeleted = 0 AND dm.ModuleName = @1 "
                + "GROUP BY m.ModuleID, m.AllTabs ORDER BY m.ModuleID",
                portalId,
                ModuleName);
        }

        /// <summary>
        /// The GTM container ID set in DNN's own Google Tag Manager connector (Persona Bar &gt;
        /// Settings &gt; Connectors), stored as <c>AnalyticsConfig</c> XML in the portal's home
        /// folder. Null when the connector is not configured. That connector injects GTM on
        /// every page regardless of consent, so it should be empty when Cookie Consent loads GTM.
        /// </summary>
        public static string ConnectorGtmId(PortalSettings portal)
        {
            var path = Path.Combine(portal.HomeDirectoryMapPath, "GoogleTagManager.config");
            if (!File.Exists(path))
            {
                return null;
            }

            var setting = XDocument.Load(path)
                .Descendants("AnalyticsSetting")
                .FirstOrDefault(s => (string)s.Element("SettingName") == "GtmId");
            var id = (string)setting?.Element("SettingValue");

            return string.IsNullOrWhiteSpace(id) ? null : id.Trim();
        }

        /// <summary>
        /// Drops the vendor's cached settings and rendered banner, and DNN's output cache of
        /// each Cookie Consent module, so the next request renders the new configuration.
        /// </summary>
        public static void ClearCache(int portalId)
        {
            DataCache.ClearCache(CachePrefix);
            foreach (var placement in Placements(portalId))
            {
                ModuleController.SynchronizeModule(placement.ModuleId);
            }
        }

        public static JObject JsonObject(IDictionary<string, object> args, string name)
        {
            if (args == null || !args.TryGetValue(name, out var raw) || raw == null)
            {
                return null;
            }

            if (raw is JObject obj)
            {
                return obj;
            }

            if (raw is string text)
            {
                return string.IsNullOrWhiteSpace(text) ? null : JObject.Parse(text);
            }

            return JObject.FromObject(raw);
        }

        public static string Json(object value)
        {
            return JsonConvert.SerializeObject(value, Formatting.Indented);
        }

        internal class CategoryRow
        {
            public int CategoryId { get; set; }

            public string CategoryKey { get; set; }

            public string Name { get; set; }

            public string Description { get; set; }

            public bool IsRequired { get; set; }

            public bool Enabled { get; set; }

            public int SortOrder { get; set; }
        }

        internal class CatalogRow
        {
            public int CookieCatalogId { get; set; }

            public string CookieName { get; set; }

            public string Provider { get; set; }

            public string Purpose { get; set; }

            public string Duration { get; set; }

            public string CategoryKey { get; set; }

            public bool IsThirdParty { get; set; }

            public string ProviderPolicyUrl { get; set; }

            public string Description { get; set; }

            public bool Enabled { get; set; }

            public int SortOrder { get; set; }
        }

        internal class RuleRow
        {
            public int CookieRuleId { get; set; }

            public string CookieName { get; set; }

            public string MatchType { get; set; }

            public string CategoryKey { get; set; }

            public string Path { get; set; }

            public string Domain { get; set; }

            public bool HttpOnly { get; set; }

            public bool Secure { get; set; }

            public bool DeleteOnReject { get; set; }

            public bool DeleteOnWithdraw { get; set; }

            public bool DeleteWhenCategoryDisabled { get; set; }

            public bool Enabled { get; set; }

            public string Description { get; set; }

            public int SortOrder { get; set; }
        }

        internal class LogCounts
        {
            public int Total { get; set; }

            public int? Last30Days { get; set; }

            public DateTime? LastOnUtc { get; set; }
        }

        internal class PlacementRow
        {
            public int ModuleId { get; set; }

            public bool AllTabs { get; set; }

            public int FirstTabId { get; set; }

            public int PageCount { get; set; }

            public int ContainerShownCount { get; set; }

            public string PaneName { get; set; }
        }
    }
}
