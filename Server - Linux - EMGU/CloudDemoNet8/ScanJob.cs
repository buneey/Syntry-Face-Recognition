using SuperSocket.WebSocket.Server;

namespace CloudDemoNet8
{
    public sealed class ScanJob
    {
        public required string DeviceSn { get; init; }
        public required string ImageBase64 { get; init; }
        public required WebSocketSession Session { get; init; }
        public required DateTime Timestamp { get; init; }

        // Optional metadata
        public string? Note { get; init; }
    }

}