namespace AssumptionZero.Infrastructure.External;

public enum FeedMode
{
    /// <summary>Record live market frames without calculating decisions.</summary>
    Capture,
    /// <summary>Calculate decisions from live market frames.</summary>
    Live,
    /// <summary>Replay with an already synchronized book supplied by the caller.</summary>
    ReplayWithSnapshot,
    /// <summary>Replay with a starting book estimated from the first market frames.</summary>
    ReplayWithEstimatedBook
}
