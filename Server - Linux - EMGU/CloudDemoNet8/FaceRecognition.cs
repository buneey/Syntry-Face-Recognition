using CloudDemoNet8;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using MySqlConnector;
using Serilog;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.IO;

public static class FaceMatch
{
    // -------------------------- User Info in RAM -------------------------- //

    public class UserInfo
    {
        public int EnrollId { get; set; }
        public string UserName { get; set; } = "Unknown";
        public bool HasFace { get; set; }
        public bool IsActive { get; set; }
    }

    // Protects the AI
    private static readonly object _aiLock = new();

    // Full user cache: enrollId -> UserInfo
    public static ConcurrentDictionary<int, UserInfo> Users = new();

    // -------------------------- Tunables -------------------------- //

    private const double MatchThreshold = 0.40; // OLD value preserved

    // -------------------------- AI Models -------------------------- //

    private static FaceDetectorYN? _detector;
    private static FaceRecognizerSF? _recognizer;

    // In-Memory DB for embeddings
    private static List<float[]> _knownEmbeddings = new();
    private static List<int> _knownLabels = new();

    private static bool _isLoaded = false;
    private static readonly object _lock = new();
    private static readonly string DebugBasePath = Path.Combine(AppContext.BaseDirectory, "DebugFaces");

    // -------------------------- Enrollment ID Generation -------------------------- //

    public static async Task<int> GenerateNextEnrollIdAsync(string connStr)
    {
        using var conn = new MySqlConnection(connStr);
        await conn.OpenAsync();

        const string sql = @"
        SELECT IFNULL(MAX(enrollid), 999) + 1
        FROM tblusers_face
        FOR UPDATE;
        ";


        using var cmd = new MySqlCommand(sql, conn);
        return (int)await cmd.ExecuteScalarAsync();
    }

    // -------------------------- Init Models -------------------------- //

    public static void InitModels(string detPath, string recPath, string spoofPath)
    {
        _detector?.Dispose();
        _recognizer?.Dispose();

        if (!File.Exists(detPath)) throw new FileNotFoundException(detPath);
        if (!File.Exists(recPath)) throw new FileNotFoundException(recPath);

        _detector = new FaceDetectorYN(detPath, "", new Size(0, 0), 0.8f, 0.3f, 5000);
        _recognizer = new FaceRecognizerSF(recPath, "", 0, 0);

        AntiSpoofing.Init(spoofPath);
        Log.Information("[FACE] Models loaded");
    }

    // -------------------------- Load Embeddings -------------------------- //
    private sealed class RawFaceRow
    {
        public int EnrollId { get; init; }
        public string UserName { get; init; } = "";
        public string Base64 { get; init; } = "";
        public bool IsActive { get; init; }
    }

    public static void LoadEmbeddings()
    {
        LoadEmbeddings(Program.ConnectionString);
    }

    public static void LoadEmbeddings(string connStr)
    {
        if (string.IsNullOrWhiteSpace(connStr))
        {
            Log.Information("[FACE] LoadEmbeddings skipped: empty connection string");
            return;
        }

        var tempEmbeddings = new List<float[]>();
        var tempLabels = new List<int>();
        var tempUsers = new ConcurrentDictionary<int, UserInfo>();

        int totalUsers = 0;
        int processed = 0;
        int loaded = 0;

        List<RawFaceRow> rawRows = new();

        // ============================
        // PHASE 1: FAST DB LOAD (NO AI)
        // ============================
        try
        {
            using var conn = new MySqlConnection(connStr);
            conn.Open();

            const string sqlCount =
                "SELECT COUNT(*) FROM tblusers_face WHERE backupnum = 50 AND record IS NOT NULL";

            using (var countCmd = new MySqlCommand(sqlCount, conn))
                totalUsers = Convert.ToInt32(countCmd.ExecuteScalar());

            Log.Information($"[FACE] Starting embedding refresh for {totalUsers} users...");

            const string sqlFaces = @"
        SELECT enrollid, username, record, isactive
        FROM tblusers_face
        WHERE backupnum = 50 AND record IS NOT NULL";

            using var cmd = new MySqlCommand(sqlFaces, conn) { CommandTimeout = 300 };
            using var reader = cmd.ExecuteReader();

            while (reader.Read())
            {
                if (!int.TryParse(reader["enrollid"]?.ToString(), out int id))
                    continue;

                rawRows.Add(new RawFaceRow
                {
                    EnrollId = id,
                    UserName = reader["username"]?.ToString() ?? "Unknown",
                    Base64 = reader["record"]?.ToString() ?? "",
                    IsActive = Convert.ToInt32(reader["isactive"] ?? 1) == 1
                });
            }
        }
        catch (Exception ex)
        {
            Log.Information($"[FACE] DB Error during Phase 1: {ex.Message}");
            return;
        }

        // ============================
        // PHASE 2: HEAVY PROCESSING
        // ============================
        Log.Information($"[FACE] Processing {rawRows.Count} users (DB closed)...");

        foreach (var row in rawRows)
        {
            processed++;

            using var rawMat = DecodeBase64ToMat(row.Base64);
            if (rawMat.IsEmpty)
                continue;

            float[]? vector = GetFaceFeature(rawMat, null);
            if (vector == null)
            {
                Log.Information($"[FACE] Skipped EnrollID {row.EnrollId} (no valid face)");
                continue;
            }

            tempEmbeddings.Add(vector);
            tempLabels.Add(row.EnrollId);
            loaded++;

            tempUsers[row.EnrollId] = new UserInfo
            {
                EnrollId = row.EnrollId,
                UserName = row.UserName,
                HasFace = true,
                IsActive = row.IsActive
            };

            if (processed % 100 == 0 || processed == rawRows.Count)
            {
                Log.Information($"[FACE] Loaded {processed}/{rawRows.Count} users into RAM (valid: {loaded}).");
            }
        }

        // ============================
        // PHASE 3: ATOMIC SWAP
        // ============================
        lock (_lock)
        {
            _knownEmbeddings = tempEmbeddings;
            _knownLabels = tempLabels;
            Users = tempUsers;
            _isLoaded = true;
        }

        Log.Information($"[FACE] Refreshed {loaded}/{totalUsers} users in RAM (Zero-Downtime).");
    }


    // -------------------------- Sync by Comparison -------------------------- //
    private static readonly SemaphoreSlim _syncLock = new(1, 1);

    public static async Task SyncByComparisonAsync()
    {
        if (!await _syncLock.WaitAsync(0))
            return; // Prevent overlap

        try
        {
            if (string.IsNullOrEmpty(Program.ConnectionString))
                return;

            // 1. LIGHT snapshot
            var dbUsers = new Dictionary<int, bool>();

            using (var conn = new MySqlConnection(Program.ConnectionString))
            {
                await conn.OpenAsync();

                const string sql = @"
                SELECT enrollid, isactive
                FROM tblusers_face
                WHERE backupnum = 50 AND record IS NOT NULL";

                using var cmd = new MySqlCommand(sql, conn)
                {
                    CommandTimeout = 60
                };

                using var reader = await cmd.ExecuteReaderAsync();

                while (await reader.ReadAsync())
                {
                    int id = reader.GetInt32(0);
                    bool active = reader.IsDBNull(1) || reader.GetInt32(1) == 1;
                    dbUsers[id] = active;
                }
            }

            // 2. ADD or UPDATE
            foreach (var kv in dbUsers)
            {
                int id = kv.Key;
                bool dbActive = kv.Value;

                if (!FaceMatch.Users.TryGetValue(id, out var ramUser))
                {
                    await FetchAndAddSingleUserAsync(id);
                    Log.Information("[SYNC] User {ID} added from DB", id);
                }
                else if (ramUser.IsActive != dbActive)
                {
                    ramUser.IsActive = dbActive;
                    Log.Information("[SYNC] User {ID} active updated to {Active}", id, dbActive);
                }
            }

            // 3. REMOVE missing users
            foreach (var ramId in FaceMatch.Users.Keys.ToList())
            {
                if (!dbUsers.ContainsKey(ramId))
                {
                    FaceMatch.RemoveUserFromMemory(ramId);
                    Log.Information("[SYNC] User {ID} removed (DB truth)", ramId);
                }
            }
        }
        catch (MySqlException ex) when (ex.Number == -2)
        {
            // Timeout — expected under load
            Log.Debug("[SYNC] DB timeout — skipping this cycle");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[SYNC] Sync failed");
        }
        finally
        {
            _syncLock.Release();
        }
    }


    private static async Task FetchAndAddSingleUserAsync(int enrollId)
    {
        try
        {
            using var conn = new MySqlConnection(Program.ConnectionString);
            await conn.OpenAsync();

            const string sql = @"
            SELECT username, record, isactive
            FROM tblusers_face
            WHERE enrollid = @id
              AND backupnum = 50
              AND record IS NOT NULL";

            using var cmd = new MySqlCommand(sql, conn)
            {
                CommandTimeout = 60
            };

            cmd.Parameters.AddWithValue("@id", enrollId);

            using var reader = await cmd.ExecuteReaderAsync();

            if (await reader.ReadAsync())
            {
                string name = reader.GetString(0);
                string record = reader.GetString(1);
                bool active = reader.IsDBNull(2) || reader.GetInt32(2) == 1;

                AddUserToMemory(enrollId, record, name, active);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[SYNC] Failed to fetch user {ID}", enrollId);
        }
    }



    // -------------------------- Matching -------------------------- //

    public static (bool FaceMatched, int label, double score) MatchFaceFromBase64(string probeBase64)
    {
        using var probeMat = DecodeBase64ToMat(probeBase64);
        if (probeMat.IsEmpty) return (false, -1, 0);

        float[]? probeVec = GetFaceFeature(probeMat, null, checkLiveness: true);
        if (probeVec == null) return (false, -1, 0);

        int bestId = -1;
        double bestScore = 0;

        lock (_lock)
        {
            for (int i = 0; i < _knownEmbeddings.Count; i++)
            {
                double s = Cosine(probeVec, _knownEmbeddings[i]);
                if (s > bestScore)
                {
                    bestScore = s;
                    bestId = _knownLabels[i];
                }
            }
        }

        return (bestScore > MatchThreshold, bestId, bestScore);
    }

    // -------------------------- Core AI -------------------------- //
    public sealed class LivenessResult
    {
        public float Score { get; init; }
        public float Prob { get; init; }
        public long TimeMs { get; init; }
    }

    public static LivenessResult? LastLivenessResult { get; private set; }
    private static float[]? GetFaceFeature(Mat input, string? dbg, bool checkLiveness = false)
    {
        if (_detector == null || _recognizer == null) return null;

        using var resized = new Mat();
        CvInvoke.Resize(input, resized, new Size(0, 0), 1, 1);

        lock (_aiLock)
        {
            _detector.InputSize = resized.Size;
            using var faces = new Mat();
            _detector.Detect(resized, faces);
            if (faces.Rows == 0) return null;

            using var faceRow = faces.Row(0);
            float[] d = new float[15];
            faceRow.CopyTo(d);
            var rect = new Rectangle((int)d[0], (int)d[1], (int)d[2], (int)d[3]);
            // COMMENT OUT TO DISABLE LIVNESS - HAVE TO COMMENT OUT IN BOTH PROGRAMS
            if (checkLiveness)
            {
                var sw = Stopwatch.StartNew();
                float live = AntiSpoofing.Predict(resized, rect);
                sw.Stop();

                LastLivenessResult = new LivenessResult
                {
                    Score = live,
                    Prob = live, // or separate if you expose both
                    TimeMs = sw.ElapsedMilliseconds
                };

                if (live < 0.30f)
                    return null;
            }


            using var aligned = new Mat();
            _recognizer.AlignCrop(resized, faceRow, aligned);
            using var feat = new Mat();
            _recognizer.Feature(aligned, feat);

            float[] vec = new float[128];
            feat.CopyTo(vec);
            return vec;
        }
    }

    // -------------------------- RAM Operations -------------------------- //

    public static void AddUserToMemory(int id, string b64, string? name = null, bool active = true)
    {
        if (!_isLoaded) LoadEmbeddings();

        using var mat = DecodeBase64ToMat(b64);
        if (mat.IsEmpty) return;

        var vec = GetFaceFeature(mat, null);
        if (vec == null) return;

        lock (_lock)
        {
            int idx = _knownLabels.IndexOf(id);
            if (idx >= 0)
            {
                _knownLabels.RemoveAt(idx);
                _knownEmbeddings.RemoveAt(idx);
            }
            _knownLabels.Add(id);
            _knownEmbeddings.Add(vec);
        }

        Users.AddOrUpdate(id,
            _ => new UserInfo { EnrollId = id, UserName = name ?? "Unknown", HasFace = true, IsActive = active },
            (_, u) => { u.UserName = name ?? u.UserName; u.HasFace = true; u.IsActive = active; return u; });
    }

    public static void RemoveUserFromMemory(int id)
    {
        lock (_lock)
        {
            int idx = _knownLabels.IndexOf(id);
            if (idx >= 0)
            {
                _knownLabels.RemoveAt(idx);
                _knownEmbeddings.RemoveAt(idx);
            }
        }
        Users.TryRemove(id, out _);
    }

    // -------------------------- Helpers -------------------------- //

    private static double Cosine(float[] a, float[] b)
    {
        double dot = 0, ma = 0, mb = 0;
        for (int i = 0; i < a.Length; i++) { dot += a[i] * b[i]; ma += a[i] * a[i]; mb += b[i] * b[i]; }
        return dot / (Math.Sqrt(ma) * Math.Sqrt(mb));
    }

    private static Mat DecodeBase64ToMat(string b64)
    {
        try
        {
            var bytes = Convert.FromBase64String(b64);
            var m = new Mat();
            CvInvoke.Imdecode(bytes, ImreadModes.AnyColor, m);
            return m;
        }
        catch { return new Mat(); }
    }
}
