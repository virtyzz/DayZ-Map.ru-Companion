namespace CrosshairMarker;

internal enum TreasureRecognitionStatus
{
    Queued,
    Recognizing,
    NeedsReview,
    Recognized,
    Ambiguous
}

internal sealed class TreasureCapture
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.Now;
    public string ImagePath { get; set; } = "";
    public string RawText { get; set; } = "";
    public int? X { get; set; }
    public int? Z { get; set; }
    public TreasureRecognitionStatus Status { get; set; } = TreasureRecognitionStatus.Recognized;
    public bool Confirmed { get; set; }
    public bool ManuallyEdited { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public string DeliveryResult { get; set; } = "";

    public bool HasCoordinates => X.HasValue && Z.HasValue;
    public string Coordinates => HasCoordinates ? $"X={X} Z={Z}" : "Координаты не распознаны";
}

internal sealed record TreasureOcrResult(string RawText, int? X, int? Z, TreasureRecognitionStatus Status, string Message);
