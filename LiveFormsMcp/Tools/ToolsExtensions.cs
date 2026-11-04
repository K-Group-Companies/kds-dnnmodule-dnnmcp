using Dnn.Mcp.WebApi;
using Microsoft.Extensions.DependencyInjection;

namespace KGroup.LiveFormsMcp.Tools
{
    public static class ToolsExtensions
    {
        public static void RegisterLiveFormsTools(this IServiceCollection services)
        {
            services.AddTransient<IMcpProvider, FormReadTools>();
            services.AddTransient<IMcpProvider, FormWriteTools>();
        }
    }
}
