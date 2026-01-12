using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
#if WINDOWS
using System.ServiceProcess;
#endif

namespace CloudDemoNet8
{
    public sealed class WebSocketService : ServiceBase
    {
        private IHost? _host;

        protected override void OnStart(string[] args)
        {
            // Fire-and-forget so ServiceBase returns quickly
            _ = Task.Run(async () =>
            {
                // Build a minimal host to read configuration
                var builder = Host.CreateDefaultBuilder(args)
                    .ConfigureAppConfiguration((context, config) =>
                    {
                        config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
                    });

                _host = builder.Build();

                // Read port from configuration 
                var config = _host.Services.GetRequiredService<IConfiguration>();
                int port = config.GetValue<int>("Server:Port");

                await WebSocketLoader.StartServerAsync(port);
            });
        }

        protected override void OnStop()
        {
            try
            {
                WebSocketLoader.StopServerAsync().GetAwaiter().GetResult();
            }
            catch
            {
                // Ignore shutdown exceptions
            }
            finally
            {
                _host = null;
            }
        }

        protected override void OnShutdown() => OnStop();
    }
}