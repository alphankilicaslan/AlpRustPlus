using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RustPlusApi;
using RustPlusApi.Camera;
using RustPlusApi.Data;
using RustPlusApi.Data.Cameras;
using RustPlusApi.Data.Events;

namespace RustPlusDesk.Services.Camera
{
    /// <summary>
    /// A single live camera. Owns a dedicated <see cref="RustPlus"/> connection (the server tracks
    /// one camera subscription per connection, so multiple simultaneous cameras each need their own),
    /// drives a <see cref="CameraController"/> for subscribe / keep-alive / input, and turns the ray
    /// stream into PNG frames with a <see cref="CameraRenderer"/>.
    /// </summary>
    public sealed class CameraSession : IAsyncDisposable
    {
        private readonly RustPlus _api;              // dedicated connection, owned by this session
        private readonly CameraController _controller;
        private readonly CameraRenderer _renderer;
        private readonly object _renderLock = new();
        private DateTime _lastRenderUtc = DateTime.MinValue;
        private volatile bool _disposed;

        /// <summary>Camera identifier this session is subscribed to.</summary>
        public string CameraId => _controller.CameraId;

        /// <summary>Subscription metadata (width/height/control flags); null if the subscribe failed.</summary>
        public CameraInfo? Info => _controller.Info;

        public CameraControlFlags ControlFlags => _controller.Info?.ControlFlags ?? CameraControlFlags.None;
        public int Width => _controller.Info?.Width ?? 0;
        public int Height => _controller.Info?.Height ?? 0;

        public bool IsDrone => _controller.IsDrone;
        public bool IsPtzCamera => _controller.IsPtzCamera;
        public bool IsAutoTurret => _controller.IsAutoTurret;
        public bool IsStaticCamera => _controller.IsStaticCamera;

        /// <summary>Enables high-contrast FLIR ironbow thermal visualization mode.</summary>
        public bool IsThermalMode { get; set; } = false;

        /// <summary>Enables low-res superpixel FPV splatting mode (instant full screen fill for fast drone flight / responsive camera).</summary>
        public bool IsTurboFpvMode { get; set; } = false;

        private int[]? _pixelBuffer;

        private static readonly int[] ThermalLut = GenerateThermalLut();
        private static readonly Func<CameraRenderer, (int, int, int)?[]> GetRendererOutput = CreateOutputGetter();

        private static Func<CameraRenderer, (int, int, int)?[]> CreateOutputGetter()
        {
            try
            {
                var fi = typeof(CameraRenderer).GetField("_output", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (fi == null) return _ => Array.Empty<(int, int, int)?>();
                var param = System.Linq.Expressions.Expression.Parameter(typeof(CameraRenderer), "r");
                var fieldAccess = System.Linq.Expressions.Expression.Field(param, fi);
                return System.Linq.Expressions.Expression.Lambda<Func<CameraRenderer, (int, int, int)?[]>>(fieldAccess, param).Compile();
            }
            catch
            {
                return _ => Array.Empty<(int, int, int)?>();
            }
        }

        private static int[] GenerateThermalLut()
        {
            var lut = new int[256];
            for (int i = 0; i < 256; i++)
            {
                float t = i / 255.0f;
                byte r, g, b;

                // Classic FLIR / Ironbow gradient:
                // 0.00 - 0.20: Black to Dark Violet/Blue
                // 0.20 - 0.45: Violet to Crimson Red
                // 0.45 - 0.70: Crimson to Bright Orange
                // 0.70 - 0.90: Orange to Golden Yellow
                // 0.90 - 1.00: Yellow to White Hot
                if (t < 0.20f)
                {
                    float k = t / 0.20f;
                    r = (byte)(10 + 35 * k);
                    g = (byte)(5 + 10 * k);
                    b = (byte)(35 + 85 * k);
                }
                else if (t < 0.45f)
                {
                    float k = (t - 0.20f) / 0.25f;
                    r = (byte)(45 + 150 * k);
                    g = (byte)(15 + 15 * k);
                    b = (byte)(120 - 70 * k);
                }
                else if (t < 0.70f)
                {
                    float k = (t - 0.45f) / 0.25f;
                    r = (byte)(195 + 50 * k);
                    g = (byte)(30 + 100 * k);
                    b = (byte)(50 - 45 * k);
                }
                else if (t < 0.90f)
                {
                    float k = (t - 0.70f) / 0.20f;
                    r = (byte)(245 + 10 * k);
                    g = (byte)(130 + 105 * k);
                    b = (byte)(5 + 35 * k);
                }
                else
                {
                    float k = (t - 0.90f) / 0.10f;
                    r = 255;
                    g = (byte)(235 + 20 * k);
                    b = (byte)(40 + 215 * k);
                }

                // In Bgr32: (255 << 24) | (r << 16) | (g << 8) | b
                lut[i] = unchecked((int)((0xFFu << 24) | ((uint)r << 16) | ((uint)g << 8) | (uint)b));
            }
            return lut;
        }

        /// <summary>Maximum rendered frames per second raised via <see cref="FrameRendered"/>.
        /// Ray samples still accumulate every frame; only the render/raise is throttled.</summary>
        public int TargetFps { get; set; } = 60;
 
         /// <summary>Raised (on a background thread) with freshly rendered PNG bytes.</summary>
         public event Action<byte[]>? FrameRendered;
 
         /// <summary>Raised (on a background thread) with a frozen, cross-thread safe BitmapSource ready for GPU presentation.</summary>
         public event Action<System.Windows.Media.Imaging.BitmapSource>? FrameBitmapRendered;
 
         /// <summary>Raised (on a background thread) with the entities in the latest frame and the vertical FOV in degrees.</summary>
         public event Action<IReadOnlyList<CameraEntity>, double>? EntitiesUpdated;
 
         /// <summary>Raised when the keep-alive renewal fails (camera destroyed, disconnected, …). Frames go quiet after this.</summary>
         public event Action<string>? KeepAliveFailed;
 
         private CameraSession(RustPlus api, CameraController controller)
         {
             _api = api;
             _controller = controller;
 
             var info = controller.Info;
             var w = info?.Width ?? 0;
             var h = info?.Height ?? 0;
             _renderer = new CameraRenderer(w > 0 ? w : 1, h > 0 ? h : 1);
 
             _controller.OnFrameReceived += OnFrameReceived;
             _controller.OnKeepAliveFailed += OnKeepAliveFailed;
         }
 
         /// <summary>
         /// Opens a dedicated connection, subscribes to <paramref name="cameraId"/> and returns a live session.
         /// Throws if the connection or subscription fails (the connection is cleaned up in that case).
         /// </summary>
         public static async Task<CameraSession> StartAsync(
             string host, int port, ulong playerId, int playerToken, bool useProxy,
             string cameraId, CancellationToken ct = default)
         {
             var api = new RustPlus(new RustPlusConnection(host, port, playerId, playerToken, useProxy));
             try
             {
                 await api.ConnectAsync(ct).ConfigureAwait(false);
                 var response = await CameraController.SubscribeAsync(api, cameraId, null, ct).ConfigureAwait(false);
                 if (!response.IsSuccess || response.Data is null)
                     throw new InvalidOperationException(response.Error?.Message ?? $"Failed to subscribe to camera '{cameraId}'.");
                 return new CameraSession(api, response.Data);
             }
             catch
             {
                 try { await api.DisposeAsync().ConfigureAwait(false); } catch { /* best effort */ }
                 throw;
             }
         }
 
        private void OnFrameReceived(object? sender, CameraRaysEventArg frame)
        {
            if (_disposed) return;

            IReadOnlyList<CameraEntity> entities = frame.Entities is null
                ? Array.Empty<CameraEntity>()
                : (frame.Entities as IReadOnlyList<CameraEntity>) ?? new List<CameraEntity>(frame.Entities);
            var vfov = frame.VerticalFov;

            System.Windows.Media.Imaging.BitmapSource? directBitmap = null;
            byte[]? png = null;

            lock (_renderLock)
            {
                if (_disposed) return;
                _renderer.AddRays(frame); // always accumulate so the image keeps sharpening

                var minIntervalMs = TargetFps >= 60 ? 0.0 : (1000.0 / Math.Max(1, TargetFps));
                var now = DateTime.UtcNow;
                if (minIntervalMs > 0.0 && (now - _lastRenderUtc).TotalMilliseconds < minIntervalMs) return;
                _lastRenderUtc = now;

                // FAST DIRECT RENDER (Zero-copy, bypasses PNG encode/decode!)
                int w = Width > 0 ? Width : 160;
                int h = Height > 0 ? Height : 120;
                int totalPixels = w * h;

                if (_pixelBuffer == null || _pixelBuffer.Length != totalPixels)
                {
                    _pixelBuffer = new int[totalPixels];
                }

                var rawOutput = GetRendererOutput(_renderer);
                if (rawOutput != null && rawOutput.Length >= totalPixels)
                {
                    bool isThermal = IsThermalMode;
                    bool isTurbo = IsTurboFpvMode;
                    const int skyColorNormal = unchecked((int)0xFF0E1116);
                    int skyColorThermal = ThermalLut[0];

                    if (isTurbo)
                    {
                        // Turbo FPV Mode (2x2 Superpixel Splatting):
                        // Fills the entire screen 4x faster (in 1-2 packets), eliminating holes and latency during flight!
                        int defaultColor = isThermal ? skyColorThermal : skyColorNormal;
                        Array.Fill(_pixelBuffer, defaultColor);

                        for (int y = 0; y < h; y++)
                        {
                            int rowOffset = y * w;
                            for (int x = 0; x < w; x++)
                            {
                                var pixel = rawOutput[rowOffset + x];
                                if (!pixel.HasValue) continue;

                                var (r, g, b) = pixel.Value;
                                int col;
                                if (isThermal)
                                {
                                    int lum = (299 * r + 587 * g + 114 * b) / 1000;
                                    if (lum > 255) lum = 255; else if (lum < 0) lum = 0;
                                    col = ThermalLut[lum];
                                }
                                else
                                {
                                    col = unchecked((int)((0xFFu << 24) | ((uint)r << 16) | ((uint)g << 8) | (uint)b));
                                }

                                _pixelBuffer[rowOffset + x] = col;
                                if (x + 1 < w) _pixelBuffer[rowOffset + x + 1] = col;
                                if (y + 1 < h)
                                {
                                    int nextRow = (y + 1) * w;
                                    _pixelBuffer[nextRow + x] = col;
                                    if (x + 1 < w) _pixelBuffer[nextRow + x + 1] = col;
                                }
                            }
                        }
                    }
                    else
                    {
                        // HD Normal Mode: standard 1:1 pixel mapping
                        for (int i = 0; i < totalPixels; i++)
                        {
                            var pixel = rawOutput[i];
                            if (pixel.HasValue)
                            {
                                var (r, g, b) = pixel.Value;
                                if (isThermal)
                                {
                                    int lum = (299 * r + 587 * g + 114 * b) / 1000;
                                    if (lum > 255) lum = 255; else if (lum < 0) lum = 0;
                                    _pixelBuffer[i] = ThermalLut[lum];
                                }
                                else
                                {
                                    _pixelBuffer[i] = unchecked((int)((0xFFu << 24) | ((uint)r << 16) | ((uint)g << 8) | (uint)b));
                                }
                            }
                            else
                            {
                                _pixelBuffer[i] = isThermal ? skyColorThermal : skyColorNormal;
                            }
                        }
                    }

                    // Highlight detected living entities (Players / Enemies) with high visibility in BOTH modes!
                    // In Thermal: Blazing white-hot heat signature.
                    // In Normal/Turbo: High-contrast bright red silhouette so enemies are NEVER blurred or lost!
                    if (entities.Count > 0 && vfov > 0)
                    {
                        double vf = vfov * Math.PI / 180.0;
                        double aspect = w / (double)h;
                        double hf = 2.0 * Math.Atan(Math.Tan(vf / 2.0) * aspect);
                        const int whiteHot = unchecked((int)0xFFFFFFFF);
                        const int flameHot = unchecked((int)0xFFFFE140);
                        const int enemyRed = unchecked((int)0xFFEF4444);
                        const int enemyOrange = unchecked((int)0xFFF97316);

                        foreach (var ent in entities)
                        {
                            bool isPlayer = ent.Type == CameraEntityType.Player || !string.IsNullOrWhiteSpace(ent.Name);
                            if (!isPlayer) continue;

                            double ez = ent.Position.Z;
                            if (ez <= 0.1) continue;

                            double xndc = (ent.Position.X / ez) / Math.Tan(hf / 2.0);
                            double yndc = (ent.Position.Y / ez) / Math.Tan(vf / 2.0);
                            int cx = (int)((xndc * 0.5 + 0.5) * w);
                            int cy = (int)((-yndc * 0.5 + 0.5) * h);

                            if (cx < -5 || cx >= w + 5 || cy < -5 || cy >= h + 5) continue;

                            int radius = Math.Clamp((int)(18.0 / Math.Max(1.0, ez)), 2, 8);
                            for (int dy = -radius; dy <= radius; dy++)
                            {
                                int py = cy + dy;
                                if (py < 0 || py >= h) continue;
                                for (int dx = -radius; dx <= radius; dx++)
                                {
                                    int px = cx + dx;
                                    if (px < 0 || px >= w) continue;
                                    int distSq = dx * dx + dy * dy;
                                    if (distSq <= radius * radius)
                                    {
                                        int pidx = py * w + px;
                                        if (isThermal)
                                        {
                                            _pixelBuffer[pidx] = distSq <= (radius * radius / 3) ? whiteHot : flameHot;
                                        }
                                        else
                                        {
                                            _pixelBuffer[pidx] = distSq <= (radius * radius / 3) ? enemyRed : enemyOrange;
                                        }
                                    }
                                }
                            }
                        }
                    }

                    try
                    {
                        var bs = System.Windows.Media.Imaging.BitmapSource.Create(
                            w, h,
                            96, 96,
                            System.Windows.Media.PixelFormats.Bgr32,
                            null,
                            _pixelBuffer,
                            w * 4
                        );
                        bs.Freeze();
                        directBitmap = bs;
                    }
                    catch { }
                }

                // If someone subscribed to legacy PNG byte[] events, render PNG on demand
                if (FrameRendered != null)
                {
                    try { png = _renderer.Render(); } catch { png = null; }
                }
            }

            if (directBitmap != null)
            {
                FrameBitmapRendered?.Invoke(directBitmap);
            }
            else if (png != null && FrameBitmapRendered != null)
            {
                try
                {
                    var bi = new System.Windows.Media.Imaging.BitmapImage();
                    using (var ms = new System.IO.MemoryStream(png))
                    {
                        bi.BeginInit();
                        bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                        bi.StreamSource = ms;
                        bi.EndInit();
                    }
                    bi.Freeze();
                    FrameBitmapRendered.Invoke(bi);
                }
                catch { }
            }

            if (png != null)
            {
                FrameRendered?.Invoke(png);
            }

            EntitiesUpdated?.Invoke(entities, vfov);
        }

        private void OnKeepAliveFailed(object? sender, ErrorMessage err)
            => KeepAliveFailed?.Invoke(err?.Message ?? "keep-alive failed");

        public Task SendInputAsync(CameraButtons buttons, float mouseDeltaX, float mouseDeltaY)
            => _disposed ? Task.CompletedTask : _controller.SendInputAsync(buttons, mouseDeltaX, mouseDeltaY, CancellationToken.None);

        /// <summary>Mouse-look pan/tilt.</summary>
        public Task LookAsync(float deltaX, float deltaY)
            => _disposed ? Task.CompletedTask : _controller.LookAsync(deltaX, deltaY, CancellationToken.None);

        /// <summary>Fire (auto-turret). Self-refuses on non-turret cameras.</summary>
        public Task ShootAsync()
            => _disposed ? Task.CompletedTask : _controller.ShootAsync(CancellationToken.None);

        /// <summary>Reload (auto-turret). Self-refuses on non-turret cameras.</summary>
        public Task ReloadAsync()
            => _disposed ? Task.CompletedTask : _controller.ReloadAsync(CancellationToken.None);

        /// <summary>Zoom (PTZ camera). Self-refuses on non-PTZ cameras.</summary>
        public Task ZoomAsync()
            => _disposed ? Task.CompletedTask : _controller.ZoomAsync(CancellationToken.None);

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;

            _controller.OnFrameReceived -= OnFrameReceived;
            _controller.OnKeepAliveFailed -= OnKeepAliveFailed;

            try { await _controller.DisposeAsync().ConfigureAwait(false); } catch { /* best effort */ }
            try { await _api.DisposeAsync().ConfigureAwait(false); } catch { /* best effort */ }
        }
    }
}
