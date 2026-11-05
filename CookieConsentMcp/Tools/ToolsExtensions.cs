using Dnn.Mcp.WebApi;
using Microsoft.Extensions.DependencyInjection;

namespace KGroup.CookieConsentMcp.Tools
{
    public static class ToolsExtensions
    {
        public static void RegisterCookieConsentTools(this IServiceCollection services)
        {
            services.AddTransient<IMcpProvider, ConsentReadTools>();
            services.AddTransient<IMcpProvider, ConsentWriteTools>();
        }
    }
}
