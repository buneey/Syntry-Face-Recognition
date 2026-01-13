/*
Recommended before large‑scale deployment:
- Replace blocking async calls with full async
- Add per‑device scan throttling
- Move thresholds to configuration
- Reduce verbose debug logging
 */

using Emgu.CV.Dnn;
using Emgu.CV.Features2D;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualBasic.ApplicationServices;
using Newtonsoft.Json.Linq;
using Serilog;
using Serilog.Events;
using SuperSocket.Server;
using SuperSocket.Server.Abstractions;
using SuperSocket.Server.Host;
using SuperSocket.WebSocket.Server;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using static CloudDemoNet8.Program;

namespace CloudDemoNet8
{
    public static class WebSocketLoader
    {
        private static readonly SynteryRepository _repo = new(Program.ConnectionString);

        private static IHost? _host;
        private static readonly ConcurrentDictionary<string, WebSocketSession> _sessions = new();
        private static readonly ConcurrentDictionary<string, string> _deviceToSession = new();

        private static readonly ConcurrentDictionary<string, WebSocketSession> _adminSessions = new();


        private static readonly ConcurrentDictionary<string, PendingEnrollment> _pendingEnrollmentsBySn = new();

        private class PendingEnrollment
        {
            public int EnrollId { get; init; }
            public string UserName { get; init; } = "";
            public int IsAdmin { get; init; }
            public int ShotsRemaining { get; set; } = 2;

            public DateTime StartedAt { get; init; } = DateTime.UtcNow;

        }

        public static IReadOnlyList<string> GetConnectedDeviceSns() => _deviceToSession.Keys.ToList();

        // ---------------- SERVER ----------------

        public static async Task<IHost> StartServerAsync(int port)
        {
            var host = WebSocketHostBuilder.Create()
                .UseSessionHandler(
                    onConnected: session =>
                    {
                        Log.Information("[CONNECTED] {EP}", session.RemoteEndPoint);
                        return ValueTask.CompletedTask;
                    },
                    onClosed: (session, reason) =>
                    {
                        CleanupSession(session.SessionID);
                        Log.Information("[DISCONNECTED] {EP} {Reason}", session.RemoteEndPoint, reason);
                        return ValueTask.CompletedTask;
                    })
                .UseWebSocketMessageHandler(async (s, p) =>
                {
                    await HandleIncomingMessage(s, p.Message);
                })
                .ConfigureSuperSocket(o =>
                {
                    o.AddListener(new ListenOptions
                    {
                        Ip = "Any",
                        Port = port
                    });
                })
                .ConfigureLogging(l => l.ClearProviders())
                .UseSerilog()
                .Build();

            _host = host;
            await host.StartAsync();
            return host;
        }

        public static async Task StopServerAsync()
        {
            if (_host != null)
            {
                await CleanLogsOnExitAsync();
                await _host.StopAsync();
            }
        }

        private static void CleanupSession(string sid)
        {
            _sessions.TryRemove(sid, out _);
            _adminSessions.TryRemove(sid, out _);

            foreach (var kv in _deviceToSession.Where(x => x.Value == sid))
            {
                var sn = kv.Key;
                _deviceToSession.TryRemove(sn, out _);

                if (_pendingEnrollmentsBySn.TryRemove(sn, out var p))
                {
                    Log.Information(
                        "[ENROLL] Cancelled | Device disconnected | EnrollID={EnrollId} | SN={SN}",
                        p.EnrollId, sn
                    );
                }
            }
        }



        // ---------------- ENROLLMENT ----------------
        private static readonly TimeSpan EnrollmentTimeout = TimeSpan.FromSeconds(60);

        public static async Task<bool> StartFaceEnrollment(string sn, int enrollId, string username, int isAdmin)
        {
            if (_pendingEnrollmentsBySn.ContainsKey(sn))
            {
                Log.Information(
                    "[ENROLL] Rejected | Enrollment already in progress | Device={SN}",
                    sn
                );
                return false;
            }

            if (!GetSessionBySN(sn))
            {
                Log.Information("[ENROLL] Rejected | Device not connected | EnrollID={EnrollId}", enrollId);
                return false;
            }

            if (await _repo.HasFaceDataAsync(enrollId))
            {
                Log.Information("[ENROLL] Rejected | Face already exists | EnrollID={EnrollId} | User={User}", enrollId, username);
                return false;
            }

            _pendingEnrollmentsBySn[sn] = new PendingEnrollment
            {
                EnrollId = enrollId,
                UserName = username,
                IsAdmin = isAdmin,
                ShotsRemaining = 2
            };

            Log.Information("[ENROLL] Started | EnrollID={EnrollId} | User={User} | Device={SN} | Shots=2", enrollId, username, sn);

            return true;
        }

        // ---------------- MESSAGE HANDLING ----------------


        private static async Task HandleIncomingMessage(WebSocketSession session, string msg)
        {
            JObject json;
            try { json = JObject.Parse(msg); }
            catch { return; }

            var cmd = json.Value<string>("cmd");
            if (cmd == null) return;

            switch (cmd)
            {
                // ---------------- DEVICE COMMANDS ----------------
                case "reg":
                    await HandleRegister(session, json);
                    break;
                case "sendlog":
                    await HandleSendLog(session, json);
                    break;
                case "senduser":
                    await HandleSendUser(session, json);
                    break;
                // ---------------- ADMIN COMMANDS ----------------

                case "admin_add_user":
                    await HandleAdminAddUser(session, json);
                    break;

                case "admin_delete_user":
                    await HandleAdminDeleteUser(session, json);
                    break;

                case "admin_set_active":
                    await HandleAdminSetActive(session, json);
                    break;
                case "admin_get_user":
                    await HandleAdminGetUser(session, json);
                    break;
                case "admin_list_devices":
                    await HandleAdminListDevices(session);
                    break;

                case "admin_ping":
                    {
                        await SafeSendReplyAsync(
                            session,
                            "admin_ping",
                            true,
                            new { serverTime = DateTime.UtcNow }
                        );
                        break;
                    }
                case "admin_hello":
                    await HandleAdminHello(session);
                    break;
                case "admin_search_user_by_name":
                    await HandleAdminSearchUserByName(session, json);
                    break;

                case "ping":
                    {
                        await SafeSendAsync(session, new JObject
                        {
                            ["ret"] = "pong",
                            ["ts"] = json.Value<long>("ts")
                        });
                        break;
                    }



                default:
                    Log.Warning("[WS] Unknown command: {Cmd}", cmd);
                    break;
            }
        }

        // Testing Commits
        private static async Task HandleAdminHello(WebSocketSession s)
        {
            _adminSessions[s.SessionID] = s;

            Log.Information(
                "[ADMIN] Registered | Session={SID} | EP={EP}",
                s.SessionID,
                s.RemoteEndPoint
            );

            // Reply to admin (correct)
            await SafeSendReplyAsync(
                s,
                "admin_hello",
                true,
                new { message = "Admin registered" }
            );

            // Replay SDK-style reg approval to ALL connected devices
            foreach (var sn in _deviceToSession.Keys)
            {
                var dev = GetSessionByID(sn);
                if (dev != null)
                {
                    await SafeSendReplyAsync(
                        dev,   
                        "reg",
                        true,
                        new
                        {
                            cloudtime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                            nosenduser = false
                        }
                    );
                }
            }
        }



        private static async Task HandleAdminSearchUserByName(WebSocketSession s, JObject j)
        {
            string name = j.Value<string>("name")?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name))
                return;

            var users = await SynteryRepository.SearchUsersByNameAsync(
                Program.ConnectionString, name);

            var arr = new JArray();
            foreach (var u in users)
            {
                arr.Add(new JObject
                {
                    ["enrollId"] = u.EnrollId,
                    ["username"] = u.UserName,
                    ["isActive"] = u.IsActive
                });
            }

            await SafeSendReplyAsync(
                s,
                "admin_search_user_by_name_result",
                true,
                new { users = arr }
            );
        }

        private static async Task HandleRegister(WebSocketSession s, JObject j)
        {
            var sn = j.Value<string>("sn") ?? "";

            await SafeSendReplyAsync(
                s,
                "reg",
                true,
                new
                {
                    cloudtime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    nosenduser = false
                }
            );


            _sessions[s.SessionID] = s;

            if (!string.IsNullOrEmpty(sn))
            {
                // 🔴 Deduplicate device sessions by SN
                if (_deviceToSession.TryGetValue(sn, out var oldSessionId))
                {
                    if (_sessions.TryGetValue(oldSessionId, out var oldSession))
                    {
                        Log.Information("[DEVICE] Replaced session for {SN} | Old={OldSID} New={NewSID}", sn, oldSessionId, s.SessionID);

                        try
                        {
                            await oldSession.CloseAsync();
                        }
                        catch
                        {
                            // Ignore — session may already be closed
                        }
                    }
                }

                _deviceToSession[sn] = s.SessionID;
            }
            // Log.Information("[DEVICE] SN={SN} mapped to Session={SID}",sn,s.SessionID); //DEBUG

        }

        private static async Task HandleSendUser(WebSocketSession s, JObject j)
        {
            try
            {
                string sn = j.Value<string>("sn") ?? "";
                int enroll = j.Value<int>("enrollid");
                int backup = j.Value<int>("backupnum");
                string name = j.Value<string>("name") ?? "";
                int admin = j.Value<int>("admin");
                string? rec = j.Value<string>("record");

                if (backup != 50 || string.IsNullOrEmpty(rec)) return;

                int enrollId = await FaceMatch.GenerateNextEnrollIdAsync(Program.ConnectionString);
                await _repo.UpsertUserAsync(enrollId, name, backup, admin, rec);
                FaceMatch.AddUserToMemory(enrollId, rec, name);


                await SafeSendReplyAsync(s, "senduser", true, new { enrollid = enroll, backupnum = backup });
                Log.Information(
                            "[ENROLL] Complete | EnrollID={EnrollId} | Device={SN}",
                            enrollId, sn
                        );
                await CleanDeviceLogs(sn);
            }
            catch
            {
                await SafeSendReplyAsync(s, "senduser", false);
            }
        }

        private static async Task HandleSendLog(WebSocketSession s, JObject j)
        {
            var sn = j.Value<string>("sn") ?? "";
            var arr = j["record"] as JArray;
            if (arr == null) return;

            foreach (var r in arr.OfType<JObject>())
            {
                int enroll = r.Value<int>("enrollid");
                string time = r.Value<string>("time") ?? "";
                string note = r["note"]?["msg"]?.ToString() ?? "";
                string img = r.Value<string>("image") ?? "";

                if (DateTime.TryParse(time, out var t) && (DateTime.Now - t).TotalSeconds > 10)
                {
                    await SafeSendReplyAsync(s, "sendlog", true);
                    await CleanDeviceLogs(sn);
                    continue;
                }

                if (enroll == 0 || note.Contains("system boot", StringComparison.OrdinalIgnoreCase))
                {
                    await SafeSendReplyAsync(s, "sendlog", true);
                    continue;
                }

                if (note.Contains("fp verify fail", StringComparison.OrdinalIgnoreCase))
                {
                    await SafeSendReplyAsync(s, "sendlog", true, new { access = 0, message = "Fingerprint Unavailable" });

                    continue;
                }

                if (_pendingEnrollmentsBySn.TryGetValue(sn, out var p))
                {
                    // 1. Timeout guard
                    if (DateTime.UtcNow - p.StartedAt > EnrollmentTimeout)
                    {
                        _pendingEnrollmentsBySn.TryRemove(sn, out _);

                        Log.Information(
                            "[ENROLL] Timeout | EnrollID={EnrollId} | Device={SN}",
                            p.EnrollId, sn
                        );
                        await SendCommandAsync(s, "cleanuser");
                        await SendCommandAsync(s, "cleanlog");

                        await SafeSendReplyAsync(s, "sendlog", true);
                        continue;
                    }

                    // 2. Ignore if no image
                    if (string.IsNullOrEmpty(img))
                        continue;

                    // 3. Persist face shot
                    await _repo.UpsertUserAsync(
                        p.EnrollId,
                        p.UserName,
                        50,
                        p.IsAdmin,
                        img
                    );

                    p.ShotsRemaining--;

                    // 4. Enrollment complete
                    if (p.ShotsRemaining <= 0)
                    {
                        _pendingEnrollmentsBySn.TryRemove(sn, out _);

                        FaceMatch.AddUserToMemory(
                            p.EnrollId,
                            img,
                            p.UserName
                        );

                        await ReplyAccess(s, 0, "Enrollment Complete");

                        if (_adminSessions != null && !_adminSessions.IsEmpty)
                        {
                            foreach (var admin in _adminSessions.Values)
                            {
                                await SafeSendReplyAsync(
                                    admin,
                                    "admin_enroll_complete",
                                    true,
                                    new
                                    {
                                        enrollId = p.EnrollId,
                                        username = p.UserName,
                                        deviceSn = sn
                                    }
                                );
                            }
                        }

                    }
                    else
                    {
                        await ReplyAccess(s, 0, "Next Shot...");
                    }

                    continue;
                }


                if (note.Contains("face not found", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(img))
                {

                    var (m, id, d) = FaceMatch.MatchFaceFromBase64(img);

                    FaceMatch.Users.TryGetValue(id, out var user);

                    await BroadcastToAdminsAsync(new JObject
                    {
                        ["ret"] = "live_scan",

                        ["deviceSn"] = sn,
                        ["deviceIp"] = s.RemoteEndPoint?.ToString(),
                        ["time"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),

                        ["matched"] = m,
                        ["matchScore"] = d,

                        ["enrollId"] = id,
                        ["userName"] = user?.UserName ?? "Unknown",
                        ["isActive"] = user?.IsActive ?? false,
                        ["hasFace"] = user?.HasFace ?? false,


                        ["liveness"] = FaceMatch.LastLivenessResult == null
                            ? null
                            : JObject.FromObject(FaceMatch.LastLivenessResult)
                    });


                    var live = FaceMatch.LastLivenessResult;

                    if (m && FaceMatch.Users.TryGetValue(id, out var u))
                    {
                        if (u.IsActive)
                        {
                            // ACTIVE
                            await ReplyAccess(s, 1, $"Welcome {u.UserName}");
                            _ = _repo.LogAttendanceAsync(id, sn, DateTime.Now, d);


                        }
                        else
                        {
                            // INACTIVE
                            await ReplyAccess(s, 0, $"User inactive: {u.UserName}");

                        }
                    }
                    else
                    {
                        // ❓ NOT FOUND
                        await ReplyAccess(s, 0, "User not found");

                        if (!string.IsNullOrEmpty(img))
                        {
                            var preview = img.Length > 200
                                ? img.Substring(0, 200) + "..."
                                : img;

                            Log.Warning(
                                "[FACE][NOT FOUND] Device={SN} EnrollID={EnrollId} ImageLength={Len}\nBase64Preview={Preview}",
                                sn,
                                id,
                                img.Length,
                                preview
                            );
                        }
                    }

                    await CleanDeviceLogs(sn);
                }

            }
        }

        // ---------------- HELPERS ----------------
        private static async Task BroadcastToAdminsAsync(JObject payload)
        {
            foreach (var admin in _adminSessions.Values)
            {
                await SafeSendReplyAsync(
                    admin,
                    payload.Value<string>("ret") ?? "broadcast",
                    true,
                    payload
                );
            }
        }




        private static WebSocketSession? GetSessionByID(string id) =>
            _deviceToSession.TryGetValue(id, out var sid) && _sessions.TryGetValue(sid, out var s) ? s :
            _sessions.TryGetValue(id, out var s2) ? s2 : null;

        public static bool GetSessionBySN(string sn) => GetSessionByID(sn) != null;

        private static async Task CleanDeviceLogs(string sn)
        {
            var s = GetSessionByID(sn);
            if (s != null)
            {
                await SendCommandAsync(s, "cleanlog");
                await SendCommandAsync(s, "cleanuser");
            }
        }

        private static async Task CleanLogsOnExitAsync()
        {
            foreach (var sn in GetConnectedDeviceSns())
                await CleanDeviceLogs(sn);
        }
        /*
        private static void SaveFacePhoto(int id, string b64)
        {
            try
            {
                var dir = Path.Combine(AppContext.BaseDirectory, "EnrollPhotos");
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(Path.Combine(dir, $"LF{id:D8}.jpg"), Convert.FromBase64String(b64));
            }
            catch { }
        }
        */
        private static async Task SafeSendReplyAsync(WebSocketSession session, string ret, bool result, object? extra = null)
        {
            if (session == null || session.State != SessionState.Connected)
                return;

            try
            {
                var payload = new JObject
                {
                    ["ret"] = ret,
                    ["result"] = result
                };

                if (extra != null)
                {
                    foreach (var p in JObject.FromObject(extra))
                        payload[p.Key] = p.Value;
                }

                await session.SendAsync(
                    payload.ToString(Newtonsoft.Json.Formatting.None)
                );
            }
            catch (InvalidOperationException)
            {
                // Session closed during write — expected race
            }
            catch (Exception ex)
            {
                // Only debug; do not alarm operators
                Log.Debug(ex, "[WS] Suppressed send failure");
            }
        }

        private static async Task SafeSendAsync(WebSocketSession session, JObject payload)
        {
            if (session == null || session.State != SessionState.Connected)
                return;

            try
            {
                await session.SendAsync(payload.ToString(Newtonsoft.Json.Formatting.None));
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[WS] Failed to send message");
            }
        }


        private static async Task SendCommandAsync(WebSocketSession s, string cmd, object? extra = null)
        {
            if (s == null || s.State != SessionState.Connected)
                return;

            var o = JObject.FromObject(extra ?? new object());
            o["cmd"] = cmd;

            await SafeSendReplyAsync(s, cmd, true, o);
        }


        private static async Task ReplyAccess(WebSocketSession s, int access, string msg)
        {
            await SafeSendReplyAsync(s, "sendlog", true, new { access, message = msg, cloudtime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") });
        }

        // ---------------- SET USER ACTIVE ----------------
        //public static async Task<bool> SetUserActiveAsync(int enrollId, bool active)
        //{
        //    // 1. Check if user exists in RAM
        //    if (!FaceMatch.Users.TryGetValue(enrollId, out var user))
        //    {
        //        Log.Information("[SETACTIVE] Failed | User not in memory | EnrollID={EnrollId}", enrollId);
        //        return false;
        //    }


        //    // 2. Update DB
        //    await _repo.SetUserActiveAsync(enrollId, active);

        //    // 3. Update RAM
        //    user.IsActive = active;

        //    Log.Information("[SETACTIVE] Completed | EnrollID={EnrollId} | User={User} | Active={Active}", enrollId, user.UserName, active);

        //    return true;
        //}

        // ---------------- FEATURES CAN BE IMPLEMENTED LATER


        //public static bool CancelEnrollment(string sn)
        //{
        //    if (_pendingEnrollmentsBySn.TryRemove(sn, out var p))
        //    {
        //        Log.Information(
        //            "[ENROLL] Cancelled by operator | EnrollID={EnrollId} | Device={SN}",
        //            p.EnrollId, sn
        //        );
        //        return true;
        //    }

        //    return false;
        //}

        // ----------------  ADMIN HANDLERS ----------------
        private static async Task HandleAdminAddUser(WebSocketSession s, JObject j)
        {
            string sn = j.Value<string>("deviceSn") ?? "";
            string name = j.Value<string>("name") ?? "";
            int isAdmin = j.Value<int?>("isAdmin") ?? 0;

            Log.Information("[ADMIN_ADD] deviceSn='{SN}', name='{Name}', isAdmin={Admin}", sn, name, isAdmin);

            if (!IsDeviceConnected(sn))
            {
                await SafeSendReplyAsync(
                    s,
                    "admin_add_user",
                    false,
                    new { error = $"Device not connected: {sn}" }
                );
                return;
            }

            if (string.IsNullOrEmpty(sn) || string.IsNullOrWhiteSpace(name))
            {
                await SafeSendReplyAsync(
                    s,
                    "admin_add_user",
                    false,
                    new { error = "Invalid parameters" }
                );
                return;
            }

            int enrollId = await FaceMatch.GenerateNextEnrollIdAsync(Program.ConnectionString);


            bool ok = await StartFaceEnrollment(sn, enrollId, name, isAdmin);

            await SafeSendReplyAsync(
                s,
                "admin_add_user",
                ok,
                new
                {
                    enrollId,
                    message = ok
                        ? $"Enrollment started (EnrollID={enrollId}) , Wait for Enrollment Complete before pressing Enter"
                        : "Enrollment failed"
                }
            );
        }

        private static async Task HandleAdminListDevices(WebSocketSession s)
        {
            var devices = GetConnectedDeviceSns();

            await SafeSendReplyAsync(
                s,
                "admin_list_devices",
                true,
                new
                {
                    devices = devices
                }
            );
        }

        private static async Task HandleAdminDeleteUser(WebSocketSession s, JObject j)
        {
            int enrollId = j.Value<int?>("enrollId") ?? 0;

            if (enrollId <= 0)
            {
                await SafeSendReplyAsync(
                    s,
                    "admin_delete_user",
                    false,
                    new { error = "Invalid enrollId" }
                );
                return;
            }
            if (!FaceMatch.Users.TryGetValue(enrollId, out var user))
            {
                Log.Information("[DELETE] User not found | EnrollID={EnrollId}", enrollId);
                await SafeSendReplyAsync(
                    s,
                    "admin_delete_user",
                    false,
                    new { error = $"User {enrollId} not found" }
                );
                return;
            }

            await DeleteUserAsync(enrollId);

            await SafeSendReplyAsync(s,"admin_delete_user",true,new { message = $"User {enrollId} deleted" });
        }

        public static async Task DeleteUserAsync(int enrollId)
        {
            if (!FaceMatch.Users.TryGetValue(enrollId, out var user))
            {
                Log.Information("[DELETE] User not found | EnrollID={EnrollId}", enrollId);

                return;
            }

            await _repo.DeleteUserAsync(enrollId);
            FaceMatch.RemoveUserFromMemory(enrollId);

            Log.Information("[DELETE] Completed | EnrollID={EnrollId} | User={User}",enrollId, user.UserName);
        }

        private static async Task HandleAdminSetActive(WebSocketSession s, JObject j)
        {
            int enrollId = j.Value<int?>("enrollId") ?? 0;
            bool active = j.Value<bool?>("active") ?? true;

            if (enrollId <= 0)
            {
                await SafeSendReplyAsync(
                    s,
                    "admin_set_active",
                    false,
                    new { error = "Invalid enrollId" }
                );
                return;
            }
            if (!FaceMatch.Users.TryGetValue(enrollId, out var user))
            {
                Log.Information("[DELETE] User not found | EnrollID={EnrollId}", enrollId);
                await SafeSendReplyAsync(
                    s,
                    "admin_set_active",
                    false,
                    new { error = $"User {enrollId} not found" }
                );
                return;
            }

            await _repo.SetUserActiveAsync(enrollId, active);

            if (FaceMatch.Users.TryGetValue(enrollId, out var u))
                u.IsActive = active;

            await SafeSendReplyAsync(
                s,
                "admin_set_active",
                true,
                new { message = active ? $"User {enrollId} activated" : $"User {enrollId} deactivated" }
            );
        }

        private static async Task HandleAdminGetUser(WebSocketSession s, JObject j)
        {
            int enrollId = j.Value<int?>("enrollId") ?? 0;

            if (enrollId <= 0)
            {
                await SafeSendReplyAsync(
                    s,
                    "admin_get_user",
                    false,
                    new { error = "Invalid enrollId" }
                );
                return;
            }

            if (!FaceMatch.Users.TryGetValue(enrollId, out var user))
            {
                await SafeSendReplyAsync(
                    s,
                    "admin_get_user",
                    false,
                    new { error = "User not found" }
                );
                return;
            }

            await SafeSendReplyAsync(
                s,
                "admin_get_user",
                true,
                new
                {
                    enrollId = user.EnrollId,
                    userName = user.UserName,
                    hasFace = user.HasFace,
                    isActive = user.IsActive
                }
            );
        }
        //---------------- UTILITIES ----------------
        private static bool IsDeviceConnected(string sn)
        {
            if (string.IsNullOrWhiteSpace(sn))
                return false;

            if (!_deviceToSession.TryGetValue(sn, out var sessionId))
                return false;

            return _sessions.ContainsKey(sessionId);
        }







        // Console wrappers
        /* Move to Admin Client
        public static async Task reboot(string sn) { var s = GetSessionByID(sn); if (s != null) await SendCommandAsync(s, "reboot"); }
        public static async Task settime(string sn) { var s = GetSessionByID(sn); if (s != null) await SendCommandAsync(s, "settime", new { cloudtime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") }); }
        public static async Task cleanlog(string sn) => await CleanDeviceLogs(sn);
        public static async Task cleanuser(string sn) { var s = GetSessionByID(sn); if (s != null) await SendCommandAsync(s, "cleanuser"); }
        */
    }
}
