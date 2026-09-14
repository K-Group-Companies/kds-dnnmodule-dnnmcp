using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Principal;
using System.Web;
using System.Web.Http;
using System.Web.Http.Controllers;
using DotNetNuke.Web.Api;
using DotNetNuke.Entities.Portals;
using DotNetNuke.Entities.Users;
using DotNetNuke.Instrumentation;
using Satrabel.PersonaBar.DnnMcp.Apis;

namespace Dnn.Mcp.WebApi.Middleware
{
    /// <summary>
    /// Authorization attribute for API key validation.
    /// </summary>
    public class ApiKeyAuthorizeAttribute : AuthorizeAttributeBase, IOverrideDefaultAuthLevel
    {
        private static readonly ILog Logger = LoggerSource.Instance.GetLogger(typeof(ApiKeyAuthorizeAttribute));


        /// <summary>
        /// Extracts the API key from the Authorization header.
        /// </summary>
        /// <param name="actionContext">The action context.</param>
        /// <returns>The API key, or null if not found.</returns>
        private string? GetApiKeyFromHeader(AuthFilterContext actionContext)
        {
            if (!actionContext.ActionContext.Request.Headers.Contains("Authorization"))
            {
                return null;
            }

            var authHeader = actionContext.ActionContext.Request.Headers.GetValues("Authorization").FirstOrDefault();
            
            if (string.IsNullOrWhiteSpace(authHeader))
            {
                return null;
            }

            // Expected format: "Bearer <api-key>"
            if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                return authHeader.Substring("Bearer ".Length).Trim();
            }

            return null;
        }

        public override bool IsAuthorized(AuthFilterContext context)
        {
            if (context == null)
            {
                return false;
            }

            var apiKey = GetApiKeyFromHeader(context);

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return false;
            }

            var portalSettings = PortalSettings.Current;
            var apiKeySetting = PortalController.GetPortalSetting(DnnMcpController.APIKEY_SETTING, portalSettings.PortalId, "");
            if (apiKeySetting != null && apiKeySetting == apiKey)
            {
                // Check if the API key valid delay is active
                var validDelayActiveStr = PortalController.GetPortalSetting(DnnMcpController.APIKEY_ACTIVE_SETTING, portalSettings.PortalId, "true");
                bool validDelayActive = bool.Parse(validDelayActiveStr);
                
                if (!validDelayActive)
                {
                    EstablishUserContext(portalSettings.PortalId);
                    return true;
                }
                // Check if the API key has expired
                var validUntilDateStr = PortalController.GetPortalSetting(DnnMcpController.APIKEY_VALID_UNTIL_DATE_SETTING, portalSettings.PortalId, "");
                if (!string.IsNullOrEmpty(validUntilDateStr))
                {
                    if (DateTime.TryParse(validUntilDateStr, out DateTime validUntilDate))
                    {
                        if (DateTime.Now > validUntilDate)
                        {
                            // API key has expired
                            return false;
                        }
                    }
                }

                EstablishUserContext(portalSettings.PortalId);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Gives the authorized request the DNN identity of the user who minted the API key,
        /// so that tools performing writes can pass DNN's permission checks.
        /// </summary>
        /// <remarks>
        /// Without this, every write tool fails with <c>InsufficientPermissions</c>: DNN core
        /// services such as <c>TabPermissionController.CanAddContentToPage</c> resolve the
        /// caller through <c>UserController.Instance.GetCurrentUserInfo()</c>, find an
        /// anonymous user, and deny. Reads succeed because they never consult it.
        ///
        /// <c>UserController.GetCurrentUserInternal</c> reads the current user from exactly
        /// one place when an <see cref="HttpContext"/> exists:
        /// <c>HttpContext.Current.Items["UserInfo"]</c>. Setting
        /// <c>PortalSettings.UserInfo</c> alone has no effect on that path — it is read-only
        /// and derives from this same entry. <c>HttpContext.User</c> is set only because
        /// other DNN code reads it directly.
        ///
        /// The identity comes from <see cref="DnnMcpController.APIKEY_CREATED_BY_SETTING"/>,
        /// which <c>DnnMcpController</c> already writes whenever a key is generated. This is
        /// impersonation, not a permission bypass — DNN still evaluates that user's real
        /// rights. The security consequence is that the bearer token carries that user's
        /// privileges, so the key must be treated as that user's credential.
        /// </remarks>
        /// <param name="portalId">The portal the request was authorized against.</param>
        private static void EstablishUserContext(int portalId)
        {
            var context = HttpContext.Current;
            if (context == null)
            {
                return;
            }

            var createdBy = PortalController.GetPortalSetting(
                DnnMcpController.APIKEY_CREATED_BY_SETTING, portalId, "");

            if (!int.TryParse(createdBy, out var userId) || userId <= 0)
            {
                Logger.Warn(
                    "Portal setting " + DnnMcpController.APIKEY_CREATED_BY_SETTING
                    + " is not a valid user id ('" + createdBy + "'); MCP tools that write will "
                    + "fail permission checks. Regenerate the API key in PersonaBar > MCP Settings.");

                return;
            }

            var user = UserController.Instance.GetUser(portalId, userId);
            if (user == null || user.IsDeleted)
            {
                Logger.Warn(
                    "User " + userId + " from " + DnnMcpController.APIKEY_CREATED_BY_SETTING
                    + " was not found or is deleted; MCP tools that write will fail permission checks.");

                return;
            }

            context.Items["UserInfo"] = user;
            context.User = new GenericPrincipal(
                new GenericIdentity(user.Username, "DNN"), user.Roles ?? new string[0]);
        }
    }
}
