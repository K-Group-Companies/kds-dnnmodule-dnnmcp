using System.Collections.Generic;
using System.Linq;
using Dnn.Mcp.WebApi;
using Dnn.Mcp.WebApi.Models;
using Dnn.Mcp.WebApi.Services;
using DotNetNuke.Common.Utilities;
using DotNetNuke.Data;
using DotNetNuke.Entities.Modules;
using DotNetNuke.Entities.Portals;
using DotNetNuke.Entities.Tabs;
using Newtonsoft.Json.Linq;
using Satrabel.AIChat.Tools;

namespace KGroup.CookieConsentMcp.Tools
{
    /// <summary>
    /// Cookie Consent write tools: <c>place-cookie-consent</c>, <c>import-cookie-consent</c> and
    /// <c>set-cookie-consent</c>.
    /// </summary>
    /// <remarks>
    /// Writes go straight to the vendor's tables, then the vendor's cache and the module's
    /// output cache are cleared so the next page request renders the new configuration.
    /// Import only adds or updates rows by their natural key (category key, cookie name,
    /// cookie name plus match type); it never deletes, because the vendor seeds rows of its
    /// own that other rows may depend on. Managed scripts are not imported.
    /// </remarks>
    public class ConsentWriteTools : IMcpProvider
    {
        private static readonly string[] MatchTypes = { "exact", "prefix" };

        /// <summary>Each tag toggle and the ID it needs, so import can switch off what the target cannot load.</summary>
        private static readonly Dictionary<string, string> TagIds = new Dictionary<string, string>
        {
            { "googleTagManagerEnabled", "googleTagManagerContainerId" },
            { "googleAnalyticsEnabled", "googleAnalyticsMeasurementId" },
            { "googleAdSenseEnabled", "googleAdSensePublisherId" },
        };

        /// <inheritdoc />
        public void Register(IMcpRegistry registry)
        {
            registry.RegisterTool(new ToolDefinition
            {
                Name = "place-cookie-consent",
                Title = "Place Cookie Consent module",
                Description =
                    "Put the EasyDNN Cookie Consent module on every page of the current portal with its "
                    + "container hidden. Adds the module if there is none (default: home page, ContentPane), "
                    + "otherwise fixes the existing one. Load any page afterwards so the module creates its "
                    + "default settings before import-cookie-consent or set-cookie-consent.",
                Category = ConsentReadTools.Category,
                ReadOnly = false,
                Parameters = new List<ToolParameter>
                {
                    ToolKit.Param("tabId", "int", "Page to add the module to when there is none. Defaults to the home page.", false),
                    ToolKit.Param("paneName", "string", "Pane for a new module. Defaults to ContentPane.", false),
                },
                Handler = ToolKit.Guard("place-cookie-consent", Place),
            });

            registry.RegisterTool(new ToolDefinition
            {
                Name = "import-cookie-consent",
                Title = "Import Cookie Consent",
                Description =
                    "Apply a Cookie Consent configuration exported by export-cookie-consent (or by the "
                    + "vendor's own export) to the current portal: settings, categories, cookie catalog "
                    + "and cookie rules. Rows are added or updated, never deleted. Site-specific values "
                    + "(tracking IDs, policy URL) are not copied, consent debug is forced off, and a tag "
                    + "is switched off when this site has no ID for it yet. Policy pages are matched by "
                    + "page name unless their IDs are given. Managed scripts are skipped.",
                Category = ConsentReadTools.Category,
                ReadOnly = false,
                Parameters = new List<ToolParameter>
                {
                    ToolKit.Param("config", "object", "The exported configuration, as a JSON object or string.", true),
                    ToolKit.Param("privacyPolicyTabId", "int", "Privacy policy page on this site. Overrides the exported page name.", false),
                    ToolKit.Param("cookiePolicyTabId", "int", "Cookie policy page on this site. Overrides the exported page name.", false),
                },
                Handler = ToolKit.Guard("import-cookie-consent", Import),
            });

            registry.RegisterTool(new ToolDefinition
            {
                Name = "set-cookie-consent",
                Title = "Set Cookie Consent settings",
                Description =
                    "Change Cookie Consent settings on the current portal, e.g. "
                    + "{\"googleTagManagerEnabled\": true, \"googleTagManagerContainerId\": \"GTM-XXXXXXX\"}. "
                    + "Names are the camelCase fields shown by get-cookie-consent. Values are validated, "
                    + "and a change is refused if it would enable a tag without its ID or load GA4 both "
                    + "directly and through GTM.",
                Category = ConsentReadTools.Category,
                ReadOnly = false,
                Parameters = new List<ToolParameter>
                {
                    ToolKit.Param("settings", "object", "Setting names and new values.", true),
                },
                Handler = ToolKit.Guard("set-cookie-consent", Set),
            });
        }

        private static CallToolResult Place(Dictionary<string, object> args)
        {
            if (!ConsentReadTools.IsAdmin())
            {
                return ToolKit.Fail("Cookie Consent settings require a portal administrator.");
            }

            var portal = PortalSettings.Current;
            var placements = ConsentDb.Placements(portal.PortalId);
            var notes = new List<string>();
            ModuleInfo module;

            if (placements.Count == 0)
            {
                var tabId = ToolKit.TryInt(args, "tabId", out var requested) ? requested : portal.HomeTabId;
                var tab = TabController.Instance.GetTab(tabId, portal.PortalId);
                if (tab == null || tab.IsDeleted)
                {
                    return ToolKit.Fail("Page " + tabId + " was not found on this portal.");
                }

                module = AddModuleTool.AddModule(tabId, ConsentDb.ModuleName, ToolKit.Str(args, "paneName") ?? "ContentPane", "Cookie Consent");
                notes.Add("Added Cookie Consent module " + module.ModuleID + " to " + tab.TabName + ".");
            }
            else
            {
                var first = placements[0];
                module = ModuleController.Instance.GetModule(first.ModuleId, first.FirstTabId, false);
                notes.Add("Using existing Cookie Consent module " + module.ModuleID + ".");

                if (placements.Count > 1)
                {
                    notes.Add(
                        "Other Cookie Consent modules were left alone: "
                        + string.Join(", ", placements.Skip(1).Select(p => p.ModuleId))
                        + ". Delete them so the banner is not rendered twice.");
                }
            }

            // UpdateModule copies an AllTabs module onto every page it is not on yet.
            module.AllTabs = true;
            module.DisplayTitle = false;
            ModuleController.Instance.UpdateModule(module);

            // DisplayTitle is stored per page; pages the module was already on keep their own value.
            ConsentDb.InTransaction(ctx => ConsentDb.Exec(
                ctx,
                "UPDATE {databaseOwner}{objectQualifier}TabModules SET DisplayTitle = 0 WHERE ModuleID = @0",
                module.ModuleID));

            DataCache.ClearPortalCache(portal.PortalId, true);
            ConsentDb.ClearCache(portal.PortalId);

            var placed = ConsentDb.Placements(portal.PortalId).First(p => p.ModuleId == module.ModuleID);
            notes.Add("It is now on " + placed.PageCount + " pages with its container hidden.");

            if (ConsentDb.LoadSettings(portal.PortalId) == null)
            {
                notes.Add("Load any page of the site (e.g. get-url-html) so the module creates its default settings.");
            }

            return ToolKit.Ok(string.Join("\n", notes));
        }

        private static CallToolResult Set(Dictionary<string, object> args)
        {
            if (!ConsentReadTools.IsAdmin())
            {
                return ToolKit.Fail("Cookie Consent settings require a portal administrator.");
            }

            var portalId = PortalSettings.Current.PortalId;
            var input = ConsentDb.JsonObject(args, "settings");
            if (input == null || !input.Properties().Any())
            {
                return ToolKit.Fail("settings is required.");
            }

            var current = ConsentDb.LoadSettings(portalId);
            if (current == null)
            {
                return ToolKit.Fail(NoSettings);
            }

            var changes = new Dictionary<string, object>();
            var errors = new List<string>();
            foreach (var property in input.Properties())
            {
                var field = ConsentFields.Find(property.Name);
                if (field == null)
                {
                    errors.Add(property.Name + " is not a Cookie Consent setting that can be changed.");
                    continue;
                }

                if (ConsentFields.TryConvert(field, property.Value, out var value, out var error))
                {
                    changes[field.Name] = value;
                }
                else
                {
                    errors.Add(error);
                }
            }

            errors.AddRange(CheckTabs(changes, portalId));

            var merged = new Dictionary<string, object>(current, System.StringComparer.OrdinalIgnoreCase);
            foreach (var change in changes)
            {
                merged[change.Key] = change.Value;
            }

            if (errors.Count == 0)
            {
                errors.AddRange(ConsentFields.CrossCheck(merged));
            }

            if (errors.Count > 0)
            {
                return ToolKit.Fail("Nothing was changed.\n" + string.Join("\n", errors));
            }

            ConsentDb.InTransaction(ctx => ConsentDb.UpdateSettings(ctx, portalId, changes));
            ConsentDb.ClearCache(portalId);

            return ToolKit.Ok("Updated " + changes.Count + " Cookie Consent settings: " + string.Join(", ", changes.Keys) + ".");
        }

        private static CallToolResult Import(Dictionary<string, object> args)
        {
            if (!ConsentReadTools.IsAdmin())
            {
                return ToolKit.Fail("Cookie Consent settings require a portal administrator.");
            }

            var portalId = PortalSettings.Current.PortalId;
            var config = ConsentDb.JsonObject(args, "config");
            if (config == null)
            {
                return ToolKit.Fail("config is required.");
            }

            var module = (string)config["module"];
            if (module != null && module != ConsentDb.ModuleName)
            {
                return ToolKit.Fail("config is an export of " + module + ", not " + ConsentDb.ModuleName + ".");
            }

            var current = ConsentDb.LoadSettings(portalId);
            if (current == null)
            {
                return ToolKit.Fail(NoSettings);
            }

            var notes = new List<string>();
            var errors = new List<string>();
            var changes = new Dictionary<string, object>();
            var ignored = new List<string>();

            if (config["settings"] is JObject settings)
            {
                foreach (var property in settings.Properties())
                {
                    var field = ConsentFields.Find(property.Name);
                    if (field == null || field.SiteSpecific)
                    {
                        ignored.Add(property.Name);
                        continue;
                    }

                    if (ConsentFields.TryConvert(field, property.Value, out var value, out var error))
                    {
                        changes[field.Name] = value;
                    }
                    else
                    {
                        errors.Add(error);
                    }
                }
            }

            changes["googleConsentDebugEnabled"] = false;

            ResolvePolicyTab(args, config, "privacyPolicyTabId", "privacyPolicyPage", portalId, changes, notes);
            ResolvePolicyTab(args, config, "cookiePolicyTabId", "cookiePolicyPage", portalId, changes, notes);
            errors.AddRange(CheckTabs(changes, portalId));

            foreach (var tag in TagIds)
            {
                var on = changes.TryGetValue(tag.Key, out var v) && v is bool flag && flag;
                if (on && string.IsNullOrWhiteSpace(current[tag.Value] as string))
                {
                    changes[tag.Key] = false;
                    notes.Add(tag.Key + " was switched off because this site has no " + tag.Value + ". Set it with set-cookie-consent, then enable it.");
                }
            }

            var categories = Rows(config["categories"]);
            var catalog = Rows(config["cookieCatalog"]);
            var rules = Rows(config["cookieRules"]);

            var keys = new HashSet<string>(ConsentDb.Categories(portalId).Select(c => c.CategoryKey), System.StringComparer.OrdinalIgnoreCase);
            foreach (var row in categories)
            {
                var key = (string)row["categoryKey"];
                Require(errors, "category", key, 50, (string)row["name"], 200);
                if (!string.IsNullOrWhiteSpace(key))
                {
                    keys.Add(key);
                }
            }

            foreach (var row in catalog)
            {
                Require(errors, "cookie catalog entry", (string)row["cookieName"], 150, (string)row["categoryKey"], 50);
                CheckCategory(errors, keys, row, "cookie catalog entry");
            }

            foreach (var row in rules)
            {
                Require(errors, "cookie rule", (string)row["cookieName"], 150, (string)row["categoryKey"], 50);
                CheckCategory(errors, keys, row, "cookie rule");
                if (!MatchTypes.Contains((string)row["matchType"]))
                {
                    errors.Add("cookie rule " + row["cookieName"] + " has matchType " + row["matchType"] + "; use exact or prefix.");
                }
            }

            var merged = new Dictionary<string, object>(current, System.StringComparer.OrdinalIgnoreCase);
            foreach (var change in changes)
            {
                merged[change.Key] = change.Value;
            }

            if (errors.Count == 0)
            {
                errors.AddRange(ConsentFields.CrossCheck(merged));
            }

            if (errors.Count > 0)
            {
                return ToolKit.Fail("Nothing was imported.\n" + string.Join("\n", errors));
            }

            var counts = new int[6];
            ConsentDb.InTransaction(ctx =>
            {
                ConsentDb.UpdateSettings(ctx, portalId, changes);
                foreach (var row in categories)
                {
                    counts[UpsertCategory(ctx, portalId, row)]++;
                }

                foreach (var row in catalog)
                {
                    counts[2 + UpsertCatalog(ctx, portalId, row)]++;
                }

                foreach (var row in rules)
                {
                    counts[4 + UpsertRule(ctx, portalId, row)]++;
                }
            });

            ConsentDb.ClearCache(portalId);

            notes.Insert(0, "Updated " + changes.Count + " settings.");
            notes.Insert(1, "Categories: " + counts[1] + " added, " + counts[0] + " updated.");
            notes.Insert(2, "Cookie catalog: " + counts[3] + " added, " + counts[2] + " updated.");
            notes.Insert(3, "Cookie rules: " + counts[5] + " added, " + counts[4] + " updated.");

            var extraCatalog = ConsentDb.Catalog(portalId).Select(c => c.CookieName)
                .Except(catalog.Select(r => (string)r["cookieName"]), System.StringComparer.OrdinalIgnoreCase).ToList();
            if (extraCatalog.Count > 0)
            {
                notes.Add("Left in place, not in the import: cookie catalog " + string.Join(", ", extraCatalog) + ".");
            }

            var siteSpecific = ignored.Where(n => ConsentFields.Find(n) != null).ToList();
            if (siteSpecific.Count > 0)
            {
                notes.Add("Not copied (site-specific): " + string.Join(", ", siteSpecific) + ".");
            }

            if (config["managedScripts"] is JArray scripts && scripts.Count > 0)
            {
                notes.Add("Skipped " + scripts.Count + " managed scripts; add any this site needs in the module's settings.");
            }

            return ToolKit.Ok(string.Join("\n", notes));
        }

        private const string NoSettings =
            "Cookie Consent has no settings on this portal yet. Run place-cookie-consent, then load any page "
            + "of the site so the module creates its defaults.";

        private static List<JObject> Rows(JToken token)
        {
            return token is JArray array ? array.OfType<JObject>().ToList() : new List<JObject>();
        }

        private static void Require(List<string> errors, string what, string key, int keyLength, string second, int secondLength)
        {
            if (string.IsNullOrWhiteSpace(key) || key.Length > keyLength || string.IsNullOrWhiteSpace(second) || second.Length > secondLength)
            {
                errors.Add("A " + what + " (" + (key ?? "no key") + ") is missing a required value or one is too long.");
            }
        }

        private static void CheckCategory(List<string> errors, HashSet<string> keys, JObject row, string what)
        {
            var key = (string)row["categoryKey"];
            if (!string.IsNullOrWhiteSpace(key) && !keys.Contains(key))
            {
                errors.Add(what + " " + row["cookieName"] + " uses category " + key + ", which does not exist.");
            }
        }

        /// <summary>
        /// Takes a policy page from the tool argument, else matches the exported page name on
        /// this portal. Exported page IDs are never used: they belong to the source site.
        /// </summary>
        private static void ResolvePolicyTab(Dictionary<string, object> args, JObject config, string field, string pageProperty, int portalId, Dictionary<string, object> changes, List<string> notes)
        {
            if (ToolKit.TryInt(args, field, out var tabId))
            {
                changes[field] = tabId;
                return;
            }

            var name = (string)config[pageProperty];
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            var tab = TabController.Instance.GetTabByName(name, portalId);
            if (tab == null || tab.IsDeleted)
            {
                notes.Add("No page named \"" + name + "\" for " + field + "; it was left unchanged.");
                return;
            }

            changes[field] = tab.TabID;
        }

        private static IEnumerable<string> CheckTabs(Dictionary<string, object> changes, int portalId)
        {
            foreach (var field in new[] { "privacyPolicyTabId", "cookiePolicyTabId" })
            {
                if (changes.TryGetValue(field, out var value) && value is int tabId)
                {
                    var tab = TabController.Instance.GetTab(tabId, portalId);
                    if (tab == null || tab.IsDeleted)
                    {
                        yield return field + " " + tabId + " is not a page on this portal.";
                    }
                }
            }
        }

        /// <returns>0 when an existing row was updated, 1 when one was added.</returns>
        private static int UpsertCategory(IDataContext ctx, int portalId, JObject row)
        {
            return ConsentDb.Scalar<int>(
                ctx,
                "IF EXISTS (SELECT 1 FROM " + ConsentDb.Table + "Categories WHERE PortalId = @0 AND CategoryKey = @1) "
                + "BEGIN UPDATE " + ConsentDb.Table + "Categories SET Name = @2, Description = @3, IsRequired = @4, "
                + "Enabled = @5, SortOrder = @6, UpdatedOnUtc = GETUTCDATE() WHERE PortalId = @0 AND CategoryKey = @1; SELECT 0 END "
                + "ELSE BEGIN INSERT INTO " + ConsentDb.Table + "Categories (PortalId, CategoryKey, Name, Description, "
                + "IsRequired, Enabled, SortOrder, CreatedOnUtc, UpdatedOnUtc) "
                + "VALUES (@0, @1, @2, @3, @4, @5, @6, GETUTCDATE(), GETUTCDATE()); SELECT 1 END",
                portalId,
                (string)row["categoryKey"],
                (string)row["name"],
                (string)row["description"],
                row.Value<bool?>("isRequired") ?? false,
                row.Value<bool?>("enabled") ?? true,
                row.Value<int?>("sortOrder") ?? 0);
        }

        private static int UpsertCatalog(IDataContext ctx, int portalId, JObject row)
        {
            return ConsentDb.Scalar<int>(
                ctx,
                "IF EXISTS (SELECT 1 FROM " + ConsentDb.Table + "CookieCatalog WHERE PortalId = @0 AND CookieName = @1) "
                + "BEGIN UPDATE " + ConsentDb.Table + "CookieCatalog SET Provider = @2, Purpose = @3, Duration = @4, "
                + "CategoryKey = @5, IsThirdParty = @6, ProviderPolicyUrl = @7, Description = @8, Enabled = @9, "
                + "SortOrder = @10, UpdatedOnUtc = GETUTCDATE() WHERE PortalId = @0 AND CookieName = @1; SELECT 0 END "
                + "ELSE BEGIN INSERT INTO " + ConsentDb.Table + "CookieCatalog (PortalId, CookieName, Provider, Purpose, "
                + "Duration, CategoryKey, IsThirdParty, ProviderPolicyUrl, Description, Enabled, SortOrder, CreatedOnUtc, UpdatedOnUtc) "
                + "VALUES (@0, @1, @2, @3, @4, @5, @6, @7, @8, @9, @10, GETUTCDATE(), GETUTCDATE()); SELECT 1 END",
                portalId,
                (string)row["cookieName"],
                (string)row["provider"],
                (string)row["purpose"],
                (string)row["duration"],
                (string)row["categoryKey"],
                row.Value<bool?>("isThirdParty") ?? false,
                (string)row["providerPolicyUrl"],
                (string)row["description"],
                row.Value<bool?>("enabled") ?? true,
                row.Value<int?>("sortOrder") ?? 100);
        }

        private static int UpsertRule(IDataContext ctx, int portalId, JObject row)
        {
            return ConsentDb.Scalar<int>(
                ctx,
                "IF EXISTS (SELECT 1 FROM " + ConsentDb.Table + "CookieRules WHERE PortalId = @0 AND CookieName = @1 AND MatchType = @2) "
                + "BEGIN UPDATE " + ConsentDb.Table + "CookieRules SET CategoryKey = @3, Path = @4, Domain = @5, HttpOnly = @6, "
                + "Secure = @7, DeleteOnReject = @8, DeleteOnWithdraw = @9, DeleteWhenCategoryDisabled = @10, Enabled = @11, "
                + "Description = @12, SortOrder = @13, UpdatedOnUtc = GETUTCDATE() "
                + "WHERE PortalId = @0 AND CookieName = @1 AND MatchType = @2; SELECT 0 END "
                + "ELSE BEGIN INSERT INTO " + ConsentDb.Table + "CookieRules (PortalId, CookieName, MatchType, CategoryKey, Path, "
                + "Domain, HttpOnly, Secure, DeleteOnReject, DeleteOnWithdraw, DeleteWhenCategoryDisabled, Enabled, Description, "
                + "SortOrder, CreatedOnUtc, UpdatedOnUtc) "
                + "VALUES (@0, @1, @2, @3, @4, @5, @6, @7, @8, @9, @10, @11, @12, @13, GETUTCDATE(), GETUTCDATE()); SELECT 1 END",
                portalId,
                (string)row["cookieName"],
                (string)row["matchType"],
                (string)row["categoryKey"],
                (string)row["path"],
                (string)row["domain"],
                row.Value<bool?>("httpOnly") ?? false,
                row.Value<bool?>("secure") ?? false,
                row.Value<bool?>("deleteOnReject") ?? true,
                row.Value<bool?>("deleteOnWithdraw") ?? true,
                row.Value<bool?>("deleteWhenCategoryDisabled") ?? true,
                row.Value<bool?>("enabled") ?? true,
                (string)row["description"],
                row.Value<int?>("sortOrder") ?? 0);
        }
    }
}
