namespace BJK.Idsrv.Host.Services.AgeAssurance;

/// <summary>A point in frame pixels.</summary>
/// <param name="X">Horizontal position.</param>
/// <param name="Y">Vertical position.</param>
public readonly record struct FacePoint(float X, float Y);

/// <summary>
/// A face found by YuNet: its box, confidence and five landmarks, in this order: right eye, left eye, nose tip,
/// right mouth corner, left mouth corner (the person's right and left).
/// </summary>
/// <param name="X">Left edge of the box.</param>
/// <param name="Y">Top edge of the box.</param>
/// <param name="Width">Box width.</param>
/// <param name="Height">Box height.</param>
/// <param name="Score">Confidence, 0 to 1.</param>
/// <param name="Landmarks">The five landmarks.</param>
public sealed record DetectedFace(float X, float Y, float Width, float Height, float Score, IReadOnlyList<FacePoint> Landmarks)
{
    /// <summary>
    /// How far the head is turned, from the landmarks: the nose tip's sideways offset from the midpoint between the eyes,
    /// divided by the distance between the eyes. About 0 facing the camera; roughly ±0.2 to ±0.5 for a clear turn.
    /// The sign says which way, but not reliably which is "left" (front cameras may mirror), so only changes matter.
    /// </summary>
    public float Yaw
    {
        get
        {
            var rightEye = Landmarks[0];
            var leftEye = Landmarks[1];
            var nose = Landmarks[2];
            var eyeDistance = Math.Abs(leftEye.X - rightEye.X);
            return eyeDistance < 1e-3f ? 0f : (nose.X - ((rightEye.X + leftEye.X) / 2f)) / eyeDistance;
        }
    }
}

/// <summary>
/// Turns YuNet's raw outputs (OpenCV Zoo face_detection_yunet_2023mar.onnx) into faces, the same way OpenCV's
/// FaceDetectorYN does: for each stride (8, 16, 32) and grid cell, score = sqrt(cls × obj), box and landmarks
/// relative to the cell, then non-maximum suppression.
/// </summary>
public static class YuNetDecoder
{
    /// <summary>The model's feature strides.</summary>
    public static readonly int[] Strides = [8, 16, 32];

    /// <summary>The faces in one frame, best first.</summary>
    /// <param name="width">Model input width (a multiple of 32).</param>
    /// <param name="height">Model input height (a multiple of 32).</param>
    /// <param name="outputs">Output tensors by name: cls_8, obj_8, bbox_8, kps_8 and the same for 16 and 32.</param>
    /// <param name="scoreThreshold">Lowest score kept.</param>
    /// <param name="nmsThreshold">Overlap (IoU) above which the weaker of two boxes is dropped.</param>
    /// <param name="topK">Most faces returned.</param>
    public static IReadOnlyList<DetectedFace> Decode(
        int width,
        int height,
        IReadOnlyDictionary<string, float[]> outputs,
        float scoreThreshold,
        float nmsThreshold = 0.3f,
        int topK = 20)
    {
        var candidates = new List<DetectedFace>();
        foreach (var stride in Strides)
        {
            var cols = width / stride;
            var rows = height / stride;
            var cls = outputs[$"cls_{stride}"];
            var obj = outputs[$"obj_{stride}"];
            var bbox = outputs[$"bbox_{stride}"];
            var kps = outputs[$"kps_{stride}"];

            for (var r = 0; r < rows; r++)
            {
                for (var c = 0; c < cols; c++)
                {
                    var idx = (r * cols) + c;
                    if (idx >= cls.Length || idx >= obj.Length)
                    {
                        continue;
                    }

                    var score = MathF.Sqrt(Math.Clamp(cls[idx], 0f, 1f) * Math.Clamp(obj[idx], 0f, 1f));
                    if (score < scoreThreshold)
                    {
                        continue;
                    }

                    var cx = (c + bbox[(idx * 4) + 0]) * stride;
                    var cy = (r + bbox[(idx * 4) + 1]) * stride;
                    var w = MathF.Exp(bbox[(idx * 4) + 2]) * stride;
                    var h = MathF.Exp(bbox[(idx * 4) + 3]) * stride;

                    var landmarks = new FacePoint[5];
                    for (var n = 0; n < 5; n++)
                    {
                        landmarks[n] = new FacePoint(
                            (kps[(idx * 10) + (2 * n)] + c) * stride,
                            (kps[(idx * 10) + (2 * n) + 1] + r) * stride);
                    }

                    candidates.Add(new DetectedFace(cx - (w / 2f), cy - (h / 2f), w, h, score, landmarks));
                }
            }
        }

        return Suppress(candidates, nmsThreshold, topK);
    }

    private static List<DetectedFace> Suppress(List<DetectedFace> candidates, float nmsThreshold, int topK)
    {
        var kept = new List<DetectedFace>();
        foreach (var face in candidates.OrderByDescending(f => f.Score))
        {
            if (kept.All(k => IntersectionOverUnion(k, face) <= nmsThreshold))
            {
                kept.Add(face);
                if (kept.Count == topK)
                {
                    break;
                }
            }
        }

        return kept;
    }

    private static float IntersectionOverUnion(DetectedFace a, DetectedFace b)
    {
        var x1 = Math.Max(a.X, b.X);
        var y1 = Math.Max(a.Y, b.Y);
        var x2 = Math.Min(a.X + a.Width, b.X + b.Width);
        var y2 = Math.Min(a.Y + a.Height, b.Y + b.Height);
        var intersection = Math.Max(0f, x2 - x1) * Math.Max(0f, y2 - y1);
        var union = (a.Width * a.Height) + (b.Width * b.Height) - intersection;
        return union <= 0f ? 0f : intersection / union;
    }
}
