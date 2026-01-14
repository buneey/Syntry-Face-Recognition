using System;
using System.IO;
using System.Linq;
using OpenCvSharp;
using OpenCvSharp.Dnn;
using Serilog;

public static class AntiSpoofing
{
    private static readonly object _netLock = new();
    private static Net? _net;
    private static bool _isLoaded = false;

    // MiniFASNet expects 112x112
    private const int InputSize = 112;

    // =========================
    // INIT
    // =========================
    public static void Init(string modelPath)
    {
        if (!File.Exists(modelPath))
            throw new FileNotFoundException("Anti-spoof model not found", modelPath);

        try
        {
            _net = CvDnn.ReadNetFromOnnx(modelPath);
            _net.SetPreferableBackend(Backend.OPENCV);
            _net.SetPreferableTarget(Target.CPU);

            _isLoaded = true;
            Log.Information("[LIVENESS] Model loaded: {Model}", Path.GetFileName(modelPath));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[LIVENESS] Failed to load model");
        }
    }

    // =========================
    // PREDICT
    // =========================
    public static float Predict(Mat image, Rect faceRect)
    {
        if (!_isLoaded || _net == null)
            return 0.0f; // fail-safe

        lock (_netLock)
        {
            try
            {
                // 1. Crop with scale (background context)
                using var cropped = CropWithScale(image, faceRect, 2.7f);
                if (cropped.Empty())
                    return 0.0f;

                // 2. Preprocess (112x112, RGB, 0–1)
                using var blob = CvDnn.BlobFromImage(
                    cropped,
                    1.0 / 255.0,
                    new Size(InputSize, InputSize),
                    new Scalar(0, 0, 0),
                    swapRB: true,
                    crop: false
                );

                // 3. Inference
                _net.SetInput(blob);
                using var output = _net.Forward();

                // 4. Extract raw scores
                output.GetArray<float>(out var scores);

                var probs = Softmax(scores);

                // Index 1 = "Real", Index 0 = "Spoof"
                return probs.Length > 1 ? probs[1] : 0.0f;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[LIVENESS] Error during prediction");
                return 0.0f;
            }
        }
    }

    // =========================
    // HELPERS
    // =========================
    private static Mat CropWithScale(Mat img, Rect face, float scale)
    {
        int centerX = face.X + face.Width / 2;
        int centerY = face.Y + face.Height / 2;

        int newW = (int)(face.Width * scale);
        int newH = (int)(face.Height * scale);

        int x = centerX - newW / 2;
        int y = centerY - newH / 2;

        x = Math.Max(0, x);
        y = Math.Max(0, y);

        newW = Math.Min(newW, img.Width - x);
        newH = Math.Min(newH, img.Height - y);

        if (newW <= 0 || newH <= 0)
            return new Mat();

        return new Mat(img, new Rect(x, y, newW, newH));
    }

    private static float[] Softmax(float[] input)
    {
        float max = input.Max();
        float sum = 0f;

        var output = new float[input.Length];
        for (int i = 0; i < input.Length; i++)
        {
            output[i] = (float)Math.Exp(input[i] - max);
            sum += output[i];
        }

        for (int i = 0; i < output.Length; i++)
            output[i] /= sum;

        return output;
    }
}
