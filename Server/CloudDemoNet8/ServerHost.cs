using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Spectre.Console;


//wd awad a
namespace CloudDemoNet8
{
    public class ServerHost : BackgroundService
    {
        public static readonly TaskCompletionSource<bool> ReadySignal = new();

        private readonly ILogger<ServerHost> _logger;
        private readonly IConfiguration _config;

        public ServerHost(ILogger<ServerHost> logger, IConfiguration config)
        {
            _logger = logger;
            _config = config;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                // 1. Initialize AI Models
                _logger.LogInformation("Initializing AI Engine...");
                string baseDir = AppContext.BaseDirectory;

                FaceMatch.InitModels(
                    Path.Combine(baseDir, _config["AI:FaceDetection"]!),
                    Path.Combine(baseDir, _config["AI:FaceRecognition"]!),
                    Path.Combine(baseDir, _config["AI:AntiSpoof"]!)
                );

                // 2. Load DB Data
                _logger.LogInformation("Loading Face Embeddings...");
                FaceMatch.LoadEmbeddings(_config.GetConnectionString("Default")!);

                // 3. Start Background Sync (Fire and Forget but monitored)
                _ = Task.Run(async () =>
                {
                    while (!stoppingToken.IsCancellationRequested)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                        await FaceMatch.SyncByComparisonAsync();
                    }
                }, stoppingToken);

                // 4. Start WebSocket Server
                int port = _config.GetValue<int>("Server:Port");
                await WebSocketLoader.StartServerAsync(port);

                // Notify UI (Spectre Console)
                _logger.LogInformation("Server is running on port {Port}", port);
                ReadySignal.TrySetResult(true);
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "FATAL: Server failed to start.");
                ReadySignal.TrySetResult(false);
            }
            // Keep alive until cancelled
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
    }
}