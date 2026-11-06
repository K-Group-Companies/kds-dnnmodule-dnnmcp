using DotNetNuke.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using KGroup.EasyDNNNewsMcp.Tools;

namespace KGroup.EasyDNNNewsMcp
{
    public class Startup : IDnnStartup
    {
        public void ConfigureServices(IServiceCollection services)
        {
            services.RegisterEasyDNNNewsTools();
        }
    }
}
