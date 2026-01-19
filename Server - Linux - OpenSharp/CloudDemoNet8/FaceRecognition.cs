using CloudDemoNet8;
using OpenCvSharp;
using OpenCvSharp.Dnn;
using Microsoft.Data.SqlClient;
using Serilog;
using System.Collections.Concurrent;
using System.Diagnostics;
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

    private static readonly object _aiLock = new();
    private static readonly object _lock = new();

    public static ConcurrentDictionary<int, UserInfo> Users = new();

    private const double MatchThreshold = 0.40;

    // -------------------------- DNN MODELS -------------------------- //

    private static Net? _detector;
    private static Net? _recognizer;

    private static List<float[]> _knownEmbeddings = new();
    private static List<int> _knownLabels = new();

    private static bool _isLoaded = false;

    // -------------------------- INIT -------------------------- //

    public static void InitModels(string detPath, string recPath, string spoofPath)
    {
        if (!File.Exists(detPath)) throw new FileNotFoundException(detPath);
        if (!File.Exists(recPath)) throw new FileNotFoundException(recPath);

        _detector = CvDnn.ReadNetFromOnnx(detPath);
        _recognizer = CvDnn.ReadNetFromOnnx(recPath);

        _detector.SetPreferableBackend(Backend.OPENCV);
        _detector.SetPreferableTarget(Target.CPU);

        _recognizer.SetPreferableBackend(Backend.OPENCV);
        _recognizer.SetPreferableTarget(Target.CPU);

        AntiSpoofing.Init(spoofPath);
        Log.Information("[FACE] DNN models loaded");

        Log.Warning(
                "Detector model: {Path}",
                detPath
        );
    }

    // -------------------------- CORE FEATURE -------------------------- //

    private static float[]? GetFaceFeature(Mat input, string? dbg, bool checkLiveness = false)
    {
        if (_detector == null || _recognizer == null)
            return null;

        lock (_aiLock)
        {
            const int YUNET_SIZE = 320;

            using var resized = new Mat();
            Cv2.Resize(input, resized, new Size(YUNET_SIZE, YUNET_SIZE));

            using var blob = CvDnn.BlobFromImage(
                resized,
                1.0,
                new Size(YUNET_SIZE, YUNET_SIZE),
                new Scalar(0, 0, 0),
                swapRB: true,
                crop: false
            );

            _detector.SetInput(blob);
            using var det = _detector.Forward();

            if (det.Dims != 3 || det.Size(2) != 15)
            {
                Log.Warning(
                    "[FACE][DETECT] Invalid YuNet output: Dims={Dims}, Shape=1x{N}x{C}",
                    det.Dims,
                    det.Size(1),
                    det.Size(2)
                );
                return null;
            }

            int bestIdx = -1;
            float bestScore = 0f;

            for (int i = 0; i < det.Size(1); i++)
            {
                float score = det.At<float>(0, i, 4);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestIdx = i;
                }
            }

            if (bestIdx < 0 || bestScore < 0.6f)
                return null;

            float sx = (float)input.Width / YUNET_SIZE;
            float sy = (float)input.Height / YUNET_SIZE;

            int x = (int)(det.At<float>(0, bestIdx, 0) * sx);
            int y = (int)(det.At<float>(0, bestIdx, 1) * sy);
            int w = (int)(det.At<float>(0, bestIdx, 2) * sx);
            int h = (int)(det.At<float>(0, bestIdx, 3) * sy);

            x = Math.Clamp(x, 0, input.Width - 1);
            y = Math.Clamp(y, 0, input.Height - 1);
            w = Math.Clamp(w, 1, input.Width - x);
            h = Math.Clamp(h, 1, input.Height - y);

            var rect = new Rect(x, y, w, h);

            if (checkLiveness)
            {
                var sw = Stopwatch.StartNew();
                float live = AntiSpoofing.Predict(input, rect);
                sw.Stop();

                LastLivenessResult = new LivenessResult
                {
                    Score = live,
                    Prob = live,
                    TimeMs = sw.ElapsedMilliseconds
                };

                if (live < 0.30f)
                    return null;
            }

            using var face = new Mat(input, rect);
            Cv2.Resize(face, face, new Size(112, 112));

            using var recBlob = CvDnn.BlobFromImage(
                face,
                1.0 / 255.0,
                new Size(112, 112),
                new Scalar(),
                swapRB: true,
                crop: false
            );

            _recognizer.SetInput(recBlob);
            using var feat = _recognizer.Forward();

            feat.GetArray(out float[] vec);
            Normalize(vec);

            return vec;
        }
    }



    // -------------------------- MATCH -------------------------- //

    public static (bool FaceMatched, int label, double score) MatchFaceFromBase64(string probeBase64)
    {
        using var probeMat = DecodeBase64ToMat(probeBase64);
        if (probeMat.Empty()) return (false, -1, 0);

        var probeVec = GetFaceFeature(probeMat, null, true);
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

    // -------------------------- RAM OPS -------------------------- //

    public static void AddUserToMemory(int id, string b64, string? name = null, bool active = true)
    {
        if (!_isLoaded) LoadEmbeddings();

        using var mat = DecodeBase64ToMat(b64);
        if (mat.Empty()) return;

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

    // -------------------------- HELPERS -------------------------- //

    public sealed class LivenessResult
    {
        public float Score { get; init; }
        public float Prob { get; init; }
        public long TimeMs { get; init; }
    }

    public static LivenessResult? LastLivenessResult { get; private set; }

    private static void Normalize(float[] v)
    {
        float sum = 0;
        foreach (var f in v) sum += f * f;
        sum = (float)Math.Sqrt(sum);
        for (int i = 0; i < v.Length; i++) v[i] /= sum;
    }

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

    private static Mat DecodeBase64ToMat(string b64)
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

    // -------------------------- LOAD EMBEDDINGS (UNCHANGED) -------------------------- //
    public static void LoadEmbeddings() => LoadEmbeddings(Program.ConnectionString);

    public static void LoadEmbeddings(string connStr)
    {
        // YOUR ORIGINAL DB LOGIC REMAINS EXACTLY THE SAME
        // (omitted here for brevity – keep your existing implementation)
        _isLoaded = true;
    }
}
