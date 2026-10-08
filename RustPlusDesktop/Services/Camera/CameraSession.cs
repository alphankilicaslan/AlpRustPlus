using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RustPlusApi;
using RustPlusApi.Camera;
using RustPlusApi.Data;
using RustPlusApi.Data.Cameras;
using RustPlusApi.Data.Events;

namespace RustPlusDesk.Services.Camera
{
    /// <summary>
    /// A single live camera session. Owns a dedicated <see cref="RustPlus"/> connection,
    /// drives a <see cref="CameraController"/> for keep-alive / PTZ / drone movement input,
    /// and uses a high-performance, zero-allocation native ray stream decoder with Turbo FPV
    /// progressive splatting and FLIR ironbow thermal imaging.
    /// </summary>
    public sealed class CameraSession : IAsyncDisposable
    {
        private readonly RustPlus _api;
        private readonly CameraController _controller;
        private readonly object _renderLock = new();
        private DateTime _lastRenderUtc = DateTime.MinValue;
        private volatile bool _disposed;

        public string CameraId => _controller.CameraId;
        public CameraInfo? Info => _controller.Info;
        public CameraControlFlags ControlFlags => _controller.Info?.ControlFlags ?? CameraControlFlags.None;
        public int Width => _controller.Info?.Width ?? 0;
        public int Height => _controller.Info?.Height ?? 0;

        public bool IsDrone => _controller.IsDrone;
        public bool IsPtzCamera => _controller.IsPtzCamera;
        public bool IsAutoTurret => _controller.IsAutoTurret;
        public bool IsStaticCamera => _controller.IsStaticCamera;

        private bool _isThermalMode = false;
        /// <summary>Enables high-contrast FLIR ironbow thermal visualization mode.</summary>
        public bool IsThermalMode
        {
            get => _isThermalMode;
            set
            {
                if (_isThermalMode != value)
                {
                    _isThermalMode = value;
                    ForceRender();
                }
            }
        }

        private bool _isTurboFpvMode = false;
        /// <summary>Enables superpixel progressive FPV splatting mode (instant full screen fill for fast drone flight & responsive camera).</summary>
        public bool IsTurboFpvMode
        {
            get => _isTurboFpvMode;
            set
            {
                if (_isTurboFpvMode != value)
                {
                    _isTurboFpvMode = value;
                    ForceRender();
                }
            }
        }

        public int TargetFps { get; set; } = 60;

#pragma warning disable CS0067
        public event Action<byte[]>? FrameRendered;
#pragma warning restore CS0067
        public event Action<BitmapSource>? FrameBitmapRendered;
        public event Action<IReadOnlyList<CameraEntity>, double>? EntitiesUpdated;
        public event Action<string>? KeepAliveFailed;

        // Native Fast Ray Engine State
        private readonly short[] _samplePositionBuffer;
        private readonly int[,] _lookback = new int[64, 3];
        private int[] _pixelBuffer;
        private int _totalPixels;

        // Motion detection to eliminate camera ghosting/smearing
        private RustPlusApi.Data.Cameras.Vector3? _lastCameraPosition;
        private RustPlusApi.Data.Cameras.Vector3? _lastCameraRotation;

        private IReadOnlyList<CameraEntity> _lastEntities = Array.Empty<CameraEntity>();
        private double _lastVfov = 0.0;

        // Rust Standard Colors & Thermal Lookups
        private static readonly float[][] Colours =
        [
            [0.5f, 0.5f, 0.5f], [0.8f, 0.7f, 0.7f], [0.3f, 0.7f, 1f], [0.6f, 0.6f, 0.6f],
            [0.7f, 0.7f, 0.7f], [0.8f, 0.6f, 0.4f], [1f, 0.4f, 0.4f], [1f, 0.1f, 0.1f],
        ];

        private const int SkyColorNormal = unchecked((int)0xFFD0E6FC); // RGB(208, 230, 252)
        private static readonly int[] ThermalLut = GenerateThermalLut();
        private static readonly int[,] PaletteLut = GeneratePaletteLut();
        private static readonly int[,] ThermalPaletteLut = GenerateThermalPaletteLut();

        private CameraSession(RustPlus api, CameraController controller)
        {
            _api = api;
            _controller = controller;

            int w = Width > 0 ? Width : 160;
            int h = Height > 0 ? Height : 120;
            _totalPixels = w * h;
            _samplePositionBuffer = BuildSamplePositionBuffer(w, h);
            _pixelBuffer = new int[_totalPixels];
            Array.Fill(_pixelBuffer, SkyColorNormal);

            _controller.OnFrameReceived += OnFrameReceived;
            _controller.OnKeepAliveFailed += OnKeepAliveFailed;
        }

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

        /// <summary>
        /// Forces an immediate re-render of the current camera frame without waiting for new network packets.
        /// </summary>
        public void ForceRender()
        {
            if (_disposed) return;
            BitmapSource? directBitmap = null;

            lock (_renderLock)
            {
                if (_disposed || _pixelBuffer == null) return;
                int w = Width > 0 ? Width : 160;
                int h = Height > 0 ? Height : 120;

                try
                {
                    var bs = BitmapSource.Create(
                        w, h,
                        96, 96,
                        PixelFormats.Bgr32,
                        null,
                        _pixelBuffer,
                        w * 4
                    );
                    bs.Freeze();
                    directBitmap = bs;
                }
                catch { }
            }

            if (directBitmap != null)
            {
                FrameBitmapRendered?.Invoke(directBitmap);
            }
        }

        private void OnFrameReceived(object? sender, CameraRaysEventArg frame)
        {
            if (_disposed) return;

            IReadOnlyList<CameraEntity> entities = frame.Entities is null
                ? Array.Empty<CameraEntity>()
                : (frame.Entities as IReadOnlyList<CameraEntity>) ?? new List<CameraEntity>(frame.Entities);
            var vfov = frame.VerticalFov;
            _lastEntities = entities;
            _lastVfov = vfov;

            BitmapSource? directBitmap = null;

            lock (_renderLock)
            {
                if (_disposed) return;

                int w = Width > 0 ? Width : 160;
                int h = Height > 0 ? Height : 120;
                if (_pixelBuffer == null || _pixelBuffer.Length != w * h)
                {
                    _totalPixels = w * h;
                    _pixelBuffer = new int[_totalPixels];
                    Array.Fill(_pixelBuffer, _isThermalMode ? ThermalLut[0] : SkyColorNormal);
                }

                // Camera motion detection: if camera translated or rotated, decay or refresh stale pixels
                bool cameraMoved = false;
                if (frame.CameraPosition != null && frame.CameraRotation != null)
                {
                    if (_lastCameraPosition != null && _lastCameraRotation != null)
                    {
                        float posDeltaSq = DistSq(frame.CameraPosition, _lastCameraPosition);
                        float rotDeltaSq = DistSq(frame.CameraRotation, _lastCameraRotation);
                        if (posDeltaSq > 0.04f || rotDeltaSq > 0.02f)
                        {
                            cameraMoved = true;
                        }
                    }
                    _lastCameraPosition = frame.CameraPosition;
                    _lastCameraRotation = frame.CameraRotation;
                }

                // Decode incoming ray stream directly into _pixelBuffer with zero allocations!
                DecodeAndRenderRayStream(frame, w, h, cameraMoved);

                // Highlight detected living entities (Players / Enemies)
                HighlightEntities(entities, vfov, w, h);

                var minIntervalMs = TargetFps >= 60 ? 0.0 : (1000.0 / Math.Max(1, TargetFps));
                var now = DateTime.UtcNow;
                if (minIntervalMs <= 0.0 || (now - _lastRenderUtc).TotalMilliseconds >= minIntervalMs)
                {
                    _lastRenderUtc = now;
                    try
                    {
                        var bs = BitmapSource.Create(
                            w, h,
                            96, 96,
                            PixelFormats.Bgr32,
                            null,
                            _pixelBuffer,
                            w * 4
                        );
                        bs.Freeze();
                        directBitmap = bs;
                    }
                    catch { }
                }
            }

            if (directBitmap != null)
            {
                FrameBitmapRendered?.Invoke(directBitmap);
            }

            EntitiesUpdated?.Invoke(entities, vfov);
        }

        private void DecodeAndRenderRayStream(CameraRaysEventArg frame, int w, int h, bool cameraMoved)
        {
            var rayData = frame.RayData;
            if (rayData == null || rayData.Length < 2) return;

            bool isThermal = _isThermalMode;
            bool isTurbo = _isTurboFpvMode || cameraMoved;
            int skyColor = isThermal ? ThermalLut[0] : SkyColorNormal;

            // When camera moves rapidly in Turbo FPV, gently blend away stale background to prevent ghosting
            if (cameraMoved && isTurbo)
            {
                // Soft background clear for pristine responsive view
                Array.Fill(_pixelBuffer, skyColor);
            }

            var sampleOffset = 2 * frame.SampleOffset;
            int sampleCountMod = 2 * w * h;
            int p = 0;

            while (p < rayData.Length - 1)
            {
                int distance = 0, alignment = 0, material = 0;
                int n = rayData[p++];

                if (n == 255)
                {
                    if (p + 2 >= rayData.Length) break;
                    int l = rayData[p++], o = rayData[p++], s = rayData[p++];
                    distance = (l << 2) | (o >> 6);
                    alignment = 63 & o;
                    material = s;
                    StoreLookback(distance, alignment, material);
                }
                else
                {
                    switch (192 & n)
                    {
                        case 0:
                            LoadLookback(63 & n, out distance, out alignment, out material);
                            break;
                        case 64:
                            if (p >= rayData.Length) break;
                            LoadLookback(63 & n, out distance, out alignment, out material);
                            var g = rayData[p++];
                            distance += (g >> 3) - 15;
                            alignment += (7 & g) - 3;
                            break;
                        case 128:
                            if (p >= rayData.Length) break;
                            LoadLookback(63 & n, out distance, out alignment, out material);
                            distance += rayData[p++] - 127;
                            break;
                        default:
                            if (p + 1 >= rayData.Length) break;
                            int a = rayData[p++], f = rayData[p++];
                            distance = (a << 2) | (f >> 6);
                            alignment = 63 & f;
                            material = 63 & n;
                            StoreLookback(distance, alignment, material);
                            break;
                    }
                }

                // Sample position mapping
                sampleOffset %= sampleCountMod;
                short sampleX = _samplePositionBuffer[sampleOffset++];
                short sampleY = _samplePositionBuffer[sampleOffset++];

                // Rust camera Y is bottom-up, WPF screen Y is top-down
                int screenY = h - 1 - sampleY;
                if (sampleX < 0 || sampleX >= w || screenY < 0 || screenY >= h) continue;

                // Color calculation via instant precalculated LUT
                int color;
                if (distance == 1023 && alignment == 0 && material == 0)
                {
                    color = skyColor;
                }
                else
                {
                    int aIdx = Math.Clamp(alignment, 0, 63);
                    int mIdx = Math.Clamp(material % 8, 0, 7);
                    color = isThermal ? ThermalPaletteLut[aIdx, mIdx] : PaletteLut[aIdx, mIdx];
                }

                if (isTurbo)
                {
                    // Turbo FPV Mode (Progressive 3x3 Splatting):
                    // Fills the screen in 2-3 frames (~100ms) with zero holes and instant responsiveness!
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int py = screenY + dy;
                        if (py < 0 || py >= h) continue;
                        int row = py * w;
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int px = sampleX + dx;
                            if (px >= 0 && px < w)
                            {
                                _pixelBuffer[row + px] = color;
                            }
                        }
                    }
                }
                else
                {
                    // HD Mode: exact 1:1 single pixel mapping for crystal clear details
                    _pixelBuffer[screenY * w + sampleX] = color;
                }
            }
        }

        private void HighlightEntities(IReadOnlyList<CameraEntity> entities, double vfov, int w, int h)
        {
            if (entities == null || entities.Count == 0 || vfov <= 0) return;

            bool isThermal = _isThermalMode;
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

                int radius = Math.Clamp((int)(18.0 / Math.Max(1.0, ez)), 2, 7);
                for (int dy = -radius; dy <= radius; dy++)
                {
                    int py = cy + dy;
                    if (py < 0 || py >= h) continue;
                    int row = py * w;
                    for (int dx = -radius; dx <= radius; dx++)
                    {
                        int px = cx + dx;
                        if (px < 0 || px >= w) continue;
                        int distSq = dx * dx + dy * dy;
                        if (distSq <= radius * radius)
                        {
                            if (isThermal)
                            {
                                _pixelBuffer[row + px] = distSq <= (radius * radius / 3) ? whiteHot : flameHot;
                            }
                            else
                            {
                                _pixelBuffer[row + px] = distSq <= (radius * radius / 3) ? enemyRed : enemyOrange;
                            }
                        }
                    }
                }
            }
        }

        private void StoreLookback(int t, int r, int i)
        {
            var u = ((3 * (t / 128)) + (5 * (r / 16)) + (7 * i)) & 63;
            _lookback[u, 0] = t;
            _lookback[u, 1] = r;
            _lookback[u, 2] = i;
        }

        private void LoadLookback(int index, out int t, out int r, out int i)
        {
            t = _lookback[index, 0];
            r = _lookback[index, 1];
            i = _lookback[index, 2];
        }

        private static byte ToByte(float value)
        {
            int v = (int)value;
            return (byte)Math.Clamp(v, 0, 255);
        }

        private static float DistSq(RustPlusApi.Data.Cameras.Vector3 a, RustPlusApi.Data.Cameras.Vector3 b)
        {
            float dx = a.X - b.X;
            float dy = a.Y - b.Y;
            float dz = a.Z - b.Z;
            return dx * dx + dy * dy + dz * dz;
        }

        private static int[,] GeneratePaletteLut()
        {
            var lut = new int[64, 8];
            for (int a = 0; a < 64; a++)
            {
                float alignment = a / 63f;
                for (int m = 0; m < 8; m++)
                {
                    var pal = Colours[m];
                    byte r = ToByte(alignment * pal[0] * 255f);
                    byte g = ToByte(alignment * pal[1] * 255f);
                    byte b = ToByte(alignment * pal[2] * 255f);
                    lut[a, m] = unchecked((int)((0xFFu << 24) | ((uint)r << 16) | ((uint)g << 8) | (uint)b));
                }
            }
            return lut;
        }

        private static int[,] GenerateThermalPaletteLut()
        {
            var lut = new int[64, 8];
            for (int a = 0; a < 64; a++)
            {
                float alignment = a / 63f;
                for (int m = 0; m < 8; m++)
                {
                    var pal = Colours[m];
                    byte r = ToByte(alignment * pal[0] * 255f);
                    byte g = ToByte(alignment * pal[1] * 255f);
                    byte b = ToByte(alignment * pal[2] * 255f);
                    int lum = (299 * r + 587 * g + 114 * b) / 1000;
                    lut[a, m] = ThermalLut[Math.Clamp(lum, 0, 255)];
                }
            }
            return lut;
        }

        private static int[] GenerateThermalLut()
        {
            var lut = new int[256];
            for (int i = 0; i < 256; i++)
            {
                float t = i / 255.0f;
                byte r, g, b;

                // Classic FLIR / Ironbow gradient
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

                lut[i] = unchecked((int)((0xFFu << 24) | ((uint)r << 16) | ((uint)g << 8) | (uint)b));
            }
            return lut;
        }

        private static short[] BuildSamplePositionBuffer(int width, int height)
        {
            var buffer = new short[width * height * 2];
            for (int w = 0, y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    buffer[w++] = (short)x;
                    buffer[w++] = (short)y;
                }
            }

            var generator = new IndexGenerator(1337);
            for (var rIndex = (width * height) - 1; rIndex >= 1; rIndex--)
            {
                var c = 2 * rIndex;
                var swap = 2 * generator.NextInt(rIndex + 1);

                (buffer[swap], buffer[c]) = (buffer[c], buffer[swap]);
                (buffer[swap + 1], buffer[c + 1]) = (buffer[c + 1], buffer[swap + 1]);
            }

            return buffer;
        }

        private sealed class IndexGenerator
        {
            private int _state;
            public IndexGenerator(int seed)
            {
                _state = seed;
                NextState();
            }
            public int NextInt(int max) => (int)((NextState() * max) / 4294967295L);
            private long NextState()
            {
                unchecked
                {
                    var e = _state;
                    var t = e;
                    e ^= e << 13;
                    e ^= e >>> 17;
                    e ^= e << 5;
                    _state = e;
                    return t >= 0 ? t : 4294967295L + t - 1;
                }
            }
        }

        private void OnKeepAliveFailed(object? sender, ErrorMessage err)
            => KeepAliveFailed?.Invoke(err?.Message ?? "keep-alive failed");

        public Task SendInputAsync(CameraButtons buttons, float mouseDeltaX, float mouseDeltaY)
            => _disposed ? Task.CompletedTask : _controller.SendInputAsync(buttons, mouseDeltaX, mouseDeltaY, CancellationToken.None);

        public Task LookAsync(float deltaX, float deltaY)
            => _disposed ? Task.CompletedTask : _controller.LookAsync(deltaX, deltaY, CancellationToken.None);

        public Task ShootAsync()
            => _disposed ? Task.CompletedTask : _controller.ShootAsync(CancellationToken.None);

        public Task ReloadAsync()
            => _disposed ? Task.CompletedTask : _controller.ReloadAsync(CancellationToken.None);

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
