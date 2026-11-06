using DotNetNuke.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using KGroup.CookieConsentMcp.Tools;

namespace KGroup.CookieConsentMcp
{
    public class Startup : IDnnStartup
    {
        public void ConfigureServices(IServiceCollection services)
        {
            services.RegisterCookieConsentTools();
        }
    }
}
