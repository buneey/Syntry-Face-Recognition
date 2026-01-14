using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Spectre.Console;
using static FaceMatch;

// First Commit
namespace CloudDemoNet8
{
    internal static class Program
    {
        public static LoggingLevelSwitch ConsoleSwitch = new(LogEventLevel.Information);

        // Global access (legacy statics rely on this)
        public static string ConnectionString { get; private set; } = string.Empty;
        public static int ServerPort { get; private set; }

        public static async Task Main(string[] args)
        {
            int? cliPort = null;
            if (args.Length > 0 && int.TryParse(args[0], out int p)) cliPort = p;

            Log.Logger = new LoggerConfiguration()
                .WriteTo.Console(levelSwitch: ConsoleSwitch)
                .WriteTo.File("logs/syntery-.log", rollingInterval: RollingInterval.Day)
                .CreateLogger();

            try
            {
                var host = Host.CreateDefaultBuilder(args)
                    .ConfigureAppConfiguration((context, config) =>
                    {
                        config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
                        if (cliPort.HasValue)
                        {
                            config.AddInMemoryCollection(new Dictionary<string, string?>
                            {
                                { "Server:Port", cliPort.Value.ToString() }
                            });
                        }
                    })
                    .ConfigureServices((context, services) =>
                    {
                        ConnectionString = context.Configuration.GetConnectionString("Default")!;
                        ServerPort = context.Configuration.GetValue<int>("Server:Port");

                        services.AddSingleton(new SynteryRepository(ConnectionString));
                        services.AddHostedService<ServerHost>();
                    })
                    .UseSerilog()
                    .Build();

                await host.RunAsync();
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Application start-up failed");
            }
            finally
            {
                Log.CloseAndFlush();
            }
        }
    }
}