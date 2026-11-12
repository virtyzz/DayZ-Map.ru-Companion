using System.Diagnostics;
using System.Collections.Concurrent;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Foundation.Metadata;
using Windows.Security.Authorization.AppCapabilityAccess;
using WinRT;

namespace CrosshairMarker;

internal sealed record CapturedWindowImage(Bitmap Image, Rectangle ClientBounds, DateTimeOffset MeasuredAt) : IDisposable
{
    public void Dispose() => Image.Dispose();
}

// Windows owns the capture session. No desktop screenshots, hooks, or game-process access.
// Frames stay on the GPU between OCR samples; CPU readback is limited to the requested crop.
internal static class WindowGraphicsCapture
{
    private static readonly Lazy<Task<bool>> borderlessAccess = new(RequestBorderlessAccessAsync);
    public static Task<bool> GetBorderlessAccessAsync() => borderlessAccess.Value;

    private static async Task<bool> RequestBorderlessAccessAsync()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348)) return false;
        try
        {
            if (!ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired")) return false;
            return await GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless) == AppCapabilityAccessStatus.Allowed;
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException) { return false; }
    }

    public static Task<CapturedWindowImage> CaptureAsync(GameWindowState window, Rectangle? clientRegion, CancellationToken token)
        => Task.Run(() => CaptureCore(window, clientRegion, token), token);

    private static CapturedWindowImage CaptureCore(GameWindowState window, Rectangle? clientRegion, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var initialized = RoInitialize(1); // RO_INIT_MULTITHREADED; a pool thread may already be initialized.
        if (initialized < 0 && initialized != unchecked((int)0x80010106)) Marshal.ThrowExceptionForHR(initialized);
        try
        {
            using var capture = new NativeSession(window, false);
            return capture.Capture(window, clientRegion, token);
        }
        finally { if (initialized >= 0) RoUninitialize(); }
    }

    internal sealed class PersistentCapture : IDisposable
    {
        private sealed record Request(GameWindowState? Window, Rectangle? Region, CancellationToken Token, bool KeepAlive, bool Borderless,
            TaskCompletionSource<CapturedWindowImage>? Completion);
        private readonly BlockingCollection<Request> requests = new();
        private readonly CancellationTokenSource lifetime = new();
        private readonly Thread worker;
        private int disposed, sessionStarts;
        private volatile Exception? failure;
        internal int SessionStarts => Volatile.Read(ref sessionStarts);

        public PersistentCapture()
        {
            worker = new Thread(Run) { IsBackground = true, Name = "DayZ window capture" };
            worker.SetApartmentState(ApartmentState.MTA);
            worker.Start();
        }

        public Task<CapturedWindowImage> CaptureAsync(GameWindowState window, Rectangle? region, CancellationToken token, bool keepAlive, bool borderless)
        {
            token.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            if (failure is not null) return Task.FromException<CapturedWindowImage>(failure);
            var completion = new TaskCompletionSource<CapturedWindowImage>(TaskCreationOptions.RunContinuationsAsynchronously);
            try { requests.Add(new(window, region, token, keepAlive, borderless, completion)); }
            catch (InvalidOperationException) { completion.TrySetException(failure ?? new ObjectDisposedException(nameof(PersistentCapture))); }
            return completion.Task;
        }

        public void Stop()
        {
            try { if (Volatile.Read(ref disposed) == 0) requests.TryAdd(new(null, null, default, false, false, null)); }
            catch (InvalidOperationException) { }
        }

        private void Run()
        {
            NativeSession? capture = null;
            var initialized = RoInitialize(1);
            try
            {
                Marshal.ThrowExceptionForHR(initialized);
                while (!lifetime.IsCancellationRequested)
                {
                    if (!requests.TryTake(out var request, 100))
                    {
                        try
                        {
                            if (capture is not null && GameWindowActivity.GetVisibleWindowState(capture.Window) is null)
                            { capture.Dispose(); capture = null; }
                            capture?.DiscardFrames();
                        }
                        catch { capture?.Dispose(); capture = null; }
                        continue;
                    }
                    if (request.Window is null) { capture?.Dispose(); capture = null; continue; }
                    using var cancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, request.Token);
                    try
                    {
                        cancel.Token.ThrowIfCancellationRequested();
                        if (GameWindowActivity.GetVisibleWindowState(request.Window.Handle) != request.Window) throw Unavailable();
                        if (capture?.Window != request.Window.Handle)
                        {
                            capture?.Dispose(); capture = null;
                            capture = new NativeSession(request.Window, request.Borderless);
                            Interlocked.Increment(ref sessionStarts);
                        }
                        var frame = capture.Capture(request.Window, request.Region, cancel.Token);
                        if (!request.Completion!.TrySetResult(frame)) frame.Dispose();
                    }
                    catch (OperationCanceledException) { request.Completion!.TrySetCanceled(); }
                    catch (DayZCompanionException ex) { request.Completion!.TrySetException(ex); }
                    catch (Exception ex) { capture?.Dispose(); capture = null; request.Completion!.TrySetException(ex); }
                    finally { if (!request.KeepAlive) { capture?.Dispose(); capture = null; } }
                }
            }
            catch (Exception ex)
            {
                failure = ex;
                requests.CompleteAdding();
                while (requests.TryTake(out var request)) request.Completion?.TrySetException(ex);
            }
            finally
            {
                requests.CompleteAdding();
                capture?.Dispose();
                while (requests.TryTake(out var request)) request.Completion?.TrySetCanceled();
                if (initialized >= 0) RoUninitialize();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            lifetime.Cancel();
            worker.Join(TimeSpan.FromSeconds(3));
        }
    }

    private sealed class NativeSession : IDisposable
    {
        private IntPtr device, context;
        private IDirect3DDevice? graphicsDevice;
        private GraphicsCaptureItem? item;
        private Direct3D11CaptureFramePool? pool;
        private GraphicsCaptureSession? session;
        private Windows.Graphics.SizeInt32 size;
        public IntPtr Window { get; }

        public NativeSession(GameWindowState window, bool borderless)
        {
            Window = window.Handle;
            try
            {
            if (!GraphicsCaptureSession.IsSupported()) throw new DayZCompanionException("Не удалось получить изображение игры.");
            if (GameWindowActivity.GetVisibleWindowState(window.Handle) != window) throw Unavailable();
            var result = D3D11CreateDevice(IntPtr.Zero, 1, IntPtr.Zero, 0x20, IntPtr.Zero, 0, 7, out device, out _, out context);
            if (result < 0)
            {
                Release(ref context); Release(ref device);
                result = D3D11CreateDevice(IntPtr.Zero, 5, IntPtr.Zero, 0x20, IntPtr.Zero, 0, 7, out device, out _, out context);
            }
            Marshal.ThrowExceptionForHR(result);
            graphicsDevice = CreateGraphicsDevice(device);
            item = CreateItem(window.Handle);
            size = item.Size;
            if (size.Width <= 0 || size.Height <= 0) throw Unavailable();
            pool = Direct3D11CaptureFramePool.CreateFreeThreaded(graphicsDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
            session = pool.CreateCaptureSession(item);
            session.IsCursorCaptureEnabled = false;
            if (borderless && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348)
                && ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired")) session.IsBorderRequired = false;
            session.StartCapture();
            }
            catch { Dispose(); throw; }
        }

        public void DiscardFrames()
        {
            for (var i = 0; i < 8; i++) { using var frame = pool!.TryGetNextFrame(); if (frame is null) break; }
        }

        public CapturedWindowImage Capture(GameWindowState window, Rectangle? clientRegion, CancellationToken token)
        {
            var next = item!.Size;
            if (size.Width != next.Width || size.Height != next.Height)
            {
                size = next;
                pool!.Recreate(graphicsDevice!, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
            }
            DiscardFrames();
            var frameBounds = GetFrameBounds(window.Handle);
            var started = Stopwatch.GetTimestamp();
            while (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(2))
            {
                token.ThrowIfCancellationRequested();
                if (GameWindowActivity.GetVisibleWindowState(window.Handle) != window) throw Unavailable();
                using var frame = pool!.TryGetNextFrame();
                if (frame is null) { token.WaitHandle.WaitOne(25); continue; }
                if (frame.ContentSize.Width != size.Width || frame.ContentSize.Height != size.Height
                    || GetFrameBounds(window.Handle) != frameBounds) throw Unavailable();
                var now = TimeSpan.FromSeconds((double)Stopwatch.GetTimestamp() / Stopwatch.Frequency);
                var age = now - frame.SystemRelativeTime;
                if (!IsFreshFrame(frame.SystemRelativeTime, TimeSpan.FromSeconds((double)started / Stopwatch.Frequency), now)) continue;
                var region = clientRegion ?? new Rectangle(Point.Empty, window.ClientBounds.Size);
                var crop = GetSurfaceRegion(region, window.ClientBounds, frameBounds, new Size(size.Width, size.Height));
                var bitmap = ReadPixels(device, context, frame.Surface, crop);
                if (GameWindowActivity.GetVisibleWindowState(window.Handle) != window)
                {
                    bitmap.Dispose();
                    throw Unavailable();
                }
                return new CapturedWindowImage(bitmap, window.ClientBounds, DateTimeOffset.UtcNow - age);
            }
            throw new DayZCompanionException("Нет свежего изображения игры. Повторите попытку после возвращения в DayZ.");
        }

        public void Dispose()
        {
            try { session?.Dispose(); }
            finally
            {
                try { pool?.Dispose(); }
                finally { graphicsDevice?.Dispose(); Release(ref context); Release(ref device); }
            }
            session = null; pool = null; graphicsDevice = null;
        }
    }

    internal static bool IsFreshFrame(TimeSpan frameTime, TimeSpan requestedAt, TimeSpan now)
        => frameTime >= requestedAt && frameTime <= now && now - frameTime <= TimeSpan.FromSeconds(2);

    internal static Rectangle GetSurfaceRegion(Rectangle clientRegion, Rectangle clientBounds, Rectangle frameBounds, Size surfaceSize)
    {
        var client = new Rectangle(Point.Empty, clientBounds.Size);
        if (clientRegion.Width <= 0 || clientRegion.Height <= 0 || !client.Contains(clientRegion)
            || frameBounds.Width <= 0 || frameBounds.Height <= 0 || surfaceSize.Width <= 0 || surfaceSize.Height <= 0) throw Unavailable();
        var xScale = (double)surfaceSize.Width / frameBounds.Width;
        var yScale = (double)surfaceSize.Height / frameBounds.Height;
        var x = clientBounds.Left - frameBounds.Left + clientRegion.Left;
        var y = clientBounds.Top - frameBounds.Top + clientRegion.Top;
        var crop = Rectangle.FromLTRB((int)Math.Round(x * xScale), (int)Math.Round(y * yScale),
            (int)Math.Round((x + clientRegion.Width) * xScale), (int)Math.Round((y + clientRegion.Height) * yScale));
        if (crop.Width <= 0 || crop.Height <= 0 || !new Rectangle(Point.Empty, surfaceSize).Contains(crop)) throw Unavailable();
        return crop;
    }

    private static DayZCompanionException Unavailable() => new("Изображение игры недоступно. Повторите попытку после возвращения в DayZ.");

    private static Rectangle GetFrameBounds(IntPtr window)
    {
        Marshal.ThrowExceptionForHR(DwmGetWindowAttribute(window, 9, out var bounds, Marshal.SizeOf<NativeRect>()));
        return Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
    }

    private static GraphicsCaptureItem CreateItem(IntPtr window)
    {
        IntPtr text = IntPtr.Zero, factory = IntPtr.Zero, item = IntPtr.Zero;
        try
        {
            const string name = "Windows.Graphics.Capture.GraphicsCaptureItem";
            Marshal.ThrowExceptionForHR(WindowsCreateString(name, name.Length, out text));
            var interopId = new Guid("3628e81b-3cac-4c60-b7f4-23ce0e0c3356");
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(text, ref interopId, out factory));
            var itemId = new Guid("79c3f95b-31f7-4ec2-a464-632ef5d30760");
            Marshal.ThrowExceptionForHR(Method<CreateForWindow>(factory, 3)(factory, window, ref itemId, out item));
            return MarshalInspectable<GraphicsCaptureItem>.FromAbi(item);
        }
        finally
        {
            Release(ref item); Release(ref factory);
            if (text != IntPtr.Zero) WindowsDeleteString(text);
        }
    }

    private static IDirect3DDevice CreateGraphicsDevice(IntPtr device)
    {
        IntPtr dxgi = IntPtr.Zero, inspectable = IntPtr.Zero;
        try
        {
            var id = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c"); // IDXGIDevice
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(device, ref id, out dxgi));
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out inspectable));
            return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        }
        finally { Release(ref inspectable); Release(ref dxgi); }
    }

    private static Bitmap ReadPixels(IntPtr device, IntPtr context, IDirect3DSurface surface, Rectangle crop)
    {
        IntPtr access = IntPtr.Zero, texture = IntPtr.Zero, staging = IntPtr.Zero;
        var mapped = false;
        try
        {
            var accessId = new Guid("a9b3d012-3df2-4ee3-b8d1-8695f457d3c1");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(((IWinRTObject)surface).NativeObject.ThisPtr, ref accessId, out access));
            var textureId = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c"); // ID3D11Texture2D
            Marshal.ThrowExceptionForHR(Method<GetInterface>(access, 3)(access, ref textureId, out texture));
            Method<GetTextureDescription>(texture, 10)(texture, out var description);
            if (description.Format != 87 || description.SampleCount != 1 || crop.Right > description.Width || crop.Bottom > description.Height) throw Unavailable();
            description.Width = (uint)crop.Width; description.Height = (uint)crop.Height;
            description.MipLevels = description.ArraySize = 1;
            description.Usage = 3; description.BindFlags = description.MiscFlags = 0; description.CpuAccessFlags = 0x20000;
            Marshal.ThrowExceptionForHR(Method<CreateTexture>(device, 5)(device, ref description, IntPtr.Zero, out staging));
            var box = new TextureBox { Left = (uint)crop.Left, Top = (uint)crop.Top, Right = (uint)crop.Right, Bottom = (uint)crop.Bottom, Back = 1 };
            Method<CopyRegion>(context, 46)(context, staging, 0, 0, 0, 0, texture, 0, ref box);
            Marshal.ThrowExceptionForHR(Method<MapTexture>(context, 14)(context, staging, 0, 1, 0, out var memory));
            mapped = true;
            var bitmap = new Bitmap(crop.Width, crop.Height, PixelFormat.Format32bppArgb);
            try
            {
                var data = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                try
                {
                    var row = new byte[checked(crop.Width * 4)];
                    for (var y = 0; y < crop.Height; y++)
                    {
                        Marshal.Copy(IntPtr.Add(memory.Data, checked(y * (int)memory.RowPitch)), row, 0, row.Length);
                        Marshal.Copy(row, 0, IntPtr.Add(data.Scan0, y * data.Stride), row.Length);
                    }
                }
                finally { bitmap.UnlockBits(data); }
                return bitmap;
            }
            catch { bitmap.Dispose(); throw; }
        }
        finally
        {
            if (mapped) Method<UnmapTexture>(context, 15)(context, staging, 0);
            Release(ref staging); Release(ref texture); Release(ref access);
        }
    }

    // ABI slots follow the Windows SDK d3d11.h and windows.graphics.capture.interop.h.
    private static T Method<T>(IntPtr instance, int slot) where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));
    private static void Release(ref IntPtr value) { if (value != IntPtr.Zero) { Marshal.Release(value); value = IntPtr.Zero; } }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct TextureDescription
    {
        public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CpuAccessFlags, MiscFlags;
    }
    [StructLayout(LayoutKind.Sequential)] private struct TextureBox { public uint Left, Top, Front, Right, Bottom, Back; }
    [StructLayout(LayoutKind.Sequential)] private struct MappedTexture { public IntPtr Data; public uint RowPitch, DepthPitch; }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateForWindow(IntPtr self, IntPtr window, ref Guid iid, out IntPtr item);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetInterface(IntPtr self, ref Guid iid, out IntPtr result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void GetTextureDescription(IntPtr self, out TextureDescription description);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateTexture(IntPtr self, ref TextureDescription description, IntPtr initialData, out IntPtr texture);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void CopyRegion(IntPtr self, IntPtr destination, uint destinationSubresource, uint x, uint y, uint z, IntPtr source, uint sourceSubresource, ref TextureBox box);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int MapTexture(IntPtr self, IntPtr texture, uint subresource, uint type, uint flags, out MappedTexture memory);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void UnmapTexture(IntPtr self, IntPtr texture, uint subresource);
    [DllImport("d3d11.dll")] private static extern int D3D11CreateDevice(IntPtr adapter, uint driverType, IntPtr software, uint flags, IntPtr levels, uint levelCount, uint sdkVersion, out IntPtr device, out uint level, out IntPtr context);
    [DllImport("d3d11.dll")] private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);
    [DllImport("combase.dll")] private static extern int RoInitialize(uint type);
    [DllImport("combase.dll")] private static extern void RoUninitialize();
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string text, int length, out IntPtr hstring);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(IntPtr hstring);
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(IntPtr hstring, ref Guid iid, out IntPtr factory);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, uint attribute, out NativeRect value, int size);
}
