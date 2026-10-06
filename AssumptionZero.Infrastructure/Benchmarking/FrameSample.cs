using System.Net.WebSockets;

namespace AssumptionZero.Infrastructure.Benchmarking;

public struct FrameSample
{
    public int Index { get; set; }
    public WebSocketMessageType MessageType { get; set; }
    public int Bytes { get; set; }
    public int TemplateId { get; set; }
    public long ReceiveTicks { get; set; }
    public long ProcessingEndTicks { get; set; }
    public long LastDecisionTicks { get; set; }
    public int Decisions { get; set; }
    public long DecodeTicks { get; set; }
    public long BookTicks { get; set; }
    public long SignalTicks { get; set; }
    public long QuoteTicks { get; set; }
    public bool GcPauseOverlap { get; set; }
}