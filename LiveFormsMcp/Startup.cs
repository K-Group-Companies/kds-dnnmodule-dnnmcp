using DotNetNuke.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using KGroup.LiveFormsMcp.Tools;

namespace KGroup.LiveFormsMcp
{
    public class Startup : IDnnStartup
    {
        public void ConfigureServices(IServiceCollection services)
        {
            services.RegisterLiveFormsTools();
        }
    }
}
