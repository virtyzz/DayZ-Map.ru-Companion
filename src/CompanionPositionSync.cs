using System.Net.Http;
using System.Text.Json;

namespace CrosshairMarker;

internal sealed class CompanionPositionSync : IDisposable
{
    private const string Endpoint = "companion/player-position";
    private readonly CompanionAccount account;
    private readonly PlayerPositionMapBridge bridge;
    private readonly Func<PerformanceSnapshot> getServer;
    private readonly CancellationTokenSource shutdown = new();
    private readonly SemaphoreSlim publication = new(1, 1);
    private readonly Task worker;
    private readonly Task refresh;
    private CompanionSession? owner;
    private PlayerPositionUpdate? lastSent;
    private PlayerPositionUpdate? rejected;
    private Guid? connection;
    private string? stream;
    private DateTimeOffset nextHeartbeat;
    private DateTimeOffset accountChangedAt;

    public CompanionPositionSync(CompanionAccount account, PlayerPositionMapBridge bridge, Func<PerformanceSnapshot> getServer)
    {
        this.account = account;
        this.bridge = bridge;
        this.getServer = getServer;
        var token = shutdown.Token;
        worker = Task.Run(() => RunAsync(token));
        refresh = Task.Run(() => RefreshProfilesAsync(token));
    }

    private async Task RefreshProfilesAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));
        try
        {
            do { await account.RefreshAsync(token); }
            while (await timer.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task RunAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                await publication.WaitAsync(token);
                try { await TickAsync(token); }
                catch (DayZCompanionException ex)
                {
                    if (bridge.GetLatestPosition() is { } update) bridge.ReportCloudPosition(update, false, ex.Message);
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    AppRuntimeLog.Error("Could not synchronize player position", ex);
                }
                finally { publication.Release(); }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task TickAsync(CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;
        var currentOwner = account.Session;
        if (!ReferenceEquals(currentOwner, owner))
        {
            ResetPublication();
            owner = currentOwner;
            accountChangedAt = now;
        }

        var update = bridge.GetLatestPosition();
        if (owner is null)
        {
            if (update is not null)
                bridge.ReportCloudPosition(update, false, update.Visibility == "private"
                    ? "Позиция доступна в Companion. Войдите, чтобы видеть её на сайте."
                    : "Войдите в аккаунт Companion, чтобы передавать позицию.");
            return;
        }

        var server = getServer();
        var valid = update is not null && update.MeasuredAt >= accountChangedAt && IsCurrentMeasurement(update, now, server);
        if (stream is not null && (!valid || connection != server.ConnectionId || (lastSent is not null && !SameAudience(lastSent, update!))))
            await WithdrawAsync(token);
        if (!valid || update is null || ReferenceEquals(update, rejected)) return;

        if (update.Visibility == "profiles" && update.ProfileIds is not { Length: > 0 })
        {
            bridge.ReportCloudPosition(update, false, "Выберите хотя бы один профиль для передачи позиции.");
            return;
        }
        if (!ReferenceEquals(update, lastSent))
        {
            stream ??= Guid.NewGuid().ToString("N");
            connection = server.ConnectionId;
            // Remember the attempted audience even if the HTTP response is lost.
            lastSent = update;
            try
            {
                var result = await account.SendAsync<JsonElement>(HttpMethod.Put, Endpoint, new
                {
                    update.MapId, stream_id = stream, update.Visibility, update.ProfileIds, update.X, update.Y, update.Z,
                    update.MeasuredAt, update.IntervalSeconds, update.Template.Name, update.Template.Color,
                    shape = update.Template.Type, update.ShowLabel, update.MarkerSize, update.LabelContent, update.Server,
                    update.CenterOnPlayer, update.KeepZoom, update.CenteringZoom
                }, owner, token);
                if (!result.GetProperty("accepted").GetBoolean())
                {
                    rejected = update;
                    bridge.ReportCloudPosition(update, false, "На карте уже есть более свежая позиция этого аккаунта.");
                    return;
                }
                nextHeartbeat = now.AddSeconds(5);
                ReportPositionSent(update);
            }
            catch (DayZCompanionException)
            {
                rejected = update;
                await WithdrawAsync(token);
                throw;
            }
        }
        else if (now >= nextHeartbeat)
        {
            var result = await account.SendAsync<JsonElement>(HttpMethod.Post,
                PositionPath("/heartbeat"), null, owner, token);
            if (!result.GetProperty("active").GetBoolean())
            {
                ResetPublication();
                return;
            }
            nextHeartbeat = now.AddSeconds(5);
            ReportPositionSent(update);
        }
    }

    private void ReportPositionSent(PlayerPositionUpdate update) =>
        bridge.ReportCloudPosition(update, true, update.Visibility == "private"
            ? "Личная позиция обновлена на сайте." : "Позиция передана на карту.");

    private bool IsCurrentMeasurement(PlayerPositionUpdate update, DateTimeOffset now, PerformanceSnapshot server)
    {
        if (now - update.MeasuredAt >= TimeSpan.FromSeconds(Math.Max(20, update.IntervalSeconds * 3 + 10)) ||
            !server.Running || update.ConnectionId != server.ConnectionId || update.Server?.Address != server.Server ||
            bridge.GetSession() is not { } destination)
            return false;
        try { return PlayerPositionMapResolver.Resolve(destination, server).Id == update.MapId; }
        catch (DayZCompanionException) { return false; }
    }

    private static bool SameAudience(PlayerPositionUpdate first, PlayerPositionUpdate second) =>
        first.MapId == second.MapId && first.Visibility == second.Visibility &&
        (first.ProfileIds ?? []).SequenceEqual(second.ProfileIds ?? []);

    private string PositionPath(string suffix = "") => $"{Endpoint}{suffix}?stream_id={stream}";

    private async Task WithdrawAsync(CancellationToken token)
    {
        if (owner is not null && stream is not null && account.IsCurrent(owner))
            await account.SendAsync<JsonElement>(HttpMethod.Delete, PositionPath(), null, owner, token);
        ResetPublication();
    }

    private void ResetPublication()
    {
        lastSent = null;
        connection = null;
        stream = null;
        nextHeartbeat = default;
    }

    public async Task SignOutAsync(CancellationToken token)
    {
        await publication.WaitAsync(token);
        try
        {
            if (account.Session is not { } signedIn) return;
            try { await WithdrawAsync(token); }
            catch (DayZCompanionException) { }
            await account.SignOutAsync(signedIn, token);
            ResetPublication();
        }
        finally { publication.Release(); }
    }

    public void Dispose()
    {
        shutdown.Cancel();
        _ = Task.WhenAll(worker, refresh).ContinueWith(_ =>
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { WithdrawAsync(timeout.Token).GetAwaiter().GetResult(); }
            catch (Exception ex) when (ex is DayZCompanionException or OperationCanceledException) { }
            finally
            {
                account.Dispose();
                publication.Dispose();
                shutdown.Dispose();
            }
        }, TaskScheduler.Default);
    }
}
