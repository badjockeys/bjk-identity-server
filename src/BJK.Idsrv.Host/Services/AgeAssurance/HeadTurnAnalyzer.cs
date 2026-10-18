using BJK.Modules.User.Infrastructure.AgeAssurance;

namespace BJK.Idsrv.Host.Services.AgeAssurance;

/// <summary>
/// Decides liveness from the faces found in each sampled frame: one person, visible in most frames, big enough, whose
/// head turns clearly one way and then clearly the other. A still photo, or a face that never turns, fails.
/// </summary>
public static class HeadTurnAnalyzer
{
    /// <summary>Analyses the faces found in each frame, in time order.</summary>
    /// <param name="frames">Faces found in each sampled frame (already filtered by score).</param>
    /// <param name="frameWidth">Frame width in pixels, for the minimum face size.</param>
    /// <param name="options">Thresholds.</param>
    public static LivenessResult Analyse(
        IReadOnlyList<IReadOnlyList<DetectedFace>> frames,
        int frameWidth,
        YuNetLivenessOptions options)
    {
        if (frames.Count == 0)
        {
            return LivenessResult.Failed(LivenessFailure.Unreadable);
        }

        // A second face in more than one frame means someone else is in shot (one frame could be a false detection).
        if (frames.Count(f => f.Count > 1) > 1)
        {
            return LivenessResult.Failed(LivenessFailure.MultipleFaces);
        }

        var minWidth = options.MinFaceWidthRatio * frameWidth;
        var yaws = frames
            .Select(f => f.Count > 0 && f[0].Width >= minWidth ? f[0].Yaw : (float?)null)
            .Where(y => y is not null)
            .Select(y => y!.Value)
            .ToList();

        var needed = Math.Max(options.MinFaceFrames, (int)Math.Ceiling(options.MinFaceFrameRatio * frames.Count));
        if (yaws.Count < needed)
        {
            return LivenessResult.Failed(LivenessFailure.NoFace);
        }

        return TurnedBothWays(yaws, options.YawThreshold)
            ? LivenessResult.Live
            : LivenessResult.Failed(LivenessFailure.NoHeadTurn);
    }

    /// <summary>True if the yaw passes the threshold one way and, later, the other way.</summary>
    /// <param name="yaws">Yaw per frame, in time order.</param>
    /// <param name="threshold">How far counts as a clear turn.</param>
    public static bool TurnedBothWays(IReadOnlyList<float> yaws, float threshold)
    {
        var firstTurn = 0;
        foreach (var yaw in yaws)
        {
            var direction = yaw >= threshold ? 1 : yaw <= -threshold ? -1 : 0;
            if (direction == 0)
            {
                continue;
            }

            if (firstTurn == 0)
            {
                firstTurn = direction;
            }
            else if (direction == -firstTurn)
            {
                return true;
            }
        }

        return false;
    }
}
