using Dnn.Mcp.WebApi;
using Microsoft.Extensions.DependencyInjection;

namespace KGroup.EasyDNNNewsMcp.Tools
{
    public static class ToolsExtensions
    {
        public static void RegisterEasyDNNNewsTools(this IServiceCollection services)
        {
            services.AddTransient<IMcpProvider, NewsCategoryTools>();
            services.AddTransient<IMcpProvider, NewsArticleTools>();
            services.AddTransient<IMcpProvider, NewsModuleSettingsTools>();
        }
    }
}
