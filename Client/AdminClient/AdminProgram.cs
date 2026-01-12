using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Spectre.Console;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using Websocket.Client;
using Microsoft.Extensions.Configuration;

namespace Syntery.AdminClient
{
    internal class Program
    {
        private static readonly List<string> _connectedDevices = new();
        private static readonly Queue<string> _uiMessages = new();
        private static readonly object _uiLock = new();

        private static Timer? _pingTimer;

        private static volatile UiMode CurrentUiMode = UiMode.Menu;

        private static TaskCompletionSource<int>? _searchTcs;

        private static long _lastPingTs;
        private static long _lastRttMs;
        private static ConnectionQuality _connQuality = ConnectionQuality.Unknown;

        private static string _serverIp = "127.0.0.1";
        private static bool _useSsl = false;

        enum ConnectionQuality
        {
            Unknown,
            High,
            Medium,
            Low
        }



        private static WebsocketClient? _client;

        private static volatile bool _menuActive;
        private static volatile bool _pendingHeaderRefresh;
        private static readonly ConcurrentQueue<LiveScanEvent> _liveScanQueue = new();

        public enum UiMode
        {
            Menu,
            LiveMonitor,
            Busy
        }


        public sealed class LiveScanEvent
        {
            public JObject Payload { get; init; }
        }

        public static int ServerPort { get; private set; } = 7790;

        static async Task Main()
        {
            var config = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings_client.json", optional: false, reloadOnChange: true)
                .Build();

            _serverIp = config["Server:Ip"] ?? "127.0.0.1";
            ServerPort = int.TryParse(config["Server:Port"], out var p) ? p : 7790;
            _useSsl = bool.TryParse(config["Server:UseSsl"], out var s) && s;

            CreateAndStartClient(_serverIp, ServerPort, _useSsl);

            ShowHeader();
            FlushUiMessages();

            await CommandLoopAsync();
        }

        private static void CreateAndStartClient(string serverIp, int port, bool useSsl)
        {
            // Kill old ping
            _pingTimer?.Dispose();
            _pingTimer = null;

            _connQuality = ConnectionQuality.Unknown;
            _lastRttMs = 0;
            _lastPingTs = 0;

            // Kill old client
            if (_client != null)
            {
                try
                {
                    _client.Stop(WebSocketCloseStatus.NormalClosure, "Recreate client").Wait();
                    _client.Dispose();
                }
                catch { }
            }

            string scheme = useSsl ? "wss" : "ws";
            var url = new Uri($"{scheme}://{serverIp}:{port}/ws");

            _client = new WebsocketClient(url);

            _client.MessageReceived.Subscribe(OnMessageReceived);

            _client.ReconnectionHappened.Subscribe(_ =>
            {
                Send(new JObject { ["cmd"] = "admin_hello" });

                if (_pingTimer == null)
                {
                    _pingTimer = new Timer(_ =>
                    {
                        if (_client?.IsRunning != true) return;

                        _lastPingTs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        Send(new JObject
                        {
                            ["cmd"] = "ping",
                            ["ts"] = _lastPingTs
                        });

                    }, null, TimeSpan.Zero, TimeSpan.FromSeconds(3));

                }

                // 🔥 FORCE FIRST RTT IMMEDIATELY
                _lastPingTs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                Send(new JObject
                {
                    ["cmd"] = "ping",
                    ["ts"] = _lastPingTs
                });

                _pendingHeaderRefresh = true;

            });


            _client.DisconnectionHappened.Subscribe(_ =>
            {
                _pingTimer?.Dispose();
                _pingTimer = null;

                _connQuality = ConnectionQuality.Unknown;
                _lastRttMs = 0;
                _pendingHeaderRefresh = true;
            });

            _client.Start();
        }

        // ---------------- UI ----------------
        private static string GetQualityLabel()
        {
            return _connQuality switch
            {
                ConnectionQuality.Low => $"[green]LOW[/], {_lastRttMs} ms",
                ConnectionQuality.Medium => $"[yellow]MEDIUM[/], {_lastRttMs} ms",
                ConnectionQuality.High => $"[red]HIGH[/], {_lastRttMs} ms",
                _ => $"[grey]UNKNOWN[/]"
            };
        }

        private static void Pause()
        {
            AnsiConsole.MarkupLine("\n[grey]Press any key to continue...[/]");
            Console.ReadKey(true);
        }

        private static void ShowHeader()
        {
            AnsiConsole.Clear();
            AnsiConsole.Write(new FigletText("Syntry").LeftJustified().Color(Color.Cyan1));

            string baseStatus =
                _client?.NativeClient?.State == WebSocketState.Open
                    ? "[green]CONNECTED[/]"
                    : _client?.IsRunning == true
                        ? "[yellow]CONNECTING[/]"
                        : "[red]DISCONNECTED[/]";

            string status =
                _client?.NativeClient?.State == WebSocketState.Open
                    ? $"{baseStatus} ({GetQualityLabel()})"
                    : baseStatus;



            AnsiConsole.MarkupLine(
                $"[bold yellow]AI Face Recognition Admin Client[/] | [grey]v2.0[/] | [blue]Port : {ServerPort}[/] | Server : {status}"
            );
            AnsiConsole.Write(new Rule());
        }

        // WORKING ON MAKING THE PING PONG KEEP REFRESHING - NOT WORKING YET
        private static async Task CommandLoopAsync()
        {
            CurrentUiMode = UiMode.Menu;

            while (true)
            {
                // Draw header ONLY if requested
                if (_pendingHeaderRefresh && CurrentUiMode == UiMode.Menu)
                {
                    _pendingHeaderRefresh = false;
                    ShowHeader();
                }

                _menuActive = true;

                var command = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title($"Select a [green]command[/]: [grey]({_connQuality}, {_lastRttMs} ms)[/]")
                .AddChoices(new[]
                {
                    "Add User",
                    "Delete User",
                    "Set Active",
                    "Get User Details",
                    "Live Monitor",
                    "Change Server Port",
                    "Check Server",
                    "Exit"
                })
        );

                _menuActive = false;

                switch (command)
                {
                    case "Exit":
                        return;

                    case "Add User":
                        HandleAddUser();
                        Pause();
                        break;

                    case "Delete User":
                        await SearchAndSelectUserAsync("admin_delete_user");
                        Pause();
                        break;

                    case "Set Active":
                        await SearchAndSelectUserAsync("admin_set_active");
                        Pause();
                        break;

                    case "Get User Details":
                        await SearchAndSelectUserAsync("admin_get_user");
                        //HandleGetUser();
                        Pause();
                        break;
                    case "Live Monitor":
                        CurrentUiMode = UiMode.LiveMonitor;

                        AnsiConsole.Clear();
                        AnsiConsole.Write(new Rule("[red]LIVE MONITOR[/]"));
                        AnsiConsole.MarkupLine("[grey]Press any key to return...[/]\n");

                        while (!Console.KeyAvailable)
                        {
                            while (_liveScanQueue.TryDequeue(out var evt))
                            {
                                ShowLiveScanTree(evt.Payload);
                            }


                            await Task.Delay(50);
                        }

                        Console.ReadKey(true);
                        CurrentUiMode = UiMode.Menu;
                        //ShowHeader();
                        break;

                    case "Change Server Port":
                        HandleChangePortAsync();
                        break;


                    case "Check Server":
                        break;



                }
                AnsiConsole.Clear();
                ShowHeader();
                _pendingHeaderRefresh = true;
            }
        }

        // ---------------- Command Handlers ----------------
        private static async Task SearchAndSelectUserAsync(string nextCommand)
        {
            Console.Write("Enter EnrollID or Name: ");
            string input = Console.ReadLine()?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(input))
                return;

            int enrollId;

            // 1️⃣ Numeric → direct
            if (int.TryParse(input, out enrollId))
            {
                // continue
            }
            else
            {
                // 2️⃣ Name search → wait for selection
                _searchTcs = new TaskCompletionSource<int>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

                Send(new JObject
                {
                    ["cmd"] = "admin_search_user_by_name",
                    ["name"] = input
                });

                enrollId = await _searchTcs.Task;
                if (enrollId == 0)
                    return;
            }

            // 3️⃣ Command-specific extra input
            JObject payload = new JObject
            {
                ["cmd"] = nextCommand,
                ["enrollId"] = enrollId
            };

            // 🔑 SPECIAL CASE: Set Active
            if (nextCommand == "admin_set_active")
            {
                bool active = AnsiConsole.Confirm("Set user as active?");
                payload["active"] = active;
            }

            if (nextCommand == "admin_delete_user")
            {
                bool confirm = AnsiConsole.Confirm(
                    "[red]Are you sure you want to permanently delete this user?[/]");

                if (!confirm)
                {
                    EnqueueUiMessage("[yellow]Delete cancelled[/]");
                    return;
                }
            }

            // 4️⃣ Send final command
            Send(payload);

            EnqueueUiMessage("[green]Command sent[/]");
        }




        private static void HandleChangePortAsync()
        {
            int newPort = AnsiConsole.Prompt(
                new TextPrompt<int>("Enter new [green]Server Port[/]:")
                    .Validate(p => p > 0 && p <= 65535
                        ? ValidationResult.Success()
                        : ValidationResult.Error("[red]Invalid port[/]"))
            );

            if (newPort == ServerPort)
            {
                EnqueueUiMessage("[yellow]Already connected to this port[/]");
                return;
            }

            EnqueueUiMessage($"[grey]Switching to port {newPort}...[/]");

            SwitchServerAsync(newPort);
        }

        private static void SwitchServerAsync(int newPort)
        {
            try
            {
                _menuActive = true;

                ServerPort = newPort;

                // reuse exact same connection logic
                CreateAndStartClient(_serverIp, ServerPort, _useSsl);

                EnqueueUiMessage($"[green]Connected to {_serverIp}:{ServerPort}[/]");
            }
            catch (Exception ex)
            {
                EnqueueUiMessage($"[red]Failed to connect: {ex.Message}[/]");
            }
        }





        private static void HandleAddUser()
        {
            if (!IsServerConnected())
            {
                EnqueueUiMessage("[red]Server not connected[/]");
                return;
            }

            RequestDeviceList();

            if (!WaitForDevices())
            {
                EnqueueUiMessage("[red]No devices connected[/]");
                return;
            }

            var sn = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Select [green]Device[/]:")
                    .AddChoices(_connectedDevices)
            );

            var name = AnsiConsole.Prompt(
                new TextPrompt<string>("Enter [green]Name[/] ([grey]leave empty to cancel[/]):")
                    .AllowEmpty()
            );

            if (string.IsNullOrWhiteSpace(name))
            {
                EnqueueUiMessage("[grey]Operation cancelled[/]");
                return;
            }

            bool isAdmin = AnsiConsole.Confirm("Is Admin?");

            Send(new JObject
            {
                ["cmd"] = "admin_add_user",
                ["deviceSn"] = sn,
                ["name"] = name,
                ["isAdmin"] = isAdmin ? 1 : 0
            });

            EnqueueUiMessage("[green]Add user command sent[/]");
        }
        /*
        private static void HandleDeleteUser()
        {
            int enrollId = PromptEnrollId();
            if (enrollId == 0) return;

            Send(new JObject
            {
                ["cmd"] = "admin_delete_user",
                ["enrollId"] = enrollId
            });

            EnqueueUiMessage("[green]Delete command sent[/]");
        }

        private static void HandleSetActive()
        {
            int enrollId = PromptEnrollId();
            if (enrollId == 0) return;

            bool active = AnsiConsole.Confirm("Set user as active?");

            Send(new JObject
            {
                ["cmd"] = "admin_set_active",
                ["enrollId"] = enrollId,
                ["active"] = active
            });

            EnqueueUiMessage("[green]Set active command sent[/]");
        }

        private static void HandleGetUser()
        {
            int enrollId = PromptEnrollId();
            if (enrollId == 0) return;

            Send(new JObject
            {
                ["cmd"] = "admin_get_user",
                ["enrollId"] = enrollId
            });
        }
        */
        // ---------------- Message Handling ----------------

        private static void OnMessageReceived(ResponseMessage msg)
        {
            if (string.IsNullOrWhiteSpace(msg.Text)) return;

            var j = JObject.Parse(msg.Text);
            string ret = j.Value<string>("ret") ?? "";

            switch (ret)
            {
                case "admin_list_devices":
                    _connectedDevices.Clear();
                    foreach (var d in j["devices"] ?? new JArray())
                        _connectedDevices.Add(d.ToString());
                    break;

                case "admin_add_user":
                    EnqueueUiMessage(j.Value<bool>("result")
                        ? $"[green]{j["message"]}[/]"
                        : $"[red]{j["error"]}[/]");
                    break;

                case "admin_get_user":
                    if (!j.Value<bool>("result"))
                    {
                        EnqueueUiMessage($"[red]{j["error"]}[/]");
                        break;
                    }
                    PrintUserContextFromJson(j);
                    break;
                case "live_scan":
                    _liveScanQueue.Enqueue(new LiveScanEvent { Payload = j });
                    return;

                case "admin_hello":
                    //  EnqueueUiMessage("[green]Admin Session Registered[/]");
                    break;

                case "admin_set_active":
                    EnqueueUiMessage(j.Value<bool>("result")
                        ? $"[green]{j["message"]}[/]"
                        : $"[red]{j["error"]}[/]");
                    break;
                case "admin_delete_user":
                    EnqueueUiMessage(j.Value<bool>("result")
                        ? $"[green]{j["message"]}[/]"
                        : $"[red]{j["error"]}[/]");
                    break;

                case "admin_search_user_by_name_result":
                    HandleAdminSearchResult(j);
                    break;

                case "admin_enroll_complete":
                    HandleEnrollComplete(j);
                    break;


                case "pong":
                    {
                        long sentTs = j.Value<long>("ts");
                        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                        _lastRttMs = now - sentTs;

                        // 🔥 Update quality HERE
                        if (_lastRttMs <= 50)
                            _connQuality = ConnectionQuality.Low;
                        else if (_lastRttMs <= 150)
                            _connQuality = ConnectionQuality.Medium;
                        else
                            _connQuality = ConnectionQuality.High;

                        _pendingHeaderRefresh = true;

                        break;

                    }



            }

            if (!_menuActive && CurrentUiMode == UiMode.Menu)
                FlushUiMessages();

        }

        // ---------------- Helpers ----------------
        private static void HandleEnrollComplete(JObject json)
        {
            int enrollId = json.Value<int>("enrollId");
            string username = json.Value<string>("username") ?? "Unknown";

            EnqueueUiMessage(
                $"[green]Enrollment complete: {username} (EnrollID: {enrollId})[/]"
            );
        }



        private static void HandleAdminSearchResult(JObject json)
        {
            var users = json["users"] as JArray;
            if (users == null || users.Count == 0)
            {
                EnqueueUiMessage("[red]No users found[/]");
                _searchTcs?.TrySetResult(0);
                return;
            }

            Console.WriteLine("Multiple users found:");

            for (int i = 0; i < users.Count; i++)
            {
                var u = (JObject)users[i];
                Console.WriteLine(
                    $"{i + 1}) {u["username"]} (EnrollID: {u["enrollId"]})"
                );
            }

            Console.Write($"Select a user (1-{users.Count}) or 0 to cancel: ");
            if (!int.TryParse(Console.ReadLine(), out int choice) ||
                choice < 1 || choice > users.Count)
            {
                _searchTcs?.TrySetResult(0);
                return;
            }

            int enrollId = users[choice - 1].Value<int>("enrollId");
            _searchTcs?.TrySetResult(enrollId);
        }

        private static void PrintUserContextFromJson(JObject j)
        {
            var table = new Table().AddColumn("Field").AddColumn("Value");
            table.AddRow("Enroll ID", j["enrollId"]!.ToString());
            table.AddRow("Name", j["userName"]!.ToString());
            table.AddRow("Has Face", (bool)j["hasFace"]! ? "Yes" : "No");
            table.AddRow("Active", (bool)j["isActive"]! ? "Yes" : "No");

            AnsiConsole.Write(table);
        }

        private static int PromptEnrollId()
        {
            return AnsiConsole.Prompt(
                new TextPrompt<int>("Enter [green]Enroll ID[/] ([grey]0 to cancel[/]):")
                    .Validate(id => id >= 0
                        ? ValidationResult.Success()
                        : ValidationResult.Error("[red]Invalid ID[/]"))
            );
        }

        private static void RequestDeviceList()
        {
            Send(new JObject { ["cmd"] = "admin_list_devices" });
        }

        private static bool WaitForDevices(int timeoutMs = 1000)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (_connectedDevices.Count > 0)
                    return true;
                Thread.Sleep(50);
            }
            return false;
        }

        private static void Send(JObject payload)
        {
            if (IsServerConnected())
                _client!.Send(payload.ToString(Newtonsoft.Json.Formatting.None));
        }

        private static bool IsServerConnected()
        {
            return _client != null &&
                   _client.IsRunning &&
                   _client.NativeClient?.State == WebSocketState.Open;
        }

        private static void EnqueueUiMessage(string msg)
        {
            lock (_uiLock)
                _uiMessages.Enqueue(msg);
        }

        private static void FlushUiMessages()
        {
            lock (_uiLock)
            {
                while (_uiMessages.Count > 0)
                    AnsiConsole.MarkupLine(_uiMessages.Dequeue());
            }
        }
        /*
        private static void StartAdminPing()
        {
            _pingTimer = new Timer(_ =>
            {
                if (!IsServerConnected())
                    return;

                Send(new JObject
                {
                    ["cmd"] = "admin_ping"
                });

            }, null, TimeSpan.Zero, TimeSpan.FromSeconds(30));
        }
        */
        // ---------------- Live Scan UI ----------------
        private static void ShowLiveScanTree(JObject j)
        {
            //Console.WriteLine("Showing Live Scan Tree"); //DEBUG
            bool isActive = j.Value<bool>("isActive");
            bool hasFace = j.Value<bool>("hasFace");



            var tree = new Tree("[bold cyan]Live Scan[/]");

            var deviceNode = tree.AddNode("[yellow]Device[/]");
            deviceNode.AddNode($"SN : {j["deviceSn"]}");
            deviceNode.AddNode($"IP : {j["deviceIp"]}");

            var userNode = tree.AddNode("[green]User[/]");
            userNode.AddNode($"Name     : {j["userName"]}");
            userNode.AddNode($"EnrollID : {j["enrollId"]}");
            userNode.AddNode($"Active   : {(isActive ? "Yes" : "No")}");
            userNode.AddNode($"Has Face : {(hasFace ? "Yes" : "No")}");


            var matchNode = tree.AddNode("[blue]Match[/]");
            matchNode.AddNode($"Matched : {(bool)j["matched"]}");
            matchNode.AddNode($"Score   : {j["matchScore"]!.Value<double>():F3}");

            if (j["liveness"] != null)
            {
                var live = j["liveness"]!;
                var liveNode = tree.AddNode("[red]Liveness[/]");
                liveNode.AddNode($"Score     : {live["Score"]}");
                liveNode.AddNode($"Prob Real : {live["Prob"]}");
                liveNode.AddNode($"Time (ms) : {live["TimeMs"]}");
            }
            else
            {
                tree.AddNode("[grey]Liveness : Not Available[/]");
            }

            tree.AddNode($"[grey]Time : {j["time"]}[/]");

            AnsiConsole.Write(tree);
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine(new string('═', 95));
        }

    }
}
