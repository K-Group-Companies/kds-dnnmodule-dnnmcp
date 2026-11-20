using System;
using System.Collections.Generic;
using System.Linq;
using Dnn.Mcp.WebApi;
using Dnn.Mcp.WebApi.Models;
using Dnn.Mcp.WebApi.Services;
using DotNetNuke.Entities.Portals;
using DotNetNuke.Entities.Tabs;
using Satrabel.AIChat.Tools;

namespace KGroup.CookieConsentMcp.Tools
{
    /// <summary>
    /// Read-only Cookie Consent tools: <c>get-cookie-consent</c> and <c>export-cookie-consent</c>.
    /// </summary>
    /// <remarks>
    /// Tracking IDs are masked, the encrypted OpenAI key is reported only as set or not set,
    /// and the consent log is reported as counts, never as individual records.
    /// </remarks>
    public class ConsentReadTools : IMcpProvider
    {
        internal const string Category = "Cookie Consent";

        private static readonly string[] MaskedFields =
        {
            "googleTagManagerContainerId",
            "googleAnalyticsMeasurementId",
            "googleAdSensePublisherId",
        };

        /// <inheritdoc />
        public void Register(IMcpRegistry registry)
        {
            registry.RegisterTool(new ToolDefinition
            {
                Name = "get-cookie-consent",
                Title = "Get Cookie Consent",
                Description =
                    "Show the EasyDNN Cookie Consent setup for the current portal: settings (tracking IDs "
                    + "masked), categories, cookie catalog and rule counts, where the module is placed, "
                    + "consent log counts, and warnings for common setup problems such as the module "
                    + "not being on every page, its container being shown, or DNN's own Google Tag "
                    + "Manager connector loading GTM without consent.",
                Category = Category,
                ReadOnly = true,
                Parameters = new List<ToolParameter>
                {
                    ToolKit.Param("detail", "boolean", "Also list every cookie catalog entry and cookie rule.", false),
                },
                Handler = ToolKit.Guard("get-cookie-consent", Get),
            });

            registry.RegisterTool(new ToolDefinition
            {
                Name = "export-cookie-consent",
                Title = "Export Cookie Consent",
                Description =
                    "Export the current portal's Cookie Consent configuration as JSON in the vendor's "
                    + "export format, ready for import-cookie-consent on another site. Site-specific "
                    + "values (tracking IDs, policy URL and page IDs) and managed scripts are left out; "
                    + "policy pages are exported by page name. Consent debug is always exported off.",
                Category = Category,
                ReadOnly = true,
                Parameters = new List<ToolParameter>(),
                Handler = ToolKit.Guard("export-cookie-consent", Export),
            });
        }

        /// <summary>
        /// Cookie Consent settings are site-wide configuration, so only portal administrators
        /// and hosts may read or change them.
        /// </summary>
        internal static bool IsAdmin()
        {
            var user = PortalSettings.Current?.UserInfo;
            return user != null && (user.IsSuperUser || user.IsAdmin);
        }

        internal static string TabName(int? tabId, int portalId)
        {
            if (tabId == null)
            {
                return null;
            }

            var tab = TabController.Instance.GetTab(tabId.Value, portalId);
            return tab == null || tab.IsDeleted ? null : tab.TabName;
        }

        private static CallToolResult Get(Dictionary<string, object> args)
        {
            if (!IsAdmin())
            {
                return ToolKit.Fail("Cookie Consent settings require a portal administrator.");
            }

            var portal = PortalSettings.Current;
            var portalId = portal.PortalId;
            var settings = ConsentDb.LoadSettings(portalId);
            var placements = ConsentDb.Placements(portalId);
            var connectorGtm = ConsentDb.ConnectorGtmId(portal);
            var warnings = new List<string>();

            if (placements.Count == 0)
            {
                warnings.Add("The Cookie Consent module is not on any page. Run place-cookie-consent.");
            }
            else
            {
                if (placements.Count > 1)
                {
                    warnings.Add("There are " + placements.Count + " Cookie Consent modules; keep one, shown on all pages.");
                }

                if (placements.Any(p => !p.AllTabs))
                {
                    warnings.Add("A Cookie Consent module is not set to display on all pages. Run place-cookie-consent.");
                }

                if (placements.Any(p => p.ContainerShownCount > 0))
                {
                    warnings.Add("The Cookie Consent module's container is shown on some pages. Run place-cookie-consent.");
                }
            }

            if (connectorGtm != null)
            {
                warnings.Add(
                    "DNN's Google Tag Manager connector is set (" + ConsentFields.Mask(connectorGtm) + "). It loads "
                    + "GTM on every page before consent. Clear it under Persona Bar > Settings > Connectors and "
                    + "set googleTagManagerContainerId here instead.");
            }

            object settingsOut = null;
            if (settings == null)
            {
                if (placements.Count > 0)
                {
                    warnings.Add("Cookie Consent has not created its settings yet. Load any page of the site once.");
                }
            }
            else
            {
                warnings.AddRange(ConsentFields.CrossCheck(settings));

                if (settings["googleConsentDebugEnabled"] is bool debug && debug)
                {
                    warnings.Add("googleConsentDebugEnabled is on. Turn it off for a live site.");
                }

                if (!(bool)settings["settingsSaved"])
                {
                    warnings.Add("The settings have never been saved, so the vendor treats them as defaults.");
                }

                if (settings["privacyPolicyTabId"] == null && settings["cookiePolicyTabId"] == null
                    && string.IsNullOrWhiteSpace(settings["policyUrl"] as string))
                {
                    warnings.Add("No privacy or cookie policy page is set.");
                }

                var shown = new Dictionary<string, object>(settings);
                foreach (var name in MaskedFields)
                {
                    shown[name] = ConsentFields.Mask(shown[name] as string);
                }

                shown["privacyPolicyPage"] = TabName(shown["privacyPolicyTabId"] as int?, portalId);
                shown["cookiePolicyPage"] = TabName(shown["cookiePolicyTabId"] as int?, portalId);
                settingsOut = shown;
            }

            var detail = ToolKit.Bool(args, "detail");
            var catalog = ConsentDb.Catalog(portalId);
            var rules = ConsentDb.Rules(portalId);
            var log = ConsentDb.ConsentLog(portalId);

            var result = new
            {
                portalId,
                settings = settingsOut,
                categories = ConsentDb.Categories(portalId).Select(c => new
                {
                    c.CategoryKey,
                    c.Name,
                    c.IsRequired,
                    c.Enabled,
                    c.SortOrder,
                }),
                cookieCatalogCount = catalog.Count,
                cookieRuleCount = rules.Count,
                cookieCatalog = detail ? catalog : null,
                cookieRules = detail ? rules : null,
                managedScriptCount = ConsentDb.ScriptCount(portalId),
                modules = placements.Select(p => new
                {
                    p.ModuleId,
                    allPages = p.AllTabs,
                    p.PageCount,
                    containerShownOnPages = p.ContainerShownCount,
                    p.PaneName,
                }),
                dnnGtmConnector = connectorGtm == null ? null : ConsentFields.Mask(connectorGtm),
                consentLog = new { total = log.Total, last30Days = log.Last30Days ?? 0, log.LastOnUtc },
                warnings,
            };

            return ToolKit.Ok(ConsentDb.Json(result));
        }

        private static CallToolResult Export(Dictionary<string, object> args)
        {
            if (!IsAdmin())
            {
                return ToolKit.Fail("Cookie Consent settings require a portal administrator.");
            }

            var portalId = PortalSettings.Current.PortalId;
            var settings = ConsentDb.LoadSettings(portalId);
            if (settings == null)
            {
                return ToolKit.Fail(
                    "Cookie Consent has no settings on this portal yet. Place the module (place-cookie-consent) "
                    + "and load a page first.");
            }

            var exported = new Dictionary<string, object>();
            foreach (var field in ConsentFields.All.Where(f => !f.SiteSpecific))
            {
                exported[field.Name] = settings[field.Name];
            }

            exported["googleConsentDebugEnabled"] = false;

            var result = new
            {
                module = ConsentDb.ModuleName,
                schemaVersion = "1.0",
                exportedOnUtc = DateTime.UtcNow,
                settings = exported,
                privacyPolicyPage = TabName(settings["privacyPolicyTabId"] as int?, portalId),
                cookiePolicyPage = TabName(settings["cookiePolicyTabId"] as int?, portalId),
                categories = ConsentDb.Categories(portalId).Select(c => new
                {
                    categoryKey = c.CategoryKey,
                    name = c.Name,
                    description = c.Description,
                    isRequired = c.IsRequired,
                    enabled = c.Enabled,
                    sortOrder = c.SortOrder,
                }),
                cookieCatalog = ConsentDb.Catalog(portalId).Select(c => new
                {
                    cookieName = c.CookieName,
                    provider = c.Provider,
                    purpose = c.Purpose,
                    duration = c.Duration,
                    categoryKey = c.CategoryKey,
                    isThirdParty = c.IsThirdParty,
                    providerPolicyUrl = c.ProviderPolicyUrl,
                    description = c.Description,
                    enabled = c.Enabled,
                    sortOrder = c.SortOrder,
                }),
                cookieRules = ConsentDb.Rules(portalId).Select(r => new
                {
                    cookieName = r.CookieName,
                    matchType = r.MatchType,
                    categoryKey = r.CategoryKey,
                    path = r.Path,
                    domain = r.Domain,
                    httpOnly = r.HttpOnly,
                    secure = r.Secure,
                    deleteOnReject = r.DeleteOnReject,
                    deleteOnWithdraw = r.DeleteOnWithdraw,
                    deleteWhenCategoryDisabled = r.DeleteWhenCategoryDisabled,
                    enabled = r.Enabled,
                    description = r.Description,
                    sortOrder = r.SortOrder,
                }),
            };

            return ToolKit.Ok(ConsentDb.Json(result));
        }
    }
}
