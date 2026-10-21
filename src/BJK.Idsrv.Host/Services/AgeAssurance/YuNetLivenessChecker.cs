using System.Diagnostics;
using BJK.Modules.User.Infrastructure.AgeAssurance;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace BJK.Idsrv.Host.Services.AgeAssurance;

/// <summary>
/// Section <c>AgeAssurance:Liveness</c>. The YuNet check runs when <see cref="ModelPath"/> is set; without it the
/// User module's stand-in is used locally, and deployed environments refuse to start.
/// </summary>
public sealed class YuNetLivenessOptions
{
    public const string SectionName = "AgeAssurance:Liveness";

    /// <summary>
    /// Path to OpenCV Zoo's face_detection_yunet_2023mar.onnx (MIT licence, about 230 KB). Set: the real check runs.
    /// The setup downloads it; see docs/local-development.md.
    /// </summary>
    public string? ModelPath { get; set; }

    /// <summary>ffmpeg, used to read frames from the video. Default <c>ffmpeg</c> on the PATH.</summary>
    public string FfmpegPath { get; set; } = "ffmpeg";

    /// <summary>Frames sampled per second of video. Default 8.</summary>
    public int FramesPerSecond { get; set; } = 8;

    /// <summary>Only this much of the video is read. Default 8 seconds.</summary>
    public int MaxSeconds { get; set; } = 8;

    /// <summary>Frame size given to the model when it accepts any size (multiples of 32). Default 640 × 480.</summary>
    public int InputWidth { get; set; } = 640;

    /// <summary>See <see cref="InputWidth"/>.</summary>
    public int InputHeight { get; set; } = 480;

    /// <summary>Lowest face score counted. Default 0.8.</summary>
    public float ScoreThreshold { get; set; } = 0.8f;

    /// <summary>Share of sampled frames that must show the face. Default 0.6.</summary>
    public double MinFaceFrameRatio { get; set; } = 0.6;

    /// <summary>At least this many frames must show the face. Default 6.</summary>
    public int MinFaceFrames { get; set; } = 6;

    /// <summary>Smallest face width counted, as a share of the frame width. Default 0.15.</summary>
    public float MinFaceWidthRatio { get; set; } = 0.15f;

    /// <summary>Yaw (see <see cref="DetectedFace.Yaw"/>) that counts as a clear turn each way. Default 0.18.</summary>
    public float YawThreshold { get; set; } = 0.18f;

    /// <summary>Where the video is written for ffmpeg, and deleted straight after. Default the system temp folder.</summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>Longest ffmpeg may take. Default 30 seconds.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Open-source liveness check that runs on our own servers: ffmpeg samples frames from the selfie video, OpenCV Zoo's
/// YuNet face detector (ONNX Runtime) finds the face and its landmarks in each, and <see cref="HeadTurnAnalyzer"/>
/// checks that one person turned their head one way and then the other.
/// </summary>
/// <remarks>
/// This is a basic check. It stops a photo held up to the camera and a face that never moves. It doesn't stop a
/// replayed video of someone turning their head, or a good mask. Adding a passive anti-spoofing model (for example
/// MiniFASNet from Silent-Face-Anti-Spoofing, Apache-2.0) is the next step before relying on it alone.
/// The video is written to a temp file only because ffmpeg needs a seekable file for MP4. The file is deleted in a
/// finally block straight after the frames are read.
/// </remarks>
public sealed class YuNetLivenessChecker(
    IOptions<YuNetLivenessOptions> options,
    ILogger<YuNetLivenessChecker> logger)
    : ILivenessChecker, IDisposable
{
    public const string ProviderName = "YuNet";

    private readonly Lazy<YuNetFaceDetector> _detector = new(() => new YuNetFaceDetector(options.Value));

    public string Name => ProviderName;

    public bool IsDevelopmentOnly => false;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(options.Value.ModelPath) && !options.Value.ModelPath.TrimStart().StartsWith('<');

    public async Task<LivenessResult> CheckAsync(SelfieVideo video, CancellationToken cancellationToken)
    {
        if (video.Content.IsEmpty)
        {
            return LivenessResult.Failed(LivenessFailure.Unreadable);
        }

        var settings = options.Value;
        var detector = _detector.Value;
        IReadOnlyList<byte[]> frames;
        try
        {
            frames = await SampleFramesAsync(video, detector.Width, detector.Height, settings, cancellationToken);
        }
        catch (Exception ex) when (ex is FfmpegException or TimeoutException)
        {
            // The reason only; never anything about the video.
            logger.LogWarning("Liveness check couldn't read the video: {Reason}", ex.Message);
            return LivenessResult.Failed(LivenessFailure.Unreadable);
        }

        try
        {
            var faces = frames.Select(f => detector.Detect(f, settings.ScoreThreshold)).ToList();
            var result = HeadTurnAnalyzer.Analyse(faces, detector.Width, settings);
            logger.LogInformation(
                "Liveness check: {Frames} frames, {FaceFrames} with a face, result {Result}.",
                frames.Count,
                faces.Count(f => f.Count > 0),
                result.IsLive ? "live" : result.Failure.ToString());
            return result;
        }
        finally
        {
            foreach (var frame in frames)
            {
                Array.Clear(frame);
            }
        }
    }

    /// <summary>Raw BGR frames, letterboxed to the model's input size.</summary>
    internal static async Task<IReadOnlyList<byte[]>> SampleFramesAsync(
        SelfieVideo video,
        int width,
        int height,
        YuNetLivenessOptions settings,
        CancellationToken cancellationToken)
    {
        var folder = string.IsNullOrWhiteSpace(settings.WorkingDirectory) ? Path.GetTempPath() : settings.WorkingDirectory;
        var path = Path.Combine(folder, $"bjk-age-{Guid.NewGuid():N}.video");
        try
        {
            await File.WriteAllBytesAsync(path, video.Content.ToArray(), cancellationToken);
            return await RunFfmpegAsync(path, width, height, settings, cancellationToken);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static async Task<IReadOnlyList<byte[]>> RunFfmpegAsync(
        string path,
        int width,
        int height,
        YuNetLivenessOptions settings,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(settings.FfmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[]
                 {
                     "-nostdin", "-v", "error",
                     "-t", settings.MaxSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                     "-i", path,
                     "-an",
                     "-vf", FormattableString.Invariant(
                         $"fps={settings.FramesPerSecond},scale={width}:{height}:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2"),
                     "-f", "rawvideo", "-pix_fmt", "bgr24", "pipe:1",
                 })
        {
            start.ArgumentList.Add(argument);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(settings.Timeout);

        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new FfmpegException($"ffmpeg couldn't be started ({settings.FfmpegPath}): {ex.Message}");
        }

        var errors = process.StandardError.ReadToEndAsync(timeout.Token);
        var frameBytes = width * height * 3;
        var frames = new List<byte[]>();
        try
        {
            var output = process.StandardOutput.BaseStream;
            while (true)
            {
                var frame = new byte[frameBytes];
                var read = await output.ReadAtLeastAsync(frame, frameBytes, throwOnEndOfStream: false, timeout.Token);
                if (read < frameBytes)
                {
                    break;
                }

                frames.Add(frame);
            }

            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("ffmpeg took too long reading the video.");
        }

        if (process.ExitCode != 0)
        {
            var message = (await errors).Trim();
            throw new FfmpegException($"ffmpeg exited with code {process.ExitCode}: {message[..Math.Min(message.Length, 300)]}");
        }

        return frames;
    }

    public void Dispose()
    {
        if (_detector.IsValueCreated)
        {
            _detector.Value.Dispose();
        }
    }
}

/// <summary>ffmpeg couldn't read the video.</summary>
/// <param name="message">What went wrong.</param>
public sealed class FfmpegException(string message) : Exception(message);

/// <summary>YuNet in ONNX Runtime. Thread-safe: one session is shared by all requests.</summary>
public sealed class YuNetFaceDetector : IDisposable
{
    private readonly InferenceSession _session;
    private readonly string _inputName;

    /// <summary>Loads the model.</summary>
    /// <param name="settings">Where the model is, and the frame size to use if the model accepts any size.</param>
    public YuNetFaceDetector(YuNetLivenessOptions settings)
    {
        if (string.IsNullOrWhiteSpace(settings.ModelPath) || !File.Exists(settings.ModelPath))
        {
            throw new InvalidOperationException(
                $"The YuNet model wasn't found at '{settings.ModelPath}'. Set {YuNetLivenessOptions.SectionName}:ModelPath.");
        }

        _session = new InferenceSession(settings.ModelPath);
        _inputName = _session.InputMetadata.Keys.First();
        var dimensions = _session.InputMetadata[_inputName].Dimensions;

        // [1, 3, height, width]; -1 where the model accepts any size.
        Height = dimensions.Length == 4 && dimensions[2] > 0 ? dimensions[2] : RoundTo32(settings.InputHeight);
        Width = dimensions.Length == 4 && dimensions[3] > 0 ? dimensions[3] : RoundTo32(settings.InputWidth);
    }

    /// <summary>Frame width the model takes.</summary>
    public int Width { get; }

    /// <summary>Frame height the model takes.</summary>
    public int Height { get; }

    /// <summary>Faces in one raw BGR frame of <see cref="Width"/> × <see cref="Height"/>, best first.</summary>
    /// <param name="bgr">The frame, 3 bytes per pixel.</param>
    /// <param name="scoreThreshold">Lowest score kept.</param>
    public IReadOnlyList<DetectedFace> Detect(byte[] bgr, float scoreThreshold)
    {
        var plane = Width * Height;
        var tensor = new DenseTensor<float>(new[] { 1, 3, Height, Width });
        var buffer = tensor.Buffer.Span;
        for (var i = 0; i < plane; i++)
        {
            // OpenCV's blobFromImage: BGR order, 0–255, no normalisation, channels first.
            buffer[i] = bgr[3 * i];
            buffer[plane + i] = bgr[(3 * i) + 1];
            buffer[(2 * plane) + i] = bgr[(3 * i) + 2];
        }

        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(_inputName, tensor) };
        using var results = _session.Run(inputs);
        var outputs = results.ToDictionary(r => r.Name, r => r.AsEnumerable<float>().ToArray(), StringComparer.Ordinal);
        return YuNetDecoder.Decode(Width, Height, outputs, scoreThreshold);
    }

    public void Dispose() => _session.Dispose();

    private static int RoundTo32(int value) => Math.Max(32, (value + 31) / 32 * 32);
}

/// <summary>Registers the YuNet liveness checker and checks its settings at startup when its model is set.</summary>
public static class YuNetLivenessServiceCollectionExtensions
{
    /// <summary>Adds <see cref="YuNetLivenessChecker"/> as an <see cref="ILivenessChecker"/> named "YuNet".</summary>
    /// <param name="services">The services.</param>
    /// <param name="configuration">The app configuration.</param>
    public static IServiceCollection AddYuNetLiveness(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<YuNetLivenessOptions>()
            .Bind(configuration.GetSection(YuNetLivenessOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<YuNetLivenessOptions>, YuNetLivenessOptionsValidator>();
        services.AddSingleton<YuNetLivenessChecker>();
        services.AddSingleton<ILivenessChecker>(sp => sp.GetRequiredService<YuNetLivenessChecker>());
        return services;
    }
}

/// <summary>When the model path is set, the file must exist; the app won't start otherwise.</summary>
internal sealed class YuNetLivenessOptionsValidator : IValidateOptions<YuNetLivenessOptions>
{
    public ValidateOptionsResult Validate(string? name, YuNetLivenessOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ModelPath) || options.ModelPath.TrimStart().StartsWith('<'))
        {
            // Not set (a template placeholder counts as not set): the stand-in is used locally.
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();
        if (!File.Exists(options.ModelPath))
        {
            failures.Add($"{YuNetLivenessOptions.SectionName}:ModelPath must point to face_detection_yunet_2023mar.onnx (no file at '{options.ModelPath}').");
        }

        if (string.IsNullOrWhiteSpace(options.FfmpegPath))
        {
            failures.Add($"{YuNetLivenessOptions.SectionName}:FfmpegPath can't be empty.");
        }

        if (options.FramesPerSecond < 2 || options.MaxSeconds < 2 || options.Timeout <= TimeSpan.Zero)
        {
            failures.Add($"{YuNetLivenessOptions.SectionName}: FramesPerSecond and MaxSeconds must be at least 2, and Timeout positive.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
