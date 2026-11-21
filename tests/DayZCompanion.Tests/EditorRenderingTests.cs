using System.Reflection;
using System.Drawing;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Forms;
using CrosshairMarker;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

internal static class EditorRenderingTests
{
    public static void MaximizedWindowFitsWorkingArea()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            const string profileVariable = "WEBVIEW2_USER_DATA_FOLDER";
            var previousProfile = Environment.GetEnvironmentVariable(profileVariable);
            var folder = Path.Combine(Path.GetTempPath(), "companion-maximize-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable(profileVariable, Path.Combine(folder, "browser"));
            try
            {
                var store = new TreasureCaptureStore(folder);
                using var capture = new PlayerPositionCaptureService();
                using var form = new EditorForm(new AppConfig(), new UpdateService(), new DayZCompanionSettings(),
                    new DayZCompanionStatus("Fixture", null, null, false, null, [], "", false, "", []),
                    new BattlePassSettings(), new BattlePassSnapshot(), store, new TreasureCaptureService(store),
                    new TreasureMapBridge(), new PlayerPositionMapBridge(), capture, "general");
                form.Shown += async (_, _) =>
                {
                    try
                    {
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                        var web = form.Controls.OfType<WebView2>().Single();
                        while (web.CoreWebView2 is not { } core
                               || await core.ExecuteScriptAsync("document.querySelector('.integrated-frame #editor')?.dataset.page") != "\"general\"")
                            await Task.Delay(100, timeout.Token);

                        foreach (var screen in Screen.AllScreens)
                        {
                            var work = screen.WorkingArea;
                            form.Bounds = new Rectangle(work.Location, new Size(Math.Min(1280, work.Width), Math.Min(800, work.Height)));
                            var restoredBounds = form.Bounds;
                            form.WindowState = FormWindowState.Maximized;
                            var client = form.RectangleToScreen(form.ClientRectangle);
                            if (form.Bounds != work || client != work)
                                throw new InvalidOperationException($"{screen.DeviceName}: window {form.Bounds}, client {client}, expected working area {work}.");
                            form.WindowState = FormWindowState.Normal;
                            if (form.Bounds != restoredBounds)
                                throw new InvalidOperationException($"Restored window bounds changed: {restoredBounds} → {form.Bounds}.");
                            Console.WriteLine($"  {screen.DeviceName}: maximized within {work}, restored to {restoredBounds}");
                        }
                    }
                    catch (Exception ex) { failure = ex; }
                    finally { form.Close(); }
                };
                Application.Run(form);
            }
            finally { Environment.SetEnvironmentVariable(profileVariable, previousProfile); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(30))) throw new Exception("Editor maximization fixture timed out");
        if (failure is not null) throw new Exception("Editor maximization failed: " + failure.Message, failure);
    }

    public static void AllTabsRender()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            using var form = new Form { Text = "Companion editor rendering fixture", Width = 1200, Height = 900 };
            using var web = new WebView2 { Dock = DockStyle.Fill };
            form.Controls.Add(web);
            form.Shown += async (_, _) =>
            {
                try
                {
                    var data = Path.Combine(Path.GetTempPath(), "companion-editor-test-" + Guid.NewGuid().ToString("N"));
                    var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: data);
                    await web.EnsureCoreWebView2Async(environment);
                    var loaded = new TaskCompletionSource<bool>();
                    web.CoreWebView2.NavigationCompleted += (_, e) => loaded.TrySetResult(e.IsSuccess);
                    var html = (string)typeof(EditorForm).GetMethod("GetEditorHtml", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;
                    web.CoreWebView2.NavigateToString(html.Replace("<body>", "<body class=\"integrated-frame\">"));
                    if (!await loaded.Task.WaitAsync(TimeSpan.FromSeconds(20))) throw new Exception("Editor navigation failed");
                    var jsonOptions = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
                    var config = new AppConfig(); config.Normalize();
                    config.CurrentProfile.CrosshairShape = CrosshairShape.Arcs;
                    config.CurrentProfile.DotShape = DotShape.Diamond;
                    var settings = new DayZCompanionSettings(); settings.Normalize();
                    var destination = new TreasureDestinationSession(new string('a', 32), DateTimeOffset.UtcNow.AddMinutes(5),
                        [new("cherno", "Chernarus", 15360, 15360)], [new("cherno", "profile", "Fixture profile", true, "", "default", "#3498db")]);
                    const string testPreview = "data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7";
                    var coordinateSaved = new TaskCompletionSource<(string? Id, int X, int Z)>();
                    var copyRequests = new List<(string? Action, string? Name, int X, int Z)>();
                    var copyRequestsReceived = new TaskCompletionSource<bool>();
                    web.CoreWebView2.WebMessageReceived += (_, args) =>
                    {
                        using var message = JsonDocument.Parse(args.WebMessageAsJson);
                        var root = message.RootElement;
                        if (root.GetProperty("type").GetString() == "treasure" && root.GetProperty("action").GetString() == "edit")
                            coordinateSaved.TrySetResult((root.GetProperty("id").GetString(), root.GetProperty("x").GetInt32(), root.GetProperty("z").GetInt32()));
                        if (root.GetProperty("type").GetString() == "copySessionMarker")
                        {
                            var marker = root.GetProperty("marker");
                            copyRequests.Add((root.GetProperty("action").GetString(), marker.GetProperty("Name").GetString(),
                                marker.GetProperty("X").GetInt32(), marker.GetProperty("Z").GetInt32()));
                            if (copyRequests.Count == 2) copyRequestsReceived.TrySetResult(true);
                        }
                    };
                    foreach (var connected in new[] { false, true })
                    {
                        settings.PlayerPositionTracking.MapId = connected ? "cherno" : "";
                        var payload = new
                        {
                            config, microsoftStore = false, openTab = connected ? "player-position" : null,
                            dayZ = new { settings, status = new DayZCompanionStatus("Fixture", null, null, false, null, [], "", false, "", []) },
                            performance = new PerformanceSnapshot(), performanceDefaults = new PerformanceSettings(),
                            ocr = new OcrStatus(false, "Fixture OCR status"),
                            battlePass = new { settings = new BattlePassSettings(), snapshot = new BattlePassSnapshot(), manualTasks = Array.Empty<object>() },
                            treasures = new { captures = connected ? new[]
                            {
                                new { Id = "first", X = 1100, Z = 2200, Status = "Recognized", CapturedAt = DateTimeOffset.UtcNow, RawText = "Первый снимок", preview = testPreview },
                                new { Id = "second", X = 3150, Z = 4700, Status = "Recognized", CapturedAt = DateTimeOffset.UtcNow, RawText = "Второй снимок", preview = testPreview }
                            } : [], destination = connected ? destination : null },
                            playerPosition = new { settings = settings.PlayerPositionTracking, destination = connected ? destination : null },
                            monitors = new[] { new { DeviceName = "fixture", DisplayName = "Fixture monitor", Primary = true } },
                            preview = ""
                        };
                        var script = "(() => { try { window.DayZMapCompanion.receiveState(" + JsonSerializer.Serialize(payload, jsonOptions) + ");" + """
                            if (document.getElementById("editor").dataset.page !== (state.openTab || "overview"))
                                throw new Error("Incorrect initial editor tab");
                            expandedSettings.clear();
                            const tabs = navigation.flatMap(item => item.tab ? [item.tab[0]] : item.tabs.map(tab => tab[0]));
                            const result = [];
                            for (const tab of tabs) {
                                window.DayZMapCompanion.openTab(tab);
                                const editor = document.getElementById('editor');
                                const heading = editor.querySelector('h1, h2')?.textContent;
                                if (!heading || editor.dataset.page !== tab) throw new Error('Empty editor tab: ' + tab);
                                result.push({ tab, heading });
                            }
                            window.DayZMapCompanion.openTab('account');
                            if (!document.querySelector('form[data-account-email] input[type="email"]')
                                || !document.querySelector('[data-account-action="discord"]'))
                                throw new Error('Both sign-in methods must be available on the account tab');
                            window.DayZMapCompanion.openTab('overview');
                            const map = document.querySelector('[data-session-map]');
                            if (map?.dataset.map !== 'cherno' || map.getBoundingClientRect().width < 250)
                                throw new Error('The overview did not reserve space for the embedded map');
                            const visibility = document.querySelector('.session-visibility');
                            if (!visibility.lastElementChild.matches('svg.icon') || visibility.lastElementChild.textContent.trim())
                                throw new Error('The position visibility chevron must be an icon');
                            window.DayZMapCompanion.receivePerformance({ Server: '127.0.0.1:2302', ServerName: 'Fixture server', MapName: 'Deer Isle', Fps: 72, Ping: 58 });
                            if (map.dataset.map !== 'deerisle' || document.querySelector('[data-session-server]').textContent !== 'Fixture server'
                                || document.querySelector('[data-performance-value="Fps"]').textContent !== '72'
                                || document.querySelector('[data-performance-value="Ping"]').parentElement.textContent.trim() !== 'Пинг 58 мс')
                                throw new Error('Live session data did not update the overview');
                            const fileStatus = state.dayZ.status;
                            state.dayZ.status = { ...fileStatus, FileWritable: true, PrivateMarkersPath: "PrivateMarkers.json" };
                            for (const tab of ["overview", "dayz"]) {
                                window.DayZMapCompanion.openTab(tab);
                                const exportButton = document.querySelector('[data-command="exportSessionMarkers"]');
                                const importButton = document.querySelector('[data-command="importSessionMarkers"]');
                                window.DayZMapCompanion.receivePerformance({ Server: "127.0.0.1:2302" }, "Выйдите с сервера DayZ.");
                                if (exportButton.disabled || !importButton.disabled)
                                    throw new Error('Incorrect marker actions on a connected server in ' + tab);
                                window.DayZMapCompanion.receivePerformance({ Server: "" }, null);
                                if (exportButton.disabled || importButton.disabled)
                                    throw new Error('Offline marker actions should be enabled in ' + tab);
                            }
                            state.dayZ.status = fileStatus;
                            window.DayZMapCompanion.openTab('performance');
                            if (document.querySelector('[data-settings-disclosure="performance-overlay"]').open
                                || document.querySelector('[data-settings-disclosure="performance-connection"]').open)
                                throw new Error('Advanced performance settings should start collapsed');
                            const connectionSettings = document.querySelector('[data-settings-disclosure="performance-connection"]');
                            connectionSettings.open = true;
                            connectionSettings.dispatchEvent(new Event('toggle'));
                            const portMode = document.querySelector('[data-performance-port-mode]');
                            if (portMode.value !== 'auto' || !document.querySelector('[data-performance-port-form]').hidden)
                                throw new Error('Automatic query port should hide the manual port field');
                            portMode.value = 'manual';
                            portMode.dispatchEvent(new Event('change'));
                            document.querySelector('[data-performance-port]').value = '27016';
                            document.querySelector('[data-performance-port-form]').requestSubmit();
                            if (state.dayZ.settings.Performance.QueryPort !== 27016 || document.querySelector('[data-performance-port-form]').hidden)
                                throw new Error('Manual query port was not saved');
                            if (!document.querySelector('[data-settings-disclosure="performance-connection"]').open)
                                throw new Error('Saving the query port collapsed its settings');
                            const manualPortMode = document.querySelector('[data-performance-port-mode]');
                            manualPortMode.value = 'auto';
                            manualPortMode.dispatchEvent(new Event('change'));
                            if (state.dayZ.settings.Performance.QueryPort !== 0 || !document.querySelector('[data-performance-port-form]').hidden)
                                throw new Error('Automatic query port was not restored');
                            window.DayZMapCompanion.openTab('crosshair');
                            if (document.querySelector('[data-select="CrosshairShape"]').value !== '6'
                                || document.querySelector('[data-select="DotShape"]').value !== '2')
                                throw new Error('Saved shape names do not match their settings controls');
                            for (const [tab, selector, readValue] of [
                                ['crosshair', '[data-general-check="ShowOnlyInGame"]', () => state.config.ShowOnlyInGame],
                                ['tasks', '[data-bp-check="ShowOnlyInGame"]', () => state.battlePass.settings.ShowOnlyInGame],
                                ['performance-settings', '[data-performance-check="ShowOnlyInGame"]', () => state.dayZ.settings.Performance.ShowOnlyInGame]
                            ]) {
                                window.DayZMapCompanion.openTab(tab);
                                const checkbox = document.querySelector(selector);
                                if (checkbox?.closest('label')?.textContent.trim() !== 'Показывать только в DayZ')
                                    throw new Error('Missing DayZ visibility setting: ' + tab);
                                const expected = !checkbox.checked;
                                checkbox.click();
                                if (readValue() !== expected || document.querySelector(selector).checked !== expected)
                                    throw new Error('DayZ visibility setting did not update: ' + tab);
                            }
                            window.DayZMapCompanion.openTab('player-position');
                            if (document.querySelector('[data-settings-disclosure="position-display"]').open
                                || document.querySelector('[data-settings-disclosure="position-diagnostics"]').open
                                || document.querySelector('[data-command="selectPlayerPositionRegion"]').closest('details'))
                                throw new Error('Position settings did not prioritize capture setup');
                            if (document.querySelector('[data-position-map], [data-position-auto-map], [data-position-label], [data-position-label-content]'))
                                throw new Error('Removed position settings are still present');
                            state.openTab = null;
                            const lastPosition = document.querySelector('[data-player-position-last]');
                            const lastTracking = { ...state.playerPosition.settings, LastX: 4150, LastY: 12, LastZ: 6110 };
                            window.DayZMapCompanion.receiveState({ ...state, dayZ: {
                                ...state.dayZ, settings: { ...state.dayZ.settings, PlayerPositionTracking: lastTracking }
                            }, playerPosition: { ...state.playerPosition, settings: lastTracking } });
                            if (document.querySelector('[data-player-position-last]') !== lastPosition || lastPosition.textContent !== 'X 4150 · Y 12 · Z 6110')
                                throw new Error('Live position should update without rebuilding the position form');
                            if (state.playerPosition.destination) {
                                const visibility = document.querySelector('[data-position-visibility]');
                                visibility.value = 'profiles';
                                visibility.dispatchEvent(new Event('change'));
                                const checkbox = document.querySelector('[data-position-profile][value="profile"]');
                                if (!checkbox) throw new Error('The loaded map profile was not displayed');
                                const box = checkbox.getBoundingClientRect();
                                const label = checkbox.nextElementSibling.getBoundingClientRect();
                                if (label.left <= box.right || Math.abs(label.top + label.height / 2 - box.top - box.height / 2) > 1)
                                    throw new Error('The loaded profile name is not aligned with its checkbox');
                            }
                            return { ok: true, tabs: result };
                            } catch (error) { return { ok: false, error: error.stack || String(error) }; } })()
                            """;
                        var result = await web.CoreWebView2.ExecuteScriptAsync(script);
                        using var report = JsonDocument.Parse(result);
                        if (!report.RootElement.GetProperty("ok").GetBoolean()) throw new Exception(result);
                        var tabs = report.RootElement.GetProperty("tabs").GetArrayLength();
                        if (tabs != 15) throw new Exception($"Expected 15 tabs, rendered {tabs}");
                        Console.WriteLine($"  Rendered {tabs} editor tabs, map connected: {connected}");
                    }
                    form.ClientSize = new System.Drawing.Size(1060, 900);
                    await Task.Delay(100);
                    var treasureResult = await web.CoreWebView2.ExecuteScriptAsync("""
                        (() => {
                            window.DayZMapCompanion.openTab("treasures");
                            const x = document.querySelector('[data-treasure-x="first"]');
                            x.value = '1250';
                            x.dispatchEvent(new Event('input'));
                            const selectSecond = () => document.querySelector('[data-treasure-select="second"]');
                            selectSecond().click();
                            document.querySelector('[data-treasure-select="first"]').click();
                            const draftRestored = document.querySelector('[data-treasure-x="first"]').value === "1250";
                            selectSecond().click();
                            const toggle = selectSecond();
                            const detail = document.getElementById("treasure-capture-detail");
                            const preview = detail.querySelector(".treasure-preview");
                            const inline = toggle.nextElementSibling?.contains(detail) && !x.isConnected;
                            toggle.click();
                            const collapsed = detail.hidden && toggle.getAttribute("aria-expanded") === "false";
                            toggle.click();
                            return draftRestored && inline && collapsed && !detail.hidden
                                && document.querySelectorAll('[data-treasure-select][aria-expanded="true"]').length === 1
                                && !!document.querySelector('[data-treasure-x="second"]')
                                && !document.querySelector('[data-treasure-x="first"]')
                                && !!(detail.querySelector(".treasure-coordinates").compareDocumentPosition(preview) & Node.DOCUMENT_POSITION_FOLLOWING);
                        })()
                        """);
                    if (treasureResult != "true" || await coordinateSaved.Task.WaitAsync(TimeSpan.FromSeconds(3)) != ("first", 1250, 2200))
                        throw new Exception("Switching captures lost the edited coordinates or the selected preview.");
                    foreach (var (width, height) in new[] { (1060, 700), (1586, 900), (1920, 1032), (2500, 900), (1060, 900) })
                    {
                        form.ClientSize = new System.Drawing.Size(width, height);
                        await Task.Delay(100);
                        var captureLayout = await web.CoreWebView2.ExecuteScriptAsync("""
                            (() => {
                                const wide = window.innerWidth > 1160;
                                const host = document.querySelector(wide ? ".treasure-capture-side" : ".treasure-inline");
                                const capture = document.querySelector('[data-treasure-select="second"]');
                                return !!host?.querySelector('[data-treasure-x="second"]')
                                    && capture.getAttribute(wide ? "aria-pressed" : "aria-expanded") === "true";
                            })()
                            """);
                        if (captureLayout != "true") throw new Exception($"Capture selection or placement at {width}px");
                        var layoutResult = await web.CoreWebView2.ExecuteScriptAsync("""
                            (() => {
                                window.DayZMapCompanion.openTab("overview");
                                const get = selector => document.querySelector(selector).getBoundingClientRect();
                                const fileStatus = state.dayZ.status;
                                const server = "127.0.0.1:2302";
                                state.dayZ.status = { ...fileStatus, FileWritable: true, PrivateMarkersPath: "PrivateMarkers.json",
                                    MarkerServer: server, SessionMarkers: Array.from({ length: 80 }, (_, i) => ({ Name: "Метка " + i, X: i, Z: i * 2 })) };
                                window.DayZMapCompanion.receivePerformance({ Server: server, MapName: "Chernarus", Fps: 72, Ping: 58 }, "Выйдите с сервера DayZ.");
                                const editor = get("#editor"), bar = get(".session-bar");
                                const map = get(".session-map"), side = get(".session-side"), footer = get(".session-footer");
                                const caption = get(".window-caption"), list = document.querySelector("[data-session-markers]");
                                const lastRow = list.lastElementChild;
                                const visibleRows = Math.floor(list.clientHeight / lastRow.getBoundingClientRect().height);
                                const enoughRows = window.innerWidth < 1900 || window.innerHeight < 1000 || visibleRows >= 8;
                                const buttons = [...document.querySelectorAll(".session-marker-actions button")].map(button => button.getBoundingClientRect());
                                list.scrollTop = list.scrollHeight;
                                const listScrolls = list.scrollTop > 0 && list.clientHeight > 0
                                    && lastRow.getBoundingClientRect().bottom <= list.getBoundingClientRect().bottom + 1;
                                const footerStaysVisible = footer.bottom <= editor.bottom
                                    && footer.top === get(".session-footer").top && map.top === get(".session-map").top;
                                const editorFits = document.getElementById("editor").scrollHeight <= document.getElementById("editor").clientHeight + 1
                                    && editor.top >= caption.bottom && list.getBoundingClientRect().top >= caption.bottom;
                                const ok = editor.right - bar.right < 38 && Math.abs(bar.right - side.right) < 2
                                    && Math.abs(map.top - side.top) < 2 && Math.abs(map.bottom - side.bottom) < 2
                                    && map.width > side.width && map.bottom <= footer.top
                                    && Math.abs(buttons[0].width - buttons[1].width) < 2
                                    && Math.abs(buttons[0].top - buttons[1].top) < 2
                                    && listScrolls && footerStaysVisible && editorFits && enoughRows;
                                window.DayZMapCompanion.receivePerformance({ Server: "" }, null);
                                state.dayZ.status = fileStatus;
                                return { ok, listScrolls, footerStaysVisible, editorFits, visibleRows, width: window.innerWidth, remainingSpace: editor.right - bar.right };
                            })()
                            """);
                        using var report = JsonDocument.Parse(layoutResult);
                        if (!report.RootElement.GetProperty("ok").GetBoolean())
                            throw new Exception($"Overview layout at {width}×{height}px: {layoutResult}");
                        var workspaces = await web.CoreWebView2.ExecuteScriptAsync("""
                            (() => {
                                const rect = selector => document.querySelector(selector).getBoundingClientRect();
                                const split = (main, side) => side.left >= main.right && Math.abs(side.top - main.top) < 2;
                                const stacked = (main, side) => side.top >= main.bottom && Math.abs(side.left - main.left) < 25;
                                const arranged = [];
                                const wide = window.innerWidth > 1160;
                                state.update = { CurrentVersion: "1", LatestVersion: "2", IsUpdateAvailable: true, ReleaseNotes: "## Test release" };
                                for (const tab of ["tasks", "dayz"]) {
                                    window.DayZMapCompanion.openTab(tab);
                                    const main = rect(".settings-workspace > :first-child"), side = rect(".settings-side");
                                    if ((wide ? split : stacked)(main, side)) arranged.push(tab);
                                }
                                window.DayZMapCompanion.openTab("updates");
                                const status = rect(".update-status"), notes = rect(".update-notes-slot");
                                const actions = rect('[data-command="installUpdate"]');
                                if (status.bottom <= notes.top && notes.bottom <= actions.top
                                    && Math.abs(notes.left - actions.left) < 2
                                    && !document.querySelector(".settings-workspace")) arranged.push("updates");
                                window.DayZMapCompanion.openTab("crosshair");
                                const main = rect("#editor"), side = rect(".preview-wrap");
                                if ((wide ? (a, b) => b.left >= a.right : stacked)(main, side)) arranged.push("crosshair");
                                window.DayZMapCompanion.openTab("hotkeys");
                                const compact = document.querySelector(".compact-settings").getBoundingClientRect().width <= 820.1
                                    && getComputedStyle(document.querySelector(".preview-wrap")).display === "none";
                                window.DayZMapCompanion.openTab("treasures");
                                if (!wide || split(rect(".treasure-workspace > :first-child"), rect(".treasure-capture-side"))) arranged.push("treasures");
                                return { ok: arranged.length === 5 && compact, arranged };
                            })()
                            """);
                        using var workspaceReport = JsonDocument.Parse(workspaces);
                        if (!workspaceReport.RootElement.GetProperty("ok").GetBoolean())
                            throw new Exception($"Workspace layout at {width}px: {workspaces}");
                    }
                    var markerMenuResult = await web.CoreWebView2.ExecuteScriptAsync("""
                        (() => {
                            const server = "127.0.0.1:2302";
                            state.dayZ.status = { ...state.dayZ.status, MarkerServer: server,
                                SessionMarkers: [{ Name: "Проверка", X: 1450, Z: 2400 }] };
                            window.DayZMapCompanion.receivePerformance({ Server: server, MapName: "Chernarus" });
                            const menu = document.getElementById("session-marker-popover");
                            const opened = () => menu.matches(":popover-open");
                            for (const [tab, action] of [["overview", "coordinates"], ["dayz", "name"]]) {
                                window.DayZMapCompanion.openTab(tab);
                                const trigger = document.querySelector("[data-session-marker-menu]");
                                trigger.click();
                                if (!opened() || document.activeElement !== menu.firstElementChild)
                                    return { tab, step: "open", opened: opened(), focus: document.activeElement.outerHTML.slice(0, 100) };
                                document.querySelector("#editor h2").click();
                                if (opened()) return { tab, step: "outside" };
                                trigger.click();
                                document.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape" }));
                                if (opened() || document.activeElement !== trigger) return { tab, step: "escape" };
                                trigger.click();
                                trigger.click();
                                if (opened()) return { tab, step: "toggle" };
                                trigger.click();
                                window.dispatchEvent(new Event("blur"));
                                if (opened()) return { tab, step: "blur" };
                                trigger.click();
                                menu.querySelector(`[data-copy-session-marker="${action}"]`).click();
                                if (opened() || trigger.getAttribute("aria-expanded") !== "false") return { tab, step: "copy" };
                            }
                            return true;
                        })()
                        """);
                    if (markerMenuResult != "true") throw new Exception($"Marker menu interaction failed: {markerMenuResult}");
                    await copyRequestsReceived.Task.WaitAsync(TimeSpan.FromSeconds(3));
                    if (!copyRequests.SequenceEqual(new (string?, string?, int, int)[] { ("coordinates", "Проверка", 1450, 2400), ("name", "Проверка", 1450, 2400) }))
                        throw new Exception("The marker menu sent the wrong copy requests.");
                    await web.CoreWebView2.ExecuteScriptAsync("window.DayZMapCompanion.openTab('overview');");
                    var proof = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "editor-rendering-preview.png"));
                    using var stream = File.Create(proof);
                    await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
                }
                catch (Exception ex) { failure = ex; }
                finally { form.Close(); }
            };
            Application.Run(form);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(55))) throw new Exception("Editor rendering fixture timed out");
        if (failure is not null) throw new Exception("Editor rendering failed: " + failure.Message, failure);
    }
}
