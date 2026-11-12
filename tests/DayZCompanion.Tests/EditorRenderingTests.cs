using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Forms;
using CrosshairMarker;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

internal static class EditorRenderingTests
{
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
                    web.CoreWebView2.NavigateToString(html);
                    if (!await loaded.Task.WaitAsync(TimeSpan.FromSeconds(20))) throw new Exception("Editor navigation failed");
                    var jsonOptions = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
                    var config = new AppConfig(); config.Normalize();
                    var settings = new DayZCompanionSettings(); settings.Normalize();
                    var destination = new TreasureDestinationSession(new string('a', 32), DateTimeOffset.UtcNow.AddMinutes(5),
                        [new("cherno", "Chernarus", 15360, 15360)], [new("cherno", "profile", "Fixture profile", true, "", "default", "#3498db")]);
                    foreach (var connected in new[] { false, true })
                    {
                        var payload = new
                        {
                            config, microsoftStore = false, openTab = "player-position",
                            dayZ = new { settings, status = new DayZCompanionStatus("Fixture", null, null, false, null, [], "", false) },
                            performance = new PerformanceSnapshot(), performanceDefaults = new PerformanceSettings(),
                            ocr = new OcrStatus(false, "Fixture OCR status"),
                            battlePass = new { settings = new BattlePassSettings(), snapshot = new BattlePassSnapshot(), manualTasks = Array.Empty<object>() },
                            treasures = new { captures = Array.Empty<object>(), destination = connected ? destination : null },
                            playerPosition = new { settings = settings.PlayerPositionTracking, destination = connected ? destination : null },
                            monitors = new[] { new { DeviceName = "fixture", DisplayName = "Fixture monitor", Primary = true } },
                            preview = ""
                        };
                        var script = "(() => { try { window.DayZMapCompanion.receiveState(" + JsonSerializer.Serialize(payload, jsonOptions) + ");" + """
                            const tabs = navigation.flatMap(item => item.tab ? [item.tab[0]] : item.tabs.map(tab => tab[0]));
                            const result = [];
                            for (const tab of tabs) {
                                window.DayZMapCompanion.openTab(tab);
                                const editor = document.getElementById('editor');
                                const heading = editor.querySelector('h2')?.textContent;
                                if (!heading || editor.dataset.page !== tab) throw new Error('Empty editor tab: ' + tab);
                                result.push({ tab, heading });
                            }
                            window.DayZMapCompanion.openTab('player-position');
                            if (document.querySelector('[data-position-map], [data-position-auto-map], [data-position-label], [data-position-label-content]'))
                                throw new Error('Removed position settings are still present');
                            return { ok: true, tabs: result };
                            } catch (error) { return { ok: false, error: error.stack || String(error) }; } })()
                            """;
                        var result = await web.CoreWebView2.ExecuteScriptAsync(script);
                        using var report = JsonDocument.Parse(result);
                        if (!report.RootElement.GetProperty("ok").GetBoolean()) throw new Exception(result);
                        var tabs = report.RootElement.GetProperty("tabs").GetArrayLength();
                        if (tabs != 14) throw new Exception($"Expected 14 tabs, rendered {tabs}");
                        Console.WriteLine($"  Rendered {tabs} editor tabs, map connected: {connected}");
                    }
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
