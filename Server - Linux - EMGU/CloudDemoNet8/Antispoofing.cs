using System;
using System.Drawing;
using System.IO;
using System.Linq;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Dnn;
using Emgu.CV.Structure;
using Emgu.CV.Util;
using CloudDemoNet8;
using Serilog;

public static class AntiSpoofing
{
    private static readonly object _netLock = new();
    private static Net _net;
    private static bool _isLoaded = false;

    // MiniFASNet expects 112x112 input
    private const int InputSize = 112; //112

    public static void Init(string modelPath)
    {
        if (!File.Exists(modelPath))
            throw new FileNotFoundException("Anti-spoof model not found", modelPath);

        try
        {
            _net = DnnInvoke.ReadNet(modelPath);
            _isLoaded = true;
            Log.Debug($"[LIVENESS] Model loaded: {Path.GetFileName(modelPath)}");
        }
        catch (Exception ex)
        {
            Log.Debug($"[LIVENESS] Failed to load model: {ex.Message}");
        }
    }

    public static float Predict(Mat originalImage, Rectangle faceBox)
    {
        if (!_isLoaded) return 0.0f; // Fail safe

        lock (_netLock)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                // 1. Crop with Scale 2.7x (Model needs background context to spot fakes)
                using var cropped = CropWithScale(originalImage, faceBox, 2.7f); //4
                if (cropped.IsEmpty) return 0.0f;

                // 2. Preprocess (112x112, RGB, 0-1 range)
                using var blob = DnnInvoke.BlobFromImage(
                    cropped,
                    1.0 / 255.0,       // Scale
                    new Size(InputSize, InputSize),
                    new MCvScalar(0, 0, 0),
                    swapRB: true,      // BGR to RGB
                    crop: false
                );

                // 3. Inference
                _net.SetInput(blob);
                using var output = _net.Forward();


                sw.Stop();
                //Log.Information($"[PERF][LIVENESS] {sw.ElapsedMilliseconds} ms");

                // 4. Get Score (Index 1 is Real, Index 0 is Spoof)
                float[] scores = new float[output.SizeOfDimension[1]];
                output.CopyTo(scores);

                float[] probs = Softmax(scores);

                // DEBUG: log everything
                /*
                Log.Information(
                    "[LIVENESS RAW] scores = " +
                    string.Join(", ", scores.Select(s => s.ToString("F3"))) +
                    " | probs = " +
                    string.Join(", ", probs.Select(p => p.ToString("F3")))
                );
                */
                return probs[1]; // Return "Real" probability
            }
            catch (Exception ex)
            {
                Log.Error("[LIVENESS] Error during liveness prediction", ex);
                return 0.0f;
            }
        }
    }

    private static Mat CropWithScale(Mat img, Rectangle face, float scale)
    {
        int centerX = face.X + face.Width / 2;
        int centerY = face.Y + face.Height / 2;
        int newW = (int)(face.Width * scale);
        int newH = (int)(face.Height * scale);

        int x = centerX - newW / 2;
        int y = centerY - newH / 2;

        // Clamp to image borders
        x = Math.Max(0, x);
        y = Math.Max(0, y);
        newW = Math.Min(newW, img.Width - x);
        newH = Math.Min(newH, img.Height - y);

        if (newW <= 0 || newH <= 0) return new Mat();
        return new Mat(img, new Rectangle(x, y, newW, newH));
    }

    private static float[] Softmax(float[] input)
    {
        float[] output = new float[input.Length];
        float max = input.Max();
        float sum = 0.0f;
        for (int i = 0; i < input.Length; i++)
        {
            output[i] = (float)Math.Exp(input[i] - max);
            sum += output[i];
        }
        for (int i = 0; i < input.Length; i++) output[i] /= sum;
        return output;
    }
}