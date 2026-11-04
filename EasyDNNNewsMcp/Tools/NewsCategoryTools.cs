using System;
using System.Collections.Generic;
using System.Linq;
using Dnn.Mcp.WebApi;
using Dnn.Mcp.WebApi.Models;
using Dnn.Mcp.WebApi.Services;
using DotNetNuke.Entities.Portals;
using Newtonsoft.Json;
using Satrabel.AIChat.Tools;

namespace KGroup.EasyDNNNewsMcp.Tools
{
    /// <summary>
    /// EasyDNNNews category tools: <c>get-news-categories</c> and <c>add-news-category</c>.
    /// </summary>
    /// <remarks>
    /// Categories are a per-portal tree in <c>EasyDNNNewsCategoryList</c>
    /// (<c>ParentCategory</c> 0 = root, <c>Level</c> 0-based, <c>Position</c> a portal-wide
    /// sort order). A news module whose <c>ShowAllCategories</c> setting is off only lists
    /// articles in the categories linked to it through <c>EasyDNNNewsModuleCategoryItems</c>,
    /// so a new category is invisible on such a module until it is linked there.
    /// </remarks>
    public class NewsCategoryTools : IMcpProvider
    {
        private const string Category = "EasyDNNNews";

        /// <inheritdoc />
        public void Register(IMcpRegistry registry)
        {
            registry.RegisterTool(new ToolDefinition
            {
                Name = "get-news-categories",
                Title = "Get news categories",
                Description =
                    "List the EasyDNNNews categories of the current portal as a tree, with article "
                    + "counts and which news modules display each category. Also lists the "
                    + "portal's news modules and whether each shows all categories.",
                Category = Category,
                ReadOnly = true,
                Parameters = new List<ToolParameter>(),
                Handler = ToolKit.Guard("get-news-categories", GetCategories),
            });

            registry.RegisterTool(new ToolDefinition
            {
                Name = "add-news-category",
                Title = "Add news category",
                Description =
                    "Create an EasyDNNNews category, optionally under a parent, and link it to news "
                    + "modules so their lists include it. Returns the existing category instead of "
                    + "creating a duplicate when one with the same name and parent exists. "
                    + "Requires portal administrator rights.",
                Category = Category,
                ReadOnly = false,
                Parameters = new List<ToolParameter>
                {
                    ToolKit.Param("name", "string", "Category name.", true),
                    ToolKit.Param("parentId", "int", "Parent category ID. Omit for a top-level category.", false),
                    ToolKit.Param(
                        "moduleIds",
                        "array",
                        "News module IDs to link the category to. Omit to link it to every news "
                        + "module on the portal that does not already show all categories.",
                        false),
                },
                Handler = ToolKit.Guard("add-news-category", AddCategory),
            });
        }

        internal class CategoryRow
        {
            public int CategoryID { get; set; }

            public string CategoryName { get; set; }

            public int? ParentCategory { get; set; }

            public int? Level { get; set; }

            public int? Position { get; set; }

            public int Articles { get; set; }
        }

        internal class ModuleCategoryRow
        {
            public int ModuleID { get; set; }

            public int CategoryID { get; set; }
        }

        internal static List<CategoryRow> LoadCategories(int portalId)
        {
            return NewsDb.Query<CategoryRow>(
                "SELECT l.CategoryID, l.CategoryName, l.ParentCategory, l.[Level], l.Position, "
                + "(SELECT COUNT(*) FROM {databaseOwner}[{objectQualifier}EasyDNNNewsCategories] c WHERE c.CategoryID = l.CategoryID) AS Articles "
                + "FROM {databaseOwner}[{objectQualifier}EasyDNNNewsCategoryList] l "
                + "WHERE l.PortalID = @0 ORDER BY l.Position, l.CategoryID",
                portalId);
        }

        /// <summary>
        /// Whether each module shows every category (true) or only its linked ones (false).
        /// A module with no settings row yet behaves as "show all" in the vendor UI.
        /// </summary>
        internal static Dictionary<int, bool> ShowAllCategories(IEnumerable<int> moduleIds)
        {
            var result = new Dictionary<int, bool>();
            foreach (var id in moduleIds)
            {
                var flag = NewsDb.Scalar<int?>(
                    "SELECT TOP 1 CAST(ShowAllCategories AS int) FROM {databaseOwner}[{objectQualifier}EasyDNNNewsModuleSettings] WHERE ModuleID = @0",
                    id);
                result[id] = flag != 0;
            }

            return result;
        }

        private static CallToolResult GetCategories(Dictionary<string, object> args)
        {
            var portalId = PortalSettings.Current.PortalId;
            var categories = LoadCategories(portalId);
            var modules = NewsDb.NewsModules(portalId);
            var showAll = ShowAllCategories(modules.Select(m => m.ModuleID));
            var links = LoadModuleLinks(modules.Select(m => m.ModuleID).ToList());

            var result = new
            {
                newsModules = modules.Select(m => new
                {
                    moduleId = m.ModuleID,
                    tabId = m.TabID,
                    title = m.ModuleTitle,
                    showAllCategories = showAll[m.ModuleID],
                }),
                categories = categories.Select(c => new
                {
                    id = c.CategoryID,
                    name = c.CategoryName,
                    parentId = c.ParentCategory.GetValueOrDefault() == 0 ? (int?)null : c.ParentCategory,
                    level = c.Level ?? 0,
                    articles = c.Articles,
                    shownInModules = modules
                        .Where(m => showAll[m.ModuleID] || links.Any(l => l.ModuleID == m.ModuleID && l.CategoryID == c.CategoryID))
                        .Select(m => m.ModuleID),
                }),
            };

            return ToolKit.Ok(JsonConvert.SerializeObject(result, Formatting.Indented));
        }

        private static List<ModuleCategoryRow> LoadModuleLinks(List<int> moduleIds)
        {
            if (moduleIds.Count == 0)
            {
                return new List<ModuleCategoryRow>();
            }

            // Module IDs come from ModuleController, never from the caller, so inlining
            // them is safe and avoids one round trip per module.
            return NewsDb.Query<ModuleCategoryRow>(
                "SELECT ModuleID, CategoryID FROM {databaseOwner}[{objectQualifier}EasyDNNNewsModuleCategoryItems] WHERE ModuleID IN ("
                + string.Join(",", moduleIds) + ")");
        }

        private static CallToolResult AddCategory(Dictionary<string, object> args)
        {
            var settings = PortalSettings.Current;
            if (!settings.UserInfo.IsAdmin)
            {
                return ToolKit.Fail("add-news-category requires a portal administrator.");
            }

            var name = ToolKit.Str(args, "name")?.Trim();
            if (name == null)
            {
                return ToolKit.Fail("name is required.");
            }

            if (name.Length > 200)
            {
                return ToolKit.Fail("name must be 200 characters or fewer.");
            }

            var portalId = settings.PortalId;
            var categories = LoadCategories(portalId);

            CategoryRow parent = null;
            if (ToolKit.TryInt(args, "parentId", out var parentId) && parentId > 0)
            {
                parent = categories.FirstOrDefault(c => c.CategoryID == parentId);
                if (parent == null)
                {
                    return ToolKit.Fail("Category " + parentId + " does not exist on this portal.");
                }
            }

            var modules = NewsDb.NewsModules(portalId);
            var showAll = ShowAllCategories(modules.Select(m => m.ModuleID));
            var requested = NewsDb.StrList(args, "moduleIds");
            List<int> targetModules;
            if (requested != null && requested.Count > 0)
            {
                targetModules = new List<int>();
                foreach (var raw in requested)
                {
                    if (!int.TryParse(raw, out var id) || modules.All(m => m.ModuleID != id))
                    {
                        return ToolKit.Fail("moduleIds: " + raw + " is not a news module on this portal.");
                    }

                    targetModules.Add(id);
                }
            }
            else
            {
                targetModules = modules.Where(m => !showAll[m.ModuleID]).Select(m => m.ModuleID).ToList();
            }

            var existing = categories.FirstOrDefault(c =>
                string.Equals(c.CategoryName, name, StringComparison.OrdinalIgnoreCase)
                && c.ParentCategory.GetValueOrDefault() == (parent?.CategoryID ?? 0));

            var created = existing == null;
            var categoryId = NewsDb.InTransaction(ctx =>
            {
                var id = existing?.CategoryID ?? NewsDb.Scalar<int>(
                    ctx,
                    "INSERT INTO {databaseOwner}[{objectQualifier}EasyDNNNewsCategoryList] "
                    + "(PortalID, CategoryName, Position, ParentCategory, [Level], Color, SearchableText) "
                    + "VALUES (@0, @1, (SELECT ISNULL(MAX(Position), 0) + 1 FROM {databaseOwner}[{objectQualifier}EasyDNNNewsCategoryList] WHERE PortalID = @0), @2, @3, N'default', @1); "
                    + "SELECT CAST(SCOPE_IDENTITY() AS int);",
                    portalId,
                    name,
                    parent?.CategoryID ?? 0,
                    parent == null ? 0 : (parent.Level ?? 0) + 1);

                foreach (var moduleId in targetModules)
                {
                    NewsDb.Exec(
                        ctx,
                        "IF NOT EXISTS (SELECT 1 FROM {databaseOwner}[{objectQualifier}EasyDNNNewsModuleCategoryItems] WHERE ModuleID = @0 AND CategoryID = @1) "
                        + "INSERT INTO {databaseOwner}[{objectQualifier}EasyDNNNewsModuleCategoryItems] (ModuleID, CategoryID) VALUES (@0, @1)",
                        moduleId,
                        id);
                }

                return id;
            });

            return ToolKit.Ok(
                (created ? "Created" : "Category already existed:") + " category " + categoryId + " '" + name + "'"
                + (parent == null ? string.Empty : " under " + parent.CategoryID + " '" + parent.CategoryName + "'")
                + ". Linked to news modules: " + (targetModules.Count == 0 ? "none needed (all modules show all categories)" : string.Join(", ", targetModules)) + ".");
        }
    }
}
