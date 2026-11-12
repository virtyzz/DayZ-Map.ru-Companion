using CrosshairMarker;

internal static class PlayerPositionSharingTests
{
    public static void AcknowledgementReleasesBridgeLock()
    {
        var bridge = new PlayerPositionMapBridge();
        var sessionId = new string('b', 32);
        bridge.SetSession(new(sessionId, DateTimeOffset.UtcNow.AddMinutes(5), [new("cherno", "Chernarus")], []));
        bridge.SetTrackingEnabled(true);
        var settings = new PlayerPositionTrackingSettings { MapId = "cherno" };
        bridge.Queue(settings, new(100, 200, 300, DateTimeOffset.UtcNow));
        var deliveries = 0;
        bridge.Delivered += delivered =>
        {
            deliveries++;
            Check(delivered.TrackingId == settings.TrackingId, "Wrong delivery reported");
            // A settings worker must be able to enter the bridge while the
            // delivery callback is running, even from another thread.
            var update = Task.Run(() =>
            {
                Check(bridge.GetPending(sessionId) is null, "Acknowledged position was not cleared before notification");
                settings.MarkerName = "Changed during acknowledgement";
                bridge.UpdateSettings(settings);
            });
            Check(update.Wait(TimeSpan.FromSeconds(5)), "Delivery callback held the bridge lock");
        };
        bridge.Acknowledge(sessionId, "wrong-id", true, "ignored");
        bridge.Acknowledge(sessionId, settings.TrackingId, false, "retry");
        Check(deliveries == 0 && bridge.GetPending(sessionId) is not null, "Failed acknowledgement delivered or cleared a position");
        bridge.Acknowledge(sessionId, settings.TrackingId, true, "ok");
        Check(deliveries == 1, "Successful acknowledgement was not delivered exactly once");
        Check(bridge.GetPending(sessionId)?.Template.Name == settings.MarkerName, "Callback's new update was discarded");
    }

    public static void MapResolutionAndPrivacy()
    {
        var session = new TreasureDestinationSession(new string('a', 32), DateTimeOffset.UtcNow.AddMinutes(5),
            [new("cherno", "Черноруссия", 15360, 15360), new("deerisle", "Deer Isle", 16384, 16384)], []);
        var fresh = new PerformanceSnapshot(Running: true, Server: "192.0.2.1:2302", Query: "192.0.2.1:27016", ServerName: "Test server", MapName: "ChernarusPlus", MapMeasuredAt: DateTimeOffset.UtcNow);
        Check(PlayerPositionMapResolver.Resolve(session, fresh).Id == "cherno", "ChernarusPlus alias failed");
        Check(PlayerPositionMapResolver.Resolve(session, fresh with { MapName = "Deer_Isle" }).Id == "deerisle", "Deer Isle alias failed");
        foreach (var bad in new[] { fresh with { Running = false }, fresh with { MapMeasuredAt = DateTimeOffset.UtcNow.AddMinutes(-1) },
                     fresh with { MapName = "Unknown" }, fresh with { Server = "" } })
        {
            try { PlayerPositionMapResolver.Resolve(session, bad); }
            catch (DayZCompanionException) { continue; }
            throw new Exception("Unverified server map accepted");
        }
        var settings = new PlayerPositionTrackingSettings { MapId = "cherno", ProfileIds = ["one", "one", "two"], Visibility = "profiles" };
        settings.Normalize();
        var copy = settings.Clone();
        copy.ProfileIds.Clear();
        Check(settings.ProfileIds.Count == 2, "Profile list was not deduplicated/deep cloned");
        var bridge = new PlayerPositionMapBridge();
        bridge.SetSession(session);
        bridge.SetTrackingEnabled(true);
        bridge.Queue(settings, new(100, 200, 300, DateTimeOffset.UtcNow), fresh);
        var update = bridge.GetPending(session.SessionId)!;
        Check(update.Visibility == "profiles" && update.ProfileIds!.SequenceEqual(new[] { "one", "two" }), "Audience lost in bridge");
        Check(update.Server == new PlayerPositionServerInfo("Test server", "192.0.2.1:2302", "ChernarusPlus"), "Server metadata lost or query port sent instead of game port");
        bridge.Acknowledge(session.SessionId, settings.TrackingId, true, "ok");
        bridge.UpdateSettings(settings);
        Check(bridge.GetPending(session.SessionId) is null, "Unchanged audience must not replay acknowledged position");
        settings.Visibility = "private";
        bridge.UpdateSettings(settings);
        Check(bridge.GetPending(session.SessionId)?.Visibility == "private", "Private switch did not propagate immediately");
        Check(bridge.GetPending(session.SessionId)?.Server == update.Server, "Display settings lost server metadata");
        bridge.Queue(settings, new(100, 200, 300, DateTimeOffset.UtcNow), fresh with { MapMeasuredAt = DateTimeOffset.UtcNow.AddMinutes(-1) });
        Check(bridge.GetPending(session.SessionId)?.Server == new PlayerPositionServerInfo("", "192.0.2.1:2302", ""), "Stale A2S metadata must not be presented as current");
        bridge.Queue(settings, new(100, 200, 300, DateTimeOffset.UtcNow), fresh with { Running = false });
        Check(bridge.GetPending(session.SessionId)?.Server is null, "Disconnected game exposed old server metadata");
        bridge.SetTrackingEnabled(false);
        Check(bridge.GetPending(session.SessionId) is null, "Disabled tracking left pending position");
        Check(new PlayerPositionTrackingSettings().Visibility == "private", "New settings expose position by default");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
