using System.Diagnostics;
using BJK.Idsrv.Host.Services.AgeAssurance;
using BJK.Modules.User;
using BJK.Modules.User.Infrastructure.AgeAssurance;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace BJK.Idsrv.Host.Tests;

public sealed class YuNetDecoderTests
{
    [Fact]
    public void Decodes_a_face_like_OpenCV_does()
    {
        // A 32 × 32 input: a 4 × 4 grid at stride 8, 2 × 2 at 16, 1 × 1 at 32.
        var outputs = EmptyOutputs(32, 32);
        var idx = (1 * 4) + 2; // row 1, column 2 at stride 8
        outputs["cls_8"][idx] = 1f;
        outputs["obj_8"][idx] = 0.81f;
        outputs["bbox_8"][(idx * 4) + 0] = 0.5f;
        outputs["bbox_8"][(idx * 4) + 1] = 0.5f;
        outputs["bbox_8"][(idx * 4) + 2] = MathF.Log(2f);
        outputs["bbox_8"][(idx * 4) + 3] = MathF.Log(2f);
        outputs["kps_8"][(idx * 10) + 4] = 0.25f; // nose x offset

        var faces = YuNetDecoder.Decode(32, 32, outputs, scoreThreshold: 0.8f);

        var face = Assert.Single(faces);
        Assert.Equal(0.9f, face.Score, 3);
        Assert.Equal(12f, face.X, 3);
        Assert.Equal(4f, face.Y, 3);
        Assert.Equal(16f, face.Width, 3);
        Assert.Equal(16f, face.Height, 3);
        Assert.Equal(new FacePoint(16f, 8f), face.Landmarks[0]);
        Assert.Equal(18f, face.Landmarks[2].X, 3);
    }

    [Fact]
    public void Drops_low_scores_and_overlapping_boxes()
    {
        var outputs = EmptyOutputs(32, 32);
        outputs["cls_8"][5] = 1f;
        outputs["obj_8"][5] = 1f;
        outputs["cls_16"][0] = 1f; // same area, bigger box, slightly lower score
        outputs["obj_16"][0] = 0.9f;
        outputs["bbox_16"][2] = MathF.Log(1.5f);
        outputs["bbox_16"][3] = MathF.Log(1.5f);
        outputs["cls_32"][0] = 0.3f; // too weak
        outputs["obj_32"][0] = 0.3f;

        var faces = YuNetDecoder.Decode(32, 32, outputs, scoreThreshold: 0.5f, nmsThreshold: 0.1f);

        Assert.Single(faces);
        Assert.Equal(1f, faces[0].Score, 3);
    }

    [Fact]
    public void Yaw_is_the_nose_offset_over_the_eye_distance()
    {
        Assert.Equal(0f, Face(yaw: 0f).Yaw, 3);
        Assert.Equal(0.3f, Face(yaw: 0.3f).Yaw, 3);
        Assert.Equal(-0.3f, Face(yaw: -0.3f).Yaw, 3);
    }

    internal static Dictionary<string, float[]> EmptyOutputs(int width, int height)
    {
        var outputs = new Dictionary<string, float[]>();
        foreach (var stride in YuNetDecoder.Strides)
        {
            var cells = (width / stride) * (height / stride);
            outputs[$"cls_{stride}"] = new float[cells];
            outputs[$"obj_{stride}"] = new float[cells];
            outputs[$"bbox_{stride}"] = new float[cells * 4];
            outputs[$"kps_{stride}"] = new float[cells * 10];
        }

        return outputs;
    }

    /// <summary>A 200-pixel-wide face whose eyes are 80 pixels apart, turned by <paramref name="yaw"/>.</summary>
    internal static DetectedFace Face(float yaw, float width = 200f, float score = 0.95f) =>
        new(100, 100, width, width * 1.2f, score,
        [
            new FacePoint(160, 160),
            new FacePoint(240, 160),
            new FacePoint(200 + (yaw * 80), 200),
            new FacePoint(170, 240),
            new FacePoint(230, 240),
        ]);
}

public sealed class HeadTurnAnalyzerTests
{
    private const int FrameWidth = 640;
    private readonly YuNetLivenessOptions _options = new();

    [Fact]
    public void Turning_one_way_then_the_other_is_live()
    {
        var result = Analyse(0f, 0.05f, 0.25f, 0.35f, 0.1f, -0.1f, -0.3f, -0.35f, 0f);

        Assert.True(result.IsLive);
    }

    [Fact]
    public void Turning_right_first_also_counts()
    {
        Assert.True(Analyse(0f, -0.3f, -0.3f, 0f, 0.3f, 0.3f, 0f, 0f).IsLive);
    }

    [Fact]
    public void A_still_face_is_not_live()
    {
        var result = Analyse(0.02f, 0.01f, 0.03f, 0f, 0.02f, 0.01f, 0.02f, 0.01f);

        Assert.False(result.IsLive);
        Assert.Equal(LivenessFailure.NoHeadTurn, result.Failure);
    }

    [Fact]
    public void Turning_only_one_way_is_not_live()
    {
        Assert.Equal(LivenessFailure.NoHeadTurn, Analyse(0f, 0.3f, 0.4f, 0.3f, 0f, 0.05f, 0f, 0f).Failure);
    }

    [Fact]
    public void Too_few_frames_with_a_face_fails()
    {
        var frames = Enumerable.Range(0, 20)
            .Select(i => i < 5 ? Faces(YuNetDecoderTests.Face(i % 2 == 0 ? 0.3f : -0.3f)) : Faces())
            .ToList();

        Assert.Equal(LivenessFailure.NoFace, HeadTurnAnalyzer.Analyse(frames, FrameWidth, _options).Failure);
    }

    [Fact]
    public void Faces_too_far_away_do_not_count()
    {
        var frames = new[] { 0f, 0.3f, -0.3f, 0.3f, -0.3f, 0f, 0f, 0f }
            .Select(y => Faces(YuNetDecoderTests.Face(y, width: 40f)))
            .ToList();

        Assert.Equal(LivenessFailure.NoFace, HeadTurnAnalyzer.Analyse(frames, FrameWidth, _options).Failure);
    }

    [Fact]
    public void Two_people_fails()
    {
        var frames = new[] { 0f, 0.3f, -0.3f, 0.3f, -0.3f, 0f, 0f, 0f }
            .Select(y => Faces(YuNetDecoderTests.Face(y), YuNetDecoderTests.Face(0f)))
            .ToList();

        Assert.Equal(LivenessFailure.MultipleFaces, HeadTurnAnalyzer.Analyse(frames, FrameWidth, _options).Failure);
    }

    [Fact]
    public void No_frames_is_unreadable()
    {
        Assert.Equal(LivenessFailure.Unreadable, HeadTurnAnalyzer.Analyse([], FrameWidth, _options).Failure);
    }

    private LivenessResult Analyse(params float[] yaws) =>
        HeadTurnAnalyzer.Analyse(yaws.Select(y => Faces(YuNetDecoderTests.Face(y))).ToList(), FrameWidth, _options);

    private static IReadOnlyList<DetectedFace> Faces(params DetectedFace[] faces) => faces;
}

public sealed class YuNetLivenessOptionsValidatorTests
{
    [Fact]
    public void The_model_is_required_when_YuNet_is_selected()
    {
        var ageAssurance = new AgeAssuranceOptions();
        ageAssurance.Liveness.Provider = "YuNet";
        var validator = new YuNetLivenessOptionsValidator(Options.Create(ageAssurance));

        var result = validator.Validate(null, new YuNetLivenessOptions { ModelPath = "/nowhere/yunet.onnx" });

        Assert.True(result.Failed);
        Assert.Contains("ModelPath", result.FailureMessage);
    }

    [Fact]
    public void Nothing_is_required_when_another_provider_is_selected()
    {
        var validator = new YuNetLivenessOptionsValidator(Options.Create(new AgeAssuranceOptions()));

        Assert.True(validator.Validate(null, new YuNetLivenessOptions()).Succeeded);
    }
}

/// <summary>
/// Runs the real YuNet model on a real photo, when CI (or a developer) provides them: YUNET_MODEL_PATH (OpenCV Zoo
/// face_detection_yunet_2023mar.onnx) and YUNET_TEST_IMAGE (a photo with one face), plus ffmpeg on the PATH.
/// Skipped otherwise.
/// </summary>
public sealed class YuNetModelTests
{
    private static readonly string? ModelPath = Environment.GetEnvironmentVariable("YUNET_MODEL_PATH");
    private static readonly string? ImagePath = Environment.GetEnvironmentVariable("YUNET_TEST_IMAGE");

    [Fact]
    public async Task Finds_the_face_in_a_still_video_but_says_it_is_not_live()
    {
        Assert.SkipWhen(
            string.IsNullOrEmpty(ModelPath) || !File.Exists(ModelPath) || string.IsNullOrEmpty(ImagePath) || !File.Exists(ImagePath),
            "Set YUNET_MODEL_PATH and YUNET_TEST_IMAGE to run the real model.");

        var video = await MakeVideoAsync(ImagePath!, flipHalfway: false);
        var settings = new YuNetLivenessOptions { ModelPath = ModelPath, MinFaceWidthRatio = 0.02f };
        using var detector = new YuNetFaceDetector(settings);

        var frames = await YuNetLivenessChecker.SampleFramesAsync(
            new SelfieVideo(video, "video/mp4"), detector.Width, detector.Height, settings, TestContext.Current.CancellationToken);
        var faces = frames.Select(f => detector.Detect(f, 0.6f)).ToList();

        Assert.NotEmpty(frames);
        Assert.True(faces.Count(f => f.Count > 0) >= frames.Count / 2, $"A face was found in only {faces.Count(f => f.Count > 0)} of {frames.Count} frames.");
        TestContext.Current.SendDiagnosticMessage(
            "Yaw per frame: " + string.Join(", ", faces.Where(f => f.Count > 0).Select(f => f[0].Yaw.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture))));

        using var checker = new YuNetLivenessChecker(Options.Create(settings), NullLogger<YuNetLivenessChecker>.Instance);
        var result = await checker.CheckAsync(new SelfieVideo(video, "video/mp4"), TestContext.Current.CancellationToken);
        Assert.False(result.IsLive);
    }

    [Fact]
    public async Task Garbage_is_unreadable()
    {
        Assert.SkipWhen(string.IsNullOrEmpty(ModelPath) || !File.Exists(ModelPath), "Set YUNET_MODEL_PATH to run the real model.");

        using var checker = new YuNetLivenessChecker(
            Options.Create(new YuNetLivenessOptions { ModelPath = ModelPath }), NullLogger<YuNetLivenessChecker>.Instance);

        var result = await checker.CheckAsync(new SelfieVideo(new byte[] { 1, 2, 3, 4, 5 }, "video/webm"), TestContext.Current.CancellationToken);

        Assert.Equal(LivenessFailure.Unreadable, result.Failure);
    }

    private static async Task<byte[]> MakeVideoAsync(string image, bool flipHalfway)
    {
        var output = Path.Combine(Path.GetTempPath(), $"yunet-test-{Guid.NewGuid():N}.mp4");
        var start = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[]
                 {
                     "-v", "error", "-loop", "1", "-i", image, "-t", "2", "-r", "10",
                     "-vf", flipHalfway ? "scale=640:-2,hflip" : "scale=640:-2",
                     "-pix_fmt", "yuv420p", "-y", output,
                 })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var errors = await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.True(process.ExitCode == 0, errors);
        try
        {
            return await File.ReadAllBytesAsync(output, TestContext.Current.CancellationToken);
        }
        finally
        {
            File.Delete(output);
        }
    }
}
