using CloudDemoNet8;
using Microsoft.Data.SqlClient;
using OpenCvSharp;
using OpenCvSharp.Dnn;
using Serilog;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;

public static class FaceMatch
{
    // -------------------------- User Info in RAM -------------------------- //
    private static readonly SemaphoreSlim _syncLock = new(1, 1);

    public class UserInfo
    {
        public int EnrollId { get; set; }
        public string UserName { get; set; } = "Unknown";
        public bool HasFace { get; set; }
        public bool IsActive { get; set; }
    }

    private static readonly object _aiLock = new();
    public static ConcurrentDictionary<int, UserInfo> Users = new();

    private const double MatchThreshold = 0.30;

    // -------------------------- OpenCV Models -------------------------- //

    private static Net? _detector;
    private static Net? _recognizer;

    private static List<float[]> _knownEmbeddings = new();
    private static List<int> _knownLabels = new();

    private static bool _isLoaded = false;
    private static readonly object _lock = new();

    // -------------------------- Enrollment ID -------------------------- //

    public static async Task<int> GenerateNextEnrollIdAsync(string connStr)
    {
        using var conn = new SqlConnection(connStr);
        await conn.OpenAsync();

        const string sql = @"
            SELECT ISNULL(MAX(enrollid), 999) + 1
            FROM tblusers_face WITH (UPDLOCK, HOLDLOCK);";

        using var cmd = new SqlCommand(sql, conn);
        return (int)await cmd.ExecuteScalarAsync();
    }

    // -------------------------- Init Models -------------------------- //

    public static void InitModels(string detPath, string recPath, string spoofPath)
    {
        _detector?.Dispose();
        _recognizer?.Dispose();

        if (!File.Exists(detPath)) throw new FileNotFoundException(detPath);
        if (!File.Exists(recPath)) throw new FileNotFoundException(recPath);

        _detector = CvDnn.ReadNetFromOnnx(detPath);
        _recognizer = CvDnn.ReadNetFromOnnx(recPath);

        _detector.SetPreferableBackend(Backend.OPENCV);
        _detector.SetPreferableTarget(Target.CPU);
        _recognizer.SetPreferableBackend(Backend.OPENCV);
        _recognizer.SetPreferableTarget(Target.CPU);

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

    public static void LoadEmbeddings() => LoadEmbeddings(Program.ConnectionString);

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

        List<RawFaceRow> rawRows = new();
        int totalUsers = 0, processed = 0, loaded = 0;

        try
        {
            using var conn = new SqlConnection(connStr);
            conn.Open();

            using (var countCmd = new SqlCommand(
                "SELECT COUNT(*) FROM tblusers_face WHERE backupnum = 50 AND record IS NOT NULL", conn))
                totalUsers = (int)countCmd.ExecuteScalar();

            const string sql = @"
                SELECT enrollid, username, record, isactive
                FROM tblusers_face
                WHERE backupnum = 50 AND record IS NOT NULL";

            using var cmd = new SqlCommand(sql, conn);
            using var reader = cmd.ExecuteReader();

            while (reader.Read())
            {
                if (!int.TryParse(reader["enrollid"]?.ToString(), out int id)) continue;

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
            Log.Error(ex, "[FACE] DB Error");
            return;
        }

        foreach (var row in rawRows)
        {
            processed++;

            using var mat = DecodeBase64ToMat(row.Base64);
            if (mat.Empty()) continue;

            var vec = GetFaceFeature(mat, null);
            if (vec == null) continue;

            tempEmbeddings.Add(vec);
            tempLabels.Add(row.EnrollId);
            loaded++;

            tempUsers[row.EnrollId] = new UserInfo
            {
                EnrollId = row.EnrollId,
                UserName = row.UserName,
                HasFace = true,
                IsActive = row.IsActive
            };
        }

        lock (_lock)
        {
            _knownEmbeddings = tempEmbeddings;
            _knownLabels = tempLabels;
            Users = tempUsers;
            _isLoaded = true;
        }

        Log.Information($"[FACE] Refreshed {loaded}/{totalUsers} users in RAM");
    }

    // -------------------------- Matching -------------------------- //

    public static (bool FaceMatched, int label, double score) MatchFaceFromBase64(string probeBase64)
    {
        using var probeMat = DecodeBase64ToMat(probeBase64);
        if (probeMat.Empty()) return (false, -1, 0);

        var probeVec = GetFaceFeature(probeMat, null, false);
        if (probeVec == null) return (false, -1, 0);

        int bestId = -1;
        double bestScore = 0;

        lock (_lock)
        {
            for (int i = 0; i < _knownEmbeddings.Count; i++)
            {
                double s = Cosine(probeVec, _knownEmbeddings[i]);

                Log.Debug(
                    "[FACE][MATCH] Compare EnrollID={ID} Score={Score:F4}",
                    _knownLabels[i],
                    s
                );

                if (s > bestScore)
                {
                    bestScore = s;
                    bestId = _knownLabels[i];
                }

                Log.Information(
                    "[FACE][MATCH] BestMatch EnrollID={ID} Score={Score:F4} Threshold={Th}",
                    bestId,
                    bestScore,
                    MatchThreshold
                );


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

        lock (_aiLock)
        {
            using var blob = CvDnn.BlobFromImage(
                input,
                1.0,
                new Size(320, 320),
                new Scalar(0, 0, 0),
                swapRB: true,
                crop: false
            );

            _detector.SetInput(blob);   // ✅ THIS WAS MISSING
            using var det = _detector.Forward();

            Log.Debug(
                "[FACE][DETECT] Output dims={Dims}, sizes=[{S0},{S1},{S2},{S3}]",
                det.Dims,
                det.Size(0),
                det.Size(1),
                det.Size(2),
                det.Size(3)
            );

            Log.Warning(
    "[FACE][DETECT] Raw output: Dims={Dims}, Shape={Shape}",
    det.Dims,
    string.Join("x", Enumerable.Range(0, det.Dims).Select(i => det.Size(i)))
);


            // Expecting [1,1,N,7]
            if (det.Dims != 4 || det.Size(3) != 7)
            {
                Log.Warning("[FACE][DETECT] Unexpected detector output shape");
                return null;
            }

            int detections = det.Size(2);
            Log.Debug("[FACE][DETECT] Total detections={Count}", detections);

            int best = -1;
            float bestScore = 0f;

            for (int i = 0; i < detections; i++)
            {
                float score = det.At<float>(0, 0, i, 2);
                Log.Debug("[FACE][DETECT] Face#{Idx} score={Score:F3}", i, score);

                if (score > bestScore)
                {
                    bestScore = score;
                    best = i;
                }
            }

            if (best < 0)
            {
                Log.Warning("[FACE][DETECT] No valid faces detected");
                return null;
            }

            if (bestScore < 0.6f)
            {
                Log.Warning(
                    "[FACE][DETECT] Best face rejected (score={Score:F3})",
                    bestScore
                );
                return null;
            }

            float x1 = det.At<float>(0, 0, best, 3) * input.Width;
            float y1 = det.At<float>(0, 0, best, 4) * input.Height;
            float x2 = det.At<float>(0, 0, best, 5) * input.Width;
            float y2 = det.At<float>(0, 0, best, 6) * input.Height;

            int x = (int)x1;
            int y = (int)y1;
            int w = (int)(x2 - x1);
            int h = (int)(y2 - y1);

            // Clamp
            x = Math.Max(0, x);
            y = Math.Max(0, y);
            w = Math.Min(w, input.Width - x);
            h = Math.Min(h, input.Height - y);

            if (w <= 0 || h <= 0)
            {
                Log.Warning(
                    "[FACE][ROI] Invalid ROI x={X} y={Y} w={W} h={H}",
                    x, y, w, h
                );
                return null;
            }

            var r = new Rect(x, y, w, h);

            Log.Debug(
                "[FACE][ROI] Using ROI x={X} y={Y} w={W} h={H}",
                x, y, w, h
            );


            if (checkLiveness)
            {
                var sw = Stopwatch.StartNew();
                float live = AntiSpoofing.Predict(input, r);
                sw.Stop();

                LastLivenessResult = new LivenessResult
                {
                    Score = live,
                    Prob = live,
                    TimeMs = sw.ElapsedMilliseconds
                };

                Log.Debug(
                    "[LIVENESS] Score={Score:F3} Time={Ms}ms",
                    live,
                    sw.ElapsedMilliseconds
                );

                if (live < 0.30f)
                {
                    Log.Warning("[LIVENESS] Rejected by liveness");
                    return null;
                }
            }


            using var face = new Mat(input, r);
            Cv2.Resize(face, face, new Size(112, 112));

            using var recBlob = CvDnn.BlobFromImage(
                face,
                1.0 / 255.0,
                new Size(112, 112),
                new Scalar(0, 0, 0),
                swapRB: true,
                crop: false
            );

            _recognizer.SetInput(recBlob);
            using var output = _recognizer.Forward();
            output.GetArray<float>(out var vector);

            if (vector == null || vector.Length == 0)
            {
                Log.Warning("[FACE][EMBED] Empty embedding vector");
                return null;
            }

            Log.Debug(
                "[FACE][EMBED] Vector length={Len} FirstVal={Val:F4}",
                vector.Length,
                vector[0]
            );

            return Normalize(vector);
        }
    }

    // -------------------------- Helpers -------------------------- //

    private static double Cosine(float[] a, float[] b)
    {
        double dot = 0, ma = 0, mb = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            ma += a[i] * a[i];
            mb += b[i] * b[i];
        }
        return dot / (Math.Sqrt(ma) * Math.Sqrt(mb));
    }

    private static float[] Normalize(float[] v)
    {
        float norm = 0f;
        for (int i = 0; i < v.Length; i++)
            norm += v[i] * v[i];

        norm = (float)Math.Sqrt(norm);
        if (norm < 1e-6f) return v;

        for (int i = 0; i < v.Length; i++)
            v[i] /= norm;

        return v;
    }

    public static Mat DecodeBase64ToMat(string b64)
    {
        try
        {
            var bytes = Convert.FromBase64String(b64);
            return Cv2.ImDecode(bytes, ImreadModes.Color);
        }
        catch
        {
            return new Mat();
        }
    }

    // -------------------------- Memory -------------------------- //
    public static void AddUserToMemory(
    int id,
    string base64Image,
    string? name = null,
    bool active = true)
    {
        if (!_isLoaded)
            LoadEmbeddings();

        using var mat = DecodeBase64ToMat(base64Image);
        if (mat.Empty())
            return;

        var vec = GetFaceFeature(mat, null);
        if (vec == null)
            return;

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

        Users.AddOrUpdate(
            id,
            _ => new UserInfo
            {
                EnrollId = id,
                UserName = name ?? "Unknown",
                HasFace = true,
                IsActive = active
            },
            (_, u) =>
            {
                u.UserName = name ?? u.UserName;
                u.HasFace = true;
                u.IsActive = active;
                return u;
            });
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
    private static async Task FetchAndAddSingleUserAsync(int enrollId)
    {
        try
        {
            using var conn = new SqlConnection(Program.ConnectionString);
            await conn.OpenAsync();

            const string sql = @"
            SELECT username, record, isactive
            FROM tblusers_face
            WHERE enrollid = @id
              AND backupnum = 50
              AND record IS NOT NULL";

            using var cmd = new SqlCommand(sql, conn)
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

    public static async Task SyncByComparisonAsync()
    {
        if (!await _syncLock.WaitAsync(0))
            return; // Prevent overlap

        try
        {
            if (string.IsNullOrEmpty(Program.ConnectionString))
                return;

            // 1. LIGHT snapshot from DB
            var dbUsers = new Dictionary<int, bool>();

            using (var conn = new SqlConnection(Program.ConnectionString))
            {
                await conn.OpenAsync();

                const string sql = @"
                SELECT enrollid, isactive
                FROM tblusers_face
                WHERE backupnum = 50 AND record IS NOT NULL";

                using var cmd = new SqlCommand(sql, conn)
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

            // 2. ADD or UPDATE users
            foreach (var kv in dbUsers)
            {
                int id = kv.Key;
                bool dbActive = kv.Value;

                if (!Users.TryGetValue(id, out var ramUser))
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
            foreach (var ramId in Users.Keys.ToList())
            {
                if (!dbUsers.ContainsKey(ramId))
                {
                    RemoveUserFromMemory(ramId);
                    Log.Information("[SYNC] User {ID} removed (DB truth)", ramId);
                }
            }
        }
        catch (SqlException ex) when (ex.Number == -2)
        {
            // Expected timeout under load
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



}
