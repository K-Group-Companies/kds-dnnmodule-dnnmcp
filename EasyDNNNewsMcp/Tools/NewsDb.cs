using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using DotNetNuke.Data;
using DotNetNuke.Entities.Modules;
using Newtonsoft.Json.Linq;
using Satrabel.AIChat.Tools;

namespace KGroup.EasyDNNNewsMcp.Tools
{
    /// <summary>
    /// Data access and shared helpers for the EasyDNNNews tools.
    /// </summary>
    /// <remarks>
    /// EasyDNNNews ships no public content API: its own UI writes the tables with inline
    /// SQL from an obfuscated assembly that changes per release. Referencing that assembly
    /// would tie this package to one vendor build, so the tools write the documented-by-
    /// observation table shape directly through DAL2 instead. Every query is scoped to the
    /// current portal; the tables themselves carry <c>PortalID</c> on articles and
    /// categories, and side tables are reached through those.
    ///
    /// SQL text carries the standard <c>{databaseOwner}{objectQualifier}</c> tokens and is
    /// passed through <see cref="Sql"/> so the tokens are resolved here rather than relying
    /// on DAL2's text-command handling to do it.
    /// </remarks>
    internal static class NewsDb
    {
        /// <summary>Desktop module name of the EasyDNNNews article module.</summary>
        public const string NewsDefinitionName = "EasyDNNNews";

        public static string Sql(string sql)
        {
            var provider = DataProvider.Instance();

            return sql
                .Replace("{databaseOwner}", provider.DatabaseOwner)
                .Replace("{objectQualifier}", provider.ObjectQualifier);
        }

        /// <summary>
        /// Opens a DAL2 context. The parameterless <c>DataContext.Instance()</c> is obsolete
        /// from DNN 10.2.2 in favour of an overload taking <c>IHostSettings</c>, but that type
        /// lives in <c>DotNetNuke.Abstractions</c>, which this package does not reference. The
        /// call is kept here, in one place, so the switch is a one-line change before v12.
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

        public static void Exec(string sql, params object[] args)
        {
            using (var ctx = Open())
            {
                Exec(ctx, sql, args);
            }
        }

        public static T Scalar<T>(string sql, params object[] args)
        {
            using (var ctx = Open())
            {
                return ctx.ExecuteScalar<T>(CommandType.Text, Sql(sql), args);
            }
        }

        /// <summary>
        /// Runs <paramref name="work"/> inside one transaction so a multi-row write either
        /// lands whole or not at all — a half-written article (row present, no URL row or
        /// category link) renders broken in the module.
        /// </summary>
        public static T InTransaction<T>(Func<IDataContext, T> work)
        {
            using (var ctx = Open())
            {
                ctx.BeginTransaction();
                try
                {
                    var result = work(ctx);
                    ctx.Commit();
                    return result;
                }
                catch
                {
                    ctx.RollbackTransaction();
                    throw;
                }
            }
        }

        public static void Exec(IDataContext ctx, string sql, params object[] args)
        {
            ctx.Execute(CommandType.Text, Sql(sql), args);
        }

        public static T Scalar<T>(IDataContext ctx, string sql, params object[] args)
        {
            return ctx.ExecuteScalar<T>(CommandType.Text, Sql(sql), args);
        }

        /// <summary>
        /// The EasyDNNNews article modules on the portal (not deleted, not on a deleted
        /// page). These are the modules an article can belong to.
        /// </summary>
        public static List<ModuleInfo> NewsModules(int portalId)
        {
            return ModuleController.Instance
                .GetModulesByDefinition(portalId, NewsDefinitionName)
                .Cast<ModuleInfo>()
                .Where(m => !m.IsDeleted && m.TabID > 0 && !(m.ParentTab?.IsDeleted ?? false))
                .GroupBy(m => m.ModuleID)
                .Select(g => g.First())
                .ToList();
        }

        /// <summary>
        /// Resolves the news module to work with: the one given, or the portal's only one.
        /// </summary>
        public static ModuleInfo ResolveNewsModule(int portalId, int? moduleId, out string error)
        {
            error = null;
            var modules = NewsModules(portalId);

            if (moduleId.HasValue)
            {
                var match = modules.FirstOrDefault(m => m.ModuleID == moduleId.Value);
                if (match == null)
                {
                    error = "Module " + moduleId.Value + " is not an EasyDNNNews module on this portal. "
                        + "News modules here: " + Describe(modules) + ".";
                }

                return match;
            }

            if (modules.Count == 1)
            {
                return modules[0];
            }

            error = modules.Count == 0
                ? "This portal has no EasyDNNNews module. Add one to a page first (add-module, moduleName 'EasyDNNnews')."
                : "This portal has several EasyDNNNews modules; pass moduleId. Options: " + Describe(modules) + ".";

            return null;
        }

        private static string Describe(IEnumerable<ModuleInfo> modules)
        {
            var list = modules.Select(m => m.ModuleID + " (page " + m.TabID + ", '" + m.ModuleTitle + "')").ToList();

            return list.Count == 0 ? "none" : string.Join(", ", list);
        }

        /// <summary>
        /// Reads a list argument sent either as a JSON array or as a comma-separated string.
        /// </summary>
        public static List<string> StrList(IDictionary<string, object> args, string name)
        {
            if (args == null || !args.TryGetValue(name, out var raw) || raw == null)
            {
                return null;
            }

            IEnumerable<string> items;
            if (raw is JArray array)
            {
                items = array.Select(t => t.Type == JTokenType.String ? (string)t : t.ToString());
            }
            else if (raw is IEnumerable<object> seq && !(raw is string))
            {
                items = seq.Select(o => Convert.ToString(o, CultureInfo.InvariantCulture));
            }
            else
            {
                var text = ToolKit.Str(args, name);
                if (text == null)
                {
                    return new List<string>();
                }

                text = text.Trim();
                if (text.StartsWith("[", StringComparison.Ordinal))
                {
                    return StrList(new Dictionary<string, object> { { name, JArray.Parse(text) } }, name);
                }

                items = text.Split(',');
            }

            return items
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Parses a date argument. A value without an offset is taken as UTC, which is what
        /// EasyDNNNews stores in <c>PublishDate</c>.
        /// </summary>
        public static bool TryDate(IDictionary<string, object> args, string name, out DateTime utc)
        {
            utc = default;
            var text = ToolKit.Str(args, name);

            return text != null && DateTime.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out utc);
        }

        /// <summary>
        /// URL slug in the module's own style: lower case, hyphen-separated, ASCII only.
        /// </summary>
        public static string Slug(string text)
        {
            var normalized = (text ?? string.Empty).Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder();
            foreach (var c in normalized)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                {
                    sb.Append(c);
                }
            }

            var slug = Regex.Replace(sb.ToString().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
            if (slug.Length > 200)
            {
                slug = slug.Substring(0, 200).Trim('-');
            }

            return slug.Length == 0 ? "article" : slug;
        }

        /// <summary>
        /// The module stores Summary and Article HTML-encoded (<c>&amp;lt;p&amp;gt;</c>,
        /// apostrophes as <c>&amp;#39;</c>), which is exactly what
        /// <see cref="WebUtility.HtmlEncode"/> produces.
        /// </summary>
        public static string Encode(string html)
        {
            return html == null ? null : WebUtility.HtmlEncode(html);
        }

        public static string Decode(string stored)
        {
            return stored == null ? null : WebUtility.HtmlDecode(stored);
        }

        /// <summary>
        /// Plain text for <c>CleanArticleData</c>, which the module searches: tags removed,
        /// entities decoded, whitespace collapsed.
        /// </summary>
        public static string PlainText(string html)
        {
            if (string.IsNullOrEmpty(html))
            {
                return string.Empty;
            }

            var text = Regex.Replace(html, @"<(script|style)[^>]*>.*?</\1>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            text = Regex.Replace(text, "<[^>]+>", " ");
            text = WebUtility.HtmlDecode(text);

            return Regex.Replace(text, @"\s+", " ").Trim();
        }

        public static string Truncate(string text, int max)
        {
            return text == null || text.Length <= max ? text : text.Substring(0, max);
        }
    }
}
