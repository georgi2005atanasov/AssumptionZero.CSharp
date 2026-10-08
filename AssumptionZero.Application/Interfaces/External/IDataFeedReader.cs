namespace AssumptionZero.Application.Interfaces.External;

/// <summary>Receives market frames or processes a supplied frame using the same decoding pipeline.</summary>
public interface IDataFeedReader
{
    Task Start(
        CancellationToken cancellationToken = default,
        string connectionString = "",
        Func<CancellationToken, Task>? afterConnect = null);
    void ProcessBinaryFrame(ReadOnlySpan<byte> frame);
}
