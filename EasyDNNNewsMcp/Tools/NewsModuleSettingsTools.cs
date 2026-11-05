using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Dnn.Mcp.WebApi;
using Dnn.Mcp.WebApi.Models;
using Dnn.Mcp.WebApi.Services;
using DotNetNuke.Data;
using DotNetNuke.Entities.Portals;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Satrabel.AIChat.Tools;

namespace KGroup.EasyDNNNewsMcp.Tools
{
    /// <summary>
    /// EasyDNNNews module settings tools: <c>get-news-module-settings</c>,
    /// <c>set-news-module-settings</c> and <c>copy-news-module-settings</c>.
    /// </summary>
    /// <remarks>
    /// The module keeps its settings outside DNN's ModuleSettings, in one wide row per module
    /// in <c>EasyDNNNewsModuleSettings</c> (about 300 columns, keyed by <c>ModuleID</c>), plus
    /// per-module side tables: linked categories, groups and authors, the searchable flag, and
    /// per-role rights in <c>EasyDNNNewsRolePremissionSettings</c> with two category child
    /// tables keyed by that table's identity. Column names differ between vendor releases, so
    /// every column list here is read from <c>sys.columns</c> at call time rather than
    /// hard-coded; a copy between two modules on the same install always lines up.
    ///
    /// A module without role rights rows denies article views to everyone but hosts and
    /// administrators ("Login to view article"), which is why a copy brings them along by
    /// default.
    /// </remarks>
    public class NewsModuleSettingsTools : IMcpProvider
    {
        private const string Category = "EasyDNNNews";
        private const string SettingsTable = "EasyDNNNewsModuleSettings";
        private const string RightsTable = "EasyDNNNewsRolePremissionSettings";
        private const string RightsKey = "PremissionSettingsID";

        /// <summary>Side tables keyed by ModuleID that hold content filters (copied with categories).</summary>
        private static readonly string[] FilterTables =
        {
            "EasyDNNNewsModuleCategoryItems",
            "EasyDNNNewsModuleGroupItems",
            "EasyDNNNewsModuleAuthorsItems",
        };

        /// <summary>Child tables of the role rights rows, keyed by <see cref="RightsKey"/>.</summary>
        private static readonly string[] RightsChildTables =
        {
            "EasyDNNNewsRolePremissionsShowCategories",
            "EasyDNNNewsRolePremissionsAddToCategories",
        };

        /// <summary>Settings columns holding module, tab or portal IDs; a copy keeps them verbatim.</summary>
        private static readonly string[] IdColumns =
        {
            "ModuleToOpenUserActivity", "ModuleToOpenGroupActivity", "ModuleToOpenTwitterDetails",
            "ModuleToOpenFacebookDetails", "ModuleToOpenLinkedInDetails", "CanonicalLinksNewsModuleId",
            "CanonicalLinksNewsTabId", "SimpleForumModuleIdToOpenTopic", "SimpleForumTabIdToOpenTopic",
            "SharingPortalID", "ModuleBoundToCategory", "GalleryCategoryID", "OrderByCustomFieldID", "WorkflowId",
        };

        /// <inheritdoc />
        public void Register(IMcpRegistry registry)
        {
            registry.RegisterTool(new ToolDefinition
            {
                Name = "get-news-module-settings",
                Title = "Get news module settings",
                Description =
                    "Read an EasyDNNNews module's settings (the module's own settings row, about 300 "
                    + "columns), its linked categories and its per-role rights. Pass filter to "
                    + "return only columns whose name contains that text (e.g. 'Image', 'Paging'). "
                    + "Values are returned with their column names, ready to send back to "
                    + "set-news-module-settings.",
                Category = Category,
                ReadOnly = true,
                Parameters = new List<ToolParameter>
                {
                    ToolKit.Param("moduleId", "int", "News module ID. Optional when the portal has only one.", false),
                    ToolKit.Param("filter", "string", "Only columns whose name contains this text (case-insensitive).", false),
                    ToolKit.Param("columns", "array", "Only these exact column names.", false),
                },
                Handler = ToolKit.Guard("get-news-module-settings", GetSettings),
            });

            registry.RegisterTool(new ToolDefinition
            {
                Name = "set-news-module-settings",
                Title = "Set news module settings",
                Description =
                    "Change columns of an EasyDNNNews module's settings row. settings is an object "
                    + "of column name to value, using the names get-news-module-settings returns "
                    + "(bit columns take true/false). Unknown columns, ModuleID and values of the "
                    + "wrong type are rejected and nothing is written. Returns each column's old "
                    + "and new value. Requires portal administrator rights.",
                Category = Category,
                ReadOnly = false,
                Parameters = new List<ToolParameter>
                {
                    ToolKit.Param("moduleId", "int", "News module ID. Optional when the portal has only one.", false),
                    ToolKit.Param("settings", "object", "Column name to new value, e.g. {\"EnableWebpImages\": true}.", true),
                },
                Handler = ToolKit.Guard("set-news-module-settings", SetSettings),
            });

            registry.RegisterTool(new ToolDefinition
            {
                Name = "copy-news-module-settings",
                Title = "Copy news module settings",
                Description =
                    "Copy one EasyDNNNews module's settings onto another news module on the same "
                    + "portal, replacing the target's: the settings row, and optionally its linked "
                    + "categories, groups and authors, and its per-role rights. Use it to give a "
                    + "new blog module the house settings of an existing one. Settings that point "
                    + "at other modules or pages are copied as-is and listed in the result. "
                    + "Requires portal administrator rights.",
                Category = Category,
                ReadOnly = false,
                Parameters = new List<ToolParameter>
                {
                    ToolKit.Param("sourceModuleId", "int", "News module to copy from.", true),
                    ToolKit.Param("targetModuleId", "int", "News module to copy onto.", true),
                    ToolKit.Param("includeCategories", "bool", "Also replace the linked categories, groups and authors. Default true.", false),
                    ToolKit.Param("includePermissions", "bool", "Also replace the per-role rights. Default true.", false),
                },
                Handler = ToolKit.Guard("copy-news-module-settings", CopySettings),
            });
        }

        internal class ColumnRow
        {
            public string Name { get; set; }

            public string TypeName { get; set; }

            public short MaxLength { get; set; }

            public bool IsNullable { get; set; }

            public bool IsIdentity { get; set; }
        }

        internal class CategoryLink
        {
            public int CategoryID { get; set; }

            public string CategoryName { get; set; }
        }

        private static string Table(string name)
        {
            return "{databaseOwner}[{objectQualifier}" + name + "]";
        }

        /// <summary>The table's columns, or an empty list when this vendor release lacks it.</summary>
        private static List<ColumnRow> Columns(string table)
        {
            return NewsDb.Query<ColumnRow>(
                "SELECT c.name AS Name, t.name AS TypeName, c.max_length AS MaxLength, c.is_nullable AS IsNullable, c.is_identity AS IsIdentity "
                + "FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id "
                + "WHERE c.object_id = OBJECT_ID(@0) ORDER BY c.column_id",
                NewsDb.Sql(Table(table)));
        }

        private static string ColumnList(IEnumerable<ColumnRow> columns)
        {
            return string.Join(", ", columns.Select(c => "[" + c.Name + "]"));
        }

        /// <summary>
        /// One settings row as JSON. FOR JSON is wrapped in a scalar subquery because SQL Server
        /// streams a bare FOR JSON result in ~2 KB rows, and ExecuteScalar would read only the
        /// first. Column names come from sys.columns, so bracketing them is enough.
        /// </summary>
        private static JObject ReadRow(string table, int moduleId, IEnumerable<ColumnRow> columns)
        {
            var json = NewsDb.Scalar<string>(
                "SELECT (SELECT " + ColumnList(columns) + " FROM " + Table(table)
                + " WHERE ModuleID = @0 FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES)",
                moduleId);

            return string.IsNullOrEmpty(json) ? null : JObject.Parse(json);
        }

        private static CallToolResult GetSettings(Dictionary<string, object> args)
        {
            var portalId = PortalSettings.Current.PortalId;
            var module = NewsDb.ResolveNewsModule(portalId, ToolKit.TryInt(args, "moduleId", out var id) ? id : (int?)null, out var error);
            if (module == null)
            {
                return ToolKit.Fail(error);
            }

            var columns = Columns(SettingsTable);
            var filter = ToolKit.Str(args, "filter");
            var wanted = NewsDb.StrList(args, "columns");
            if (wanted != null && wanted.Count > 0)
            {
                var unknown = wanted.Where(w => columns.All(c => !c.Name.Equals(w, StringComparison.OrdinalIgnoreCase))).ToList();
                if (unknown.Count > 0)
                {
                    return ToolKit.Fail("Unknown settings columns: " + string.Join(", ", unknown) + ".");
                }

                columns = columns.Where(c => wanted.Contains(c.Name, StringComparer.OrdinalIgnoreCase)).ToList();
            }

            if (filter != null)
            {
                columns = columns.Where(c => c.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            }

            if (columns.Count == 0)
            {
                return ToolKit.Fail("No settings columns match.");
            }

            var row = ReadRow(SettingsTable, module.ModuleID, columns);

            var categories = NewsDb.Query<CategoryLink>(
                "SELECT i.CategoryID, l.CategoryName FROM " + Table("EasyDNNNewsModuleCategoryItems") + " i "
                + "LEFT JOIN " + Table("EasyDNNNewsCategoryList") + " l ON l.CategoryID = i.CategoryID "
                + "WHERE i.ModuleID = @0 ORDER BY l.Position",
                module.ModuleID);

            var result = new
            {
                moduleId = module.ModuleID,
                tabId = module.TabID,
                title = module.ModuleTitle,
                hasSettingsRow = row != null,
                settings = row,
                linkedCategories = categories.Select(c => new { id = c.CategoryID, name = c.CategoryName }),
                roleRights = ReadRights(module.ModuleID),
            };

            return ToolKit.Ok(JsonConvert.SerializeObject(result, Formatting.Indented));
        }

        /// <summary>
        /// The module's per-role rights, each row reduced to its role and the rights it grants
        /// (false and zero values dropped) so eight rows of ~50 flags stay readable.
        /// </summary>
        private static List<JObject> ReadRights(int moduleId)
        {
            var json = NewsDb.Scalar<string>(
                // FOR JSON drops NULL columns, and EasyDNN stores the anonymous-visitor row
                // with a NULL RoleID, so name that row explicitly.
                "SELECT (SELECT p.*, ISNULL(r.RoleName, CASE WHEN p.RoleID IS NULL "
                + "THEN 'Unauthenticated Users (RoleID null)' END) AS RoleName FROM " + Table(RightsTable) + " p "
                + "LEFT JOIN " + Table("Roles") + " r ON r.RoleID = p.RoleID "
                + "WHERE p.ModuleID = @0 ORDER BY p." + RightsKey + " FOR JSON PATH)",
                moduleId);

            if (string.IsNullOrEmpty(json))
            {
                return new List<JObject>();
            }

            return JArray.Parse(json).OfType<JObject>().Select(row =>
            {
                var compact = new JObject();
                foreach (var prop in row.Properties())
                {
                    var keep = prop.Name == "RoleID" || prop.Name == "RoleName"
                        || (prop.Value.Type == JTokenType.Boolean && (bool)prop.Value)
                        || (prop.Value.Type == JTokenType.Integer && (long)prop.Value != 0
                            && prop.Name != RightsKey && prop.Name != "ModuleID" && prop.Name != "PortalID");
                    if (keep)
                    {
                        compact[prop.Name] = prop.Value;
                    }
                }

                return compact;
            }).ToList();
        }

        private static CallToolResult SetSettings(Dictionary<string, object> args)
        {
            var portal = PortalSettings.Current;
            if (!portal.UserInfo.IsAdmin)
            {
                return ToolKit.Fail("set-news-module-settings requires a portal administrator.");
            }

            var module = NewsDb.ResolveNewsModule(portal.PortalId, ToolKit.TryInt(args, "moduleId", out var id) ? id : (int?)null, out var error);
            if (module == null)
            {
                return ToolKit.Fail(error);
            }

            JObject requested;
            try
            {
                requested = ToJObject(args.TryGetValue("settings", out var raw) ? raw : null);
            }
            catch (JsonException ex)
            {
                return ToolKit.Fail("settings is not a JSON object: " + ex.Message);
            }

            if (requested == null || !requested.HasValues)
            {
                return ToolKit.Fail("settings is required: an object of column name to value.");
            }

            var columns = Columns(SettingsTable);
            var updates = new List<KeyValuePair<ColumnRow, object>>();
            var problems = new List<string>();
            foreach (var prop in requested.Properties())
            {
                var column = columns.FirstOrDefault(c => c.Name.Equals(prop.Name, StringComparison.OrdinalIgnoreCase));
                if (column == null)
                {
                    problems.Add(prop.Name + ": no such column");
                    continue;
                }

                if (column.Name == "ModuleID")
                {
                    problems.Add("ModuleID cannot be changed");
                    continue;
                }

                if (TryConvert(prop.Value, column, out var value, out var why))
                {
                    updates.Add(new KeyValuePair<ColumnRow, object>(column, value));
                }
                else
                {
                    problems.Add(column.Name + ": " + why);
                }
            }

            if (problems.Count > 0)
            {
                return ToolKit.Fail("Nothing was written. " + string.Join("; ", problems) + ".");
            }

            var before = ReadRow(SettingsTable, module.ModuleID, updates.Select(u => u.Key));
            if (before == null)
            {
                return ToolKit.Fail(
                    "Module " + module.ModuleID + " has no settings row yet. Open its Settings page once, "
                    + "or use copy-news-module-settings from a configured news module.");
            }

            var sql = "UPDATE " + Table(SettingsTable) + " SET "
                + string.Join(", ", updates.Select((u, i) => "[" + u.Key.Name + "] = @" + (i + 1)))
                + " WHERE ModuleID = @0";
            var values = new List<object> { module.ModuleID };
            values.AddRange(updates.Select(u => u.Value));
            NewsDb.Exec(sql, values.ToArray());

            NewsDb.ClearModuleCache();
            var after = ReadRow(SettingsTable, module.ModuleID, updates.Select(u => u.Key));

            var changes = updates.Select(u => new
            {
                column = u.Key.Name,
                from = before[u.Key.Name],
                to = after[u.Key.Name],
            });

            return ToolKit.Ok(
                "Updated module " + module.ModuleID + " ('" + module.ModuleTitle + "'):\n"
                + JsonConvert.SerializeObject(changes, Formatting.Indented));
        }

        private static CallToolResult CopySettings(Dictionary<string, object> args)
        {
            var portal = PortalSettings.Current;
            if (!portal.UserInfo.IsAdmin)
            {
                return ToolKit.Fail("copy-news-module-settings requires a portal administrator.");
            }

            if (!ToolKit.TryInt(args, "sourceModuleId", out var sourceId) || !ToolKit.TryInt(args, "targetModuleId", out var targetId))
            {
                return ToolKit.Fail("sourceModuleId and targetModuleId are required.");
            }

            if (sourceId == targetId)
            {
                return ToolKit.Fail("sourceModuleId and targetModuleId must differ.");
            }

            var source = NewsDb.ResolveNewsModule(portal.PortalId, sourceId, out var error);
            var target = source == null ? null : NewsDb.ResolveNewsModule(portal.PortalId, targetId, out error);
            if (source == null || target == null)
            {
                return ToolKit.Fail(error);
            }

            var includeCategories = ToolKit.Bool(args, "includeCategories", true);
            var includePermissions = ToolKit.Bool(args, "includePermissions", true);

            var settingsColumns = Columns(SettingsTable).Where(c => c.Name != "ModuleID").ToList();
            var sourceRow = ReadRow(SettingsTable, source.ModuleID, settingsColumns);
            if (sourceRow == null)
            {
                return ToolKit.Fail("Module " + source.ModuleID + " has no settings row to copy.");
            }

            var copied = new List<string>();
            NewsDb.InTransaction(ctx =>
            {
                ReplaceByModule(ctx, SettingsTable, settingsColumns, source.ModuleID, target.ModuleID);
                copied.Add("settings");

                ReplaceByModule(ctx, "EasyDNNNewsModuleSearchable", null, source.ModuleID, target.ModuleID);

                if (includeCategories)
                {
                    foreach (var table in FilterTables)
                    {
                        copied.Add(table.Substring("EasyDNNNewsModule".Length) + " " + ReplaceByModule(ctx, table, null, source.ModuleID, target.ModuleID));
                    }
                }

                if (includePermissions)
                {
                    copied.Add("role rights " + ReplaceRights(ctx, source.ModuleID, target.ModuleID));
                }

                return 0;
            });

            NewsDb.ClearModuleCache();

            var idSettings = IdColumns
                // 0 and -1 both mean "not set" in EasyDNN, so only real IDs are worth a warning.
                .Where(c => sourceRow[c] != null && sourceRow[c].Type == JTokenType.Integer && (long)sourceRow[c] > 0)
                .Select(c => c + "=" + sourceRow[c])
                .ToList();

            return ToolKit.Ok(
                "Copied module " + source.ModuleID + " ('" + source.ModuleTitle + "') onto module "
                + target.ModuleID + " ('" + target.ModuleTitle + "', page " + target.TabID + "): "
                + string.Join(", ", copied) + "."
                + (idSettings.Count == 0
                    ? string.Empty
                    : " Settings that reference other modules/pages were copied unchanged; check they suit the target: " + string.Join(", ", idSettings) + "."));
        }

        /// <summary>
        /// Replaces the target module's rows in a ModuleID-keyed table with the source's.
        /// Returns the number of rows copied (0 when the table does not exist in this release).
        /// </summary>
        private static int ReplaceByModule(IDataContext ctx, string table, List<ColumnRow> columns, int sourceId, int targetId)
        {
            columns = columns ?? Columns(table).Where(c => c.Name != "ModuleID" && !c.IsIdentity).ToList();
            if (columns.Count == 0 && Columns(table).Count == 0)
            {
                return 0;
            }

            NewsDb.Exec(ctx, "DELETE FROM " + Table(table) + " WHERE ModuleID = @0", targetId);
            var list = columns.Count == 0 ? string.Empty : ", " + ColumnList(columns);

            return NewsDb.Scalar<int>(
                ctx,
                "INSERT INTO " + Table(table) + " (ModuleID" + list + ") SELECT @1" + list + " FROM " + Table(table)
                + " WHERE ModuleID = @0; SELECT CAST(ROWCOUNT_BIG() AS int);", // not @@ROWCOUNT: PetaPoco unescapes @@ to @
                sourceId,
                targetId);
        }

        /// <summary>
        /// Replaces the target's role rights. Each rights row gets a new identity, so its
        /// category child rows are copied one parent at a time with the new key.
        /// </summary>
        private static int ReplaceRights(IDataContext ctx, int sourceId, int targetId)
        {
            var rightsColumns = Columns(RightsTable).Where(c => c.Name != "ModuleID" && !c.IsIdentity).ToList();
            var children = RightsChildTables
                .Select(t => new { Table = t, Columns = Columns(t).Where(c => c.Name != RightsKey && !c.IsIdentity).ToList() })
                .Where(t => t.Columns.Count > 0)
                .ToList();

            foreach (var child in children)
            {
                NewsDb.Exec(
                    ctx,
                    "DELETE FROM " + Table(child.Table) + " WHERE " + RightsKey + " IN (SELECT " + RightsKey + " FROM " + Table(RightsTable) + " WHERE ModuleID = @0)",
                    targetId);
            }

            NewsDb.Exec(ctx, "DELETE FROM " + Table(RightsTable) + " WHERE ModuleID = @0", targetId);

            var sourceKeys = ctx.ExecuteQuery<int>(
                System.Data.CommandType.Text,
                NewsDb.Sql("SELECT " + RightsKey + " FROM " + Table(RightsTable) + " WHERE ModuleID = @0 ORDER BY " + RightsKey),
                sourceId).ToList();

            var list = ColumnList(rightsColumns);
            foreach (var oldKey in sourceKeys)
            {
                var newKey = NewsDb.Scalar<int>(
                    ctx,
                    "INSERT INTO " + Table(RightsTable) + " (ModuleID, " + list + ") SELECT @1, " + list + " FROM " + Table(RightsTable)
                    + " WHERE " + RightsKey + " = @0; SELECT CAST(SCOPE_IDENTITY() AS int);",
                    oldKey,
                    targetId);

                foreach (var child in children)
                {
                    var childList = ColumnList(child.Columns);
                    NewsDb.Exec(
                        ctx,
                        "INSERT INTO " + Table(child.Table) + " (" + RightsKey + ", " + childList + ") SELECT @1, " + childList
                        + " FROM " + Table(child.Table) + " WHERE " + RightsKey + " = @0",
                        oldKey,
                        newKey);
                }
            }

            return sourceKeys.Count;
        }

        private static JObject ToJObject(object raw)
        {
            switch (raw)
            {
                case null:
                    return null;
                case JObject j:
                    return j;
                case string s:
                    return string.IsNullOrWhiteSpace(s) ? null : JObject.Parse(s);
                default:
                    return JObject.FromObject(raw);
            }
        }

        /// <summary>Converts a JSON value to the column's SQL type, or says why it cannot.</summary>
        private static bool TryConvert(JToken token, ColumnRow column, out object value, out string why)
        {
            value = null;
            why = null;
            if (token == null || token.Type == JTokenType.Null)
            {
                if (column.IsNullable)
                {
                    return true;
                }

                why = "cannot be null";
                return false;
            }

            var text = token.Type == JTokenType.String ? (string)token : token.ToString(Formatting.None);
            switch (column.TypeName)
            {
                case "bit":
                    if (token.Type == JTokenType.Boolean)
                    {
                        value = (bool)token;
                        return true;
                    }

                    if (text == "1" || text == "0" || bool.TryParse(text, out _))
                    {
                        value = text == "1" || (text != "0" && bool.Parse(text));
                        return true;
                    }

                    why = "expects true or false";
                    return false;

                case "tinyint":
                case "smallint":
                case "int":
                case "bigint":
                    var (min, max) = column.TypeName == "tinyint" ? (0L, 255L)
                        : column.TypeName == "smallint" ? (short.MinValue, short.MaxValue)
                        : column.TypeName == "int" ? (int.MinValue, int.MaxValue)
                        : (long.MinValue, long.MaxValue);
                    if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= min && n <= max)
                    {
                        value = column.TypeName == "bigint" ? n : (object)(int)n;
                        return true;
                    }

                    why = "expects a whole number from " + min + " to " + max;
                    return false;

                case "nvarchar":
                case "varchar":
                case "nchar":
                case "char":
                    var limit = column.MaxLength < 0 ? int.MaxValue : column.TypeName.StartsWith("n", StringComparison.Ordinal) ? column.MaxLength / 2 : column.MaxLength;
                    if (text.Length > limit)
                    {
                        why = "longer than " + limit + " characters";
                        return false;
                    }

                    value = text;
                    return true;

                case "datetime":
                case "datetime2":
                case "date":
                    if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d))
                    {
                        value = d;
                        return true;
                    }

                    why = "expects an ISO 8601 date";
                    return false;

                default:
                    why = "column type " + column.TypeName + " is not supported here";
                    return false;
            }
        }
    }
}
