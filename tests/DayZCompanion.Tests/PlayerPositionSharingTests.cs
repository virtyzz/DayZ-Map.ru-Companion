using CrosshairMarker;

internal static class PlayerPositionSharingTests
{
    public static void GpsJumpsRequireConfirmation()
    {
        var previous = new PlayerPosition(1000, 100, 1000, DateTimeOffset.UnixEpoch);
        var normal = previous with { X = 1180, Z = 1070, MeasuredAt = previous.MeasuredAt.AddSeconds(5) };
        Check(!PlayerPositionTrackingController.RequiresConfirmation(previous, normal), "Normal vehicle movement required confirmation");
        Check(!PlayerPositionTrackingController.RequiresConfirmation(previous, normal with { Y = 2500 }), "Altitude was used for the map jump check");

        var jump = normal with { X = 8000, Z = 9000 };
        Check(PlayerPositionTrackingController.RequiresConfirmation(previous, jump), "A valid-looking OCR jump was accepted on the first frame");
        Check(PlayerPositionTrackingController.RequiresConfirmation(previous, jump with { MeasuredAt = previous.MeasuredAt.AddMinutes(5) }),
            "A long sampling interval disabled the jump check");

        var corrected = normal with { MeasuredAt = normal.MeasuredAt.AddSeconds(1) };
        var confirmed = corrected with { X = jump.X + 30, Z = jump.Z };
        var different = corrected with { X = 2000, Z = 8200 };
        Check(PlayerPositionTrackingController.ResolveConfirmation(previous, jump, corrected) == corrected, "A corrected OCR reading was discarded");
        Check(PlayerPositionTrackingController.ResolveConfirmation(previous, jump, confirmed) == confirmed, "A confirmed relocation was discarded");
        Check(PlayerPositionTrackingController.ResolveConfirmation(previous, jump, different) is null, "Conflicting readings confirmed a relocation");
        Check(PlayerPositionTrackingController.ResolveConfirmation(previous, jump, jump) is null, "The same frame confirmed a relocation");
        Check(PlayerPositionTrackingController.ResolveConfirmation(previous, jump, confirmed with { MeasuredAt = jump.MeasuredAt.AddSeconds(6) }) is null,
            "A late second reading confirmed a relocation");
        Check(PlayerPositionTrackingController.ResolveConfirmation(previous, jump, null) is null, "A missing second reading confirmed a relocation");
    }

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

    public static void PrivateSamplesAreNotReusedWhenSwitchingToPublic()
    {
        var destination = new TreasureDestinationSession(new string('n', 32), DateTimeOffset.MaxValue,
            [new("cherno", "Chernarus", 15360, 15360)], []);
        var bridge = new PlayerPositionMapBridge(() => destination);
        var settings = new PlayerPositionTrackingSettings { MapId = "cherno" };
        var measured = new PlayerPosition(4150, 12, 6110, DateTimeOffset.UtcNow);
        bridge.SetTrackingEnabled(true);
        bridge.Queue(settings, measured);
        Check(bridge.GetSession() == destination, "Offline tracking needs a map catalog");
        Check(bridge.GetLatestPosition()?.Visibility == "private", "The measurement lost its private visibility");
        Check(bridge.GetPending(destination.SessionId) is null, "The browser bridge can read a private measurement");

        settings.Visibility = "public";
        bridge.UpdateSettings(settings);
        Check(bridge.GetLatestPosition() is null, "A private sample was reused after switching to public");
        bridge.Queue(settings, measured with { MeasuredAt = measured.MeasuredAt.AddSeconds(1) });
        Check(bridge.GetLatestPosition()?.Visibility == "public", "A fresh public sample is missing");
        Check(bridge.GetPending(destination.SessionId) is null, "Native publication is exposed on the browser bridge");

        settings.Visibility = "private";
        bridge.UpdateSettings(settings);
        Check(bridge.GetLatestPosition()?.Visibility == "private", "Switching to private left a public sample available");
        bridge.SetTrackingEnabled(false);
        Check(bridge.GetLatestPosition() is null, "Pausing left a measurement available");
    }

    public static void SharedProfileSelectionsFollowTheMap()
    {
        var settings = System.Text.Json.JsonSerializer.Deserialize<PlayerPositionTrackingSettings>(
            """{"MapId":"cherno","ProfileIds":["north"]}""")!;
        settings.Normalize();
        settings.SelectMap("deerisle");
        Check(settings.ProfileIds.Count == 0, "Profiles from a different map were selected");
        settings.SaveMapProfiles("deerisle", ["island"]);
        settings.SelectMap("cherno");
        Check(settings.ProfileIds.SequenceEqual(["north"]), "Legacy profile choice was lost on return");
        settings.SaveMapProfiles("cherno", []);
        settings.SelectMap("deerisle");
        var restored = System.Text.Json.JsonSerializer.Deserialize<PlayerPositionTrackingSettings>(
            System.Text.Json.JsonSerializer.Serialize(settings))!;
        restored.Normalize();
        Check(restored.ProfileIds.SequenceEqual(["island"]), "The selected island profile did not survive a restart");
        restored.SelectMap("cherno");
        Check(restored.ProfileIds.Count == 0, "An explicitly cleared map selection was lost");
        var copy = restored.Clone();
        copy.SaveMapProfiles("deerisle", ["new-island"]);
        restored.SelectMap("deerisle");
        Check(restored.ProfileIds.SequenceEqual(["island"]), "Editing a settings snapshot changed the live map choices");
    }
}
