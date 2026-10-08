using Microsoft.Win32;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Reflection;
using System.Runtime.Loader;
using System.Windows.Threading;
using RustPlusDesk.Views;
using RustPlusDesk.Services;
using RustPlusDesk.Views.Windows;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using RustPlusDesk.Services.Auth;
using Application = System.Windows.Application;
using Velopack;

namespace RustPlusDesk;

public partial class App : Application
{
    private static Mutex? _single;
    private const string SingleMutexName = "AlpRustPlus_SingleInstance_App";
    private const string PipeName = "AlpRustPlus_Pipe_App";

    private MainWindow? _main;
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> _resourceCache = new();
    private ResourceDictionary? _localizedResources;
    private int _languageApplyVersion;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [STAThread]
    private static void Main(string[] args)
    {
        VelopackApp.Build().Run();

        CrashReporter.Initialize();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    private static void LogStartup(string msg)
    {
        try
        {
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "startup_diag.log"),
                $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\r\n");
        }
        catch { }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        LogStartup("OnStartup started");
        CrashReporter.Initialize(Dispatcher);
        Controls.Menus.MenuFlyout.Initialize();
        AssemblyLoadContext.Default.Resolving += ResolveSatelliteAssemblyFromLangFolder;
        base.OnStartup(e);
        _ = StartupWithSplashAsync(e.Args);
    }

    private async Task StartupWithSplashAsync(string[] args)
    {
        LogStartup("StartupWithSplashAsync started");
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // ── Splash on its own STA thread ────────────────────────────────────────
        SplashWindow? splash = null;
        Dispatcher? splashDispatcher = null;
        var splashReadyTcs = new TaskCompletionSource<bool>();

        var splashThread = new Thread(() =>
        {
            splash = new SplashWindow();
            splashDispatcher = Dispatcher.CurrentDispatcher;
            splash.Show();

            // Signal that the splash is up before starting the message pump
            splashReadyTcs.TrySetResult(true);
            Dispatcher.Run(); // keeps this thread alive and processing messages
        });
        splashThread.SetApartmentState(ApartmentState.STA);
        splashThread.IsBackground = true;
        splashThread.Name = "SplashThread";
        splashThread.Start();

        // Wait for the splash thread to be ready before proceeding
        await splashReadyTcs.Task;
        LogStartup("Splash is up and ready");

        try
        {
            // ── Slow synchronous init on the main thread (splash is already visible) ─
            SetLanguage(applySynchronously: true);
            LogStartup("Language applied");

            bool isBackgroundArg = args.Contains("--background");
            _single = new Mutex(initiallyOwned: true, name: SingleMutexName, createdNew: out bool createdNew);
            LogStartup($"Mutex createdNew={createdNew}");

            if (!createdNew)
            {
                LogStartup("Secondary instance detected - sending command and shutting down");
                // Another instance is running
                if (args.Length > 0 && args[0].StartsWith("rustplus://", StringComparison.OrdinalIgnoreCase))
                    _ = SendLinkToRunningInstanceAsync(args[0]);
                else if (!isBackgroundArg)
                    _ = SendCommandToRunningInstanceAsync("SHOWUI");

                CloseSplashThread(splash, splashDispatcher);
                Shutdown();
                return;
            }

            UpdateSplashStatus(splash, splashDispatcher, "Connecting to Rust+…");
            _ = Services.Cloud.CloudAuth.InitializeAsync();

            UpdateSplashStatus(splash, splashDispatcher, "Preparing system tray…");
            SetupTrayIcon();
            LogStartup("Tray icon setup done");

            if (TrackingService.ShowPlayersTab)
            {
                if (TrackingService.IsBackgroundTrackingEnabled)
                {
                    var (host, port, name) = TrackingService.LastServer;
                    TrackingService.StartPolling(host ?? "", port, name ?? "", TrackingService.LastBMId);
                }
            }
            else if (TrackingService.IsBackgroundTrackingEnabled)
            {
                TrackingService.IsBackgroundTrackingEnabled = false;
            }

            // ── Load MainWindow invisibly on the main thread ─────────────────────────
            UpdateSplashStatus(splash, splashDispatcher, "Loading your servers…");

            bool shouldShowMain = !isBackgroundArg
                || !TrackingService.StartMinimizedEnabled
                || (args.Length > 0 && args[0].StartsWith("rustplus://", StringComparison.OrdinalIgnoreCase));
            LogStartup($"shouldShowMain={shouldShowMain}");

            var mainReadyTcs = new TaskCompletionSource<bool>();
            WindowState targetState = WindowState.Normal;

            if (shouldShowMain)
            {
                LogStartup("Constructing MainWindow...");
                _main = new MainWindow();
                LogStartup("MainWindow constructed successfully");
                MainWindow = _main;
                _main.Closed += (s, ev) => _main = null;
                _main.ContentRendered += (_, _) => {
                    LogStartup("MainWindow ContentRendered fired!");
                    mainReadyTcs.TrySetResult(true);
                };

                targetState = _main.WindowState;

                _main.Opacity = 0;
                _main.ShowActivated = false;
                _main.ShowInTaskbar = false;

                if (_main.WindowState == WindowState.Maximized)
                {
                    _main.WindowState = WindowState.Normal;
                }

                LogStartup("Calling _main.Show()...");
                _main.Show();
                LogStartup("_main.Show() returned");
            }
            else
            {
                mainReadyTcs.SetResult(true);
            }

            LogStartup("Waiting for ContentRendered or 10s timeout...");
            await Task.WhenAny(
                Task.WhenAll(mainReadyTcs.Task, Task.Delay(500)),
                Task.Delay(10000)
            );
            LogStartup($"Finished waiting. mainReadyTcs.IsCompleted={mainReadyTcs.Task.IsCompleted}");

            // ── Fade out splash, reveal MainWindow ───────────────────────────────────
            FadeAndCloseSplash(splash, splashDispatcher);
            await Task.Delay(300);

            if (_main != null)
            {
                LogStartup("Revealing MainWindow (Opacity=1, ShowInTaskbar=true)...");
                _main.ShowInTaskbar = true;
                _main.Opacity = 1;
                _main.WindowState = targetState;
                _main.Activate();
                _main.Topmost = true; _main.Topmost = false;
                LogStartup("MainWindow revealed and activated!");
            }

            _ = StartPipeServerAsync();

            if (args.Length > 0 && args[0].StartsWith("rustplus://", StringComparison.OrdinalIgnoreCase))
                _main?.HandleRustPlusLink(args[0]);

            _ = Task.Run(async () => { await Task.Delay(5000); CleanupLegacyInnoSetupInstallation(); });
            _ = Task.Run(CleanupLegacyNodeRuntime);
            _ = Task.Run(async () => { await Task.Delay(1000); EnsureUrlProtocolRegistered(); });
            LogStartup("StartupWithSplashAsync completed successfully");
        }
        catch (Exception ex)
        {
            LogStartup($"StartupWithSplashAsync EXCEPTION: {ex}");
            System.Diagnostics.Debug.WriteLine($"[Startup] Exception during startup: {ex}");
            try { System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rp_startup_error.txt"), DateTime.Now + "\n" + ex); } catch { }
            FadeAndCloseSplash(splash, splashDispatcher);
            if (_main != null)
            {
                _main.ShowInTaskbar = true;
                _main.Opacity = 1;
                _main.WindowState = WindowState.Normal;
                _main.Show();
                _main.Activate();
            }
        }
    }

    // ── Splash thread helpers ────────────────────────────────────────────────────

    private static void UpdateSplashStatus(SplashWindow? splash, Dispatcher? splashDispatcher, string message)
    {
        if (splash == null || splashDispatcher == null) return;
        splashDispatcher.InvokeAsync(() => splash.SetStatus(message));
    }

    private static void FadeAndCloseSplash(SplashWindow? splash, Dispatcher? splashDispatcher)
    {
        if (splash == null || splashDispatcher == null) return;
        splashDispatcher.InvokeAsync(() =>
        {
            var anim = new System.Windows.Media.Animation.DoubleAnimation
            {
                From = 1,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(250)
            };
            anim.Completed += (_, _) => CloseSplashThread(splash, splashDispatcher);
            splash.BeginAnimation(System.Windows.UIElement.OpacityProperty, anim);
        });
    }

    private static void CloseSplashThread(SplashWindow? splash, Dispatcher? splashDispatcher)
    {
        if (splashDispatcher == null) return;
        splashDispatcher.InvokeAsync(() =>
        {
            splash?.Close();
            splashDispatcher.InvokeShutdown(); // stops Dispatcher.Run() on the splash thread
        });
    }

    // ── Rest of the class (unchanged) ───────────────────────────────────────────

    private static Assembly? ResolveSatelliteAssemblyFromLangFolder(AssemblyLoadContext context, AssemblyName assemblyName)
    {
        if (string.IsNullOrWhiteSpace(assemblyName.Name) ||
            !assemblyName.Name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(assemblyName.CultureName))
        {
            return null;
        }

        string satellitePath = Path.Combine(
            AppContext.BaseDirectory,
            "lang",
            assemblyName.CultureName,
            $"{assemblyName.Name}.dll");

        return File.Exists(satellitePath)
            ? context.LoadFromAssemblyPath(satellitePath)
            : null;
    }

    private void ShowMainWindow()
    {
        LogStartup("ShowMainWindow requested");
        if (_main == null)
        {
            _main = new MainWindow();
            _main.Closed += (s, ev) => _main = null;
        }
        _main.ShowInTaskbar = true;
        _main.Opacity = 1;
        if (_main.WindowState == WindowState.Minimized)
        {
            _main.WindowState = WindowState.Normal;
        }
        _main.Show();
        _main.Activate();
        _main.Topmost = true; _main.Topmost = false;
        LogStartup("ShowMainWindow completed");
    }

    private void SetupTrayIcon()
    {
        _trayIcon = new System.Windows.Forms.NotifyIcon();
        _trayIcon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName!);
        _trayIcon.Text = "AlpRust+";
        _trayIcon.Visible = true;

        var menu = new System.Windows.Forms.ContextMenuStrip();

        menu.Opening += (s, e) =>
        {
            menu.Items.Clear();
            var status = TrackingService.IsTracking ? "Active" : "Idle";
            var last = TrackingService.LastPullTime?.ToString("HH:mm:ss") ?? "--:--:--";

            var statusItem = new System.Windows.Forms.ToolStripMenuItem(string.Format(RustPlusDesk.Properties.Resources.TrayTrackingStatus, status));
            statusItem.Enabled = false;
            menu.Items.Add(statusItem);

            var lastItem = new System.Windows.Forms.ToolStripMenuItem(string.Format(RustPlusDesk.Properties.Resources.TrayLastUpdate, last));
            lastItem.Enabled = false;
            menu.Items.Add(lastItem);

            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("AlpRust+ Aç", null, (s, ex) => ShowMainWindow());
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add(RustPlusDesk.Properties.Resources.Exit, null, (s, ex) =>
            {
                if (_trayIcon != null) _trayIcon.Visible = false;
                Current.Shutdown();
            });
        };

        _trayIcon.MouseUp += (s, e) =>
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Right)
            {
                if (_main == null)
                {
                    _main = new MainWindow();
                    _main.Closed += (s, ev) => _main = null;
                }

                var handle = new System.Windows.Interop.WindowInteropHelper(_main).Handle;
                SetForegroundWindow(handle);
                menu.Show(System.Windows.Forms.Control.MousePosition);
            }
        };

        _trayIcon.DoubleClick += (s, e) => ShowMainWindow();

        CultureChanged += () =>
        {
            Dispatcher.Invoke(() =>
            {
                if (_trayIcon != null)
                {
                    var last = TrackingService.LastPullTime?.ToString("HH:mm:ss") ?? "--:--";
                    _trayIcon.Text = TrackingService.IsTracking
                        ? $"AlpRust+ ({last})"
                        : "AlpRust+";
                }
            });
        };

        TrackingService.OnOnlinePlayersUpdated += () =>
        {
            var last = TrackingService.LastPullTime?.ToString("HH:mm:ss") ?? "--:--";
            Dispatcher.Invoke(() =>
            {
                try
                {
                    if (_trayIcon != null)
                        _trayIcon.Text = $"AlpRust+ ({last})";
                }
                catch { }
            });
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_trayIcon != null) _trayIcon.Visible = false;
        try { NumpadSwitchHookService.Instance.Dispose(); } catch { }
        base.OnExit(e);
    }

    private static void EnsureUrlProtocolRegistered()
    {
        try
        {
            const string scheme = "rustplus";
            using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{scheme}");
            key.SetValue("", "URL: rustplus Protocol");
            key.SetValue("URL Protocol", "");
            using var shell = key.CreateSubKey(@"shell\open\command");
            var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName!;
            shell.SetValue("", $"\"{exe}\" \"%1\"");
        }
        catch { }
    }

    private static async Task SendCommandToRunningInstanceAsync(string cmd)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            await client.ConnectAsync(1500);
            var data = Encoding.UTF8.GetBytes(cmd + "\n");
            await client.WriteAsync(data, 0, data.Length);
            await client.FlushAsync();
        }
        catch { }
    }

    private static async Task SendLinkToRunningInstanceAsync(string link) => await SendCommandToRunningInstanceAsync(link);

    public void SetLanguage(bool applySynchronously = false)
    {
        try
        {
            string lang = TrackingService.SelectedLanguage;
            if (string.Equals(lang, "sr-SP", StringComparison.OrdinalIgnoreCase))
            {
                // Migrate the obsolete culture code used by older builds. Using a
                // real Serbian Latin culture also allows MSBuild to emit a satellite.
                lang = "sr-Latn-RS";
                TrackingService.SelectedLanguage = lang;
            }
            CultureInfo culture;

            if (string.IsNullOrEmpty(lang))
                culture = CultureInfo.InstalledUICulture;
            else
                culture = new CultureInfo(lang);

            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            Thread.CurrentThread.CurrentUICulture = culture;

            RustPlusDesk.Properties.Resources.Culture = culture;

            int version = Interlocked.Increment(ref _languageApplyVersion);

            if (applySynchronously)
            {
                ApplyDynamicResources(GetDynamicResourceMap(culture));
                CultureChanged?.Invoke();
            }
            else
            {
                _ = ApplyLanguageResourcesAsync(culture, version);
            }
        }
        catch { }
    }

    public static event Action? CultureChanged;

    private async Task ApplyLanguageResourcesAsync(CultureInfo culture, int version)
    {
        try
        {
            var resourceMap = await Task.Run(() => GetDynamicResourceMap(culture));
            if (version != Volatile.Read(ref _languageApplyVersion))
                return;

            await Dispatcher.InvokeAsync(() =>
            {
                if (version != Volatile.Read(ref _languageApplyVersion))
                    return;

                ApplyDynamicResources(resourceMap);
                CultureChanged?.Invoke();
            }, DispatcherPriority.Background);
        }
        catch { }
    }

    private static IReadOnlyDictionary<string, string> GetDynamicResourceMap(CultureInfo culture)
    {
        string cacheKey = string.IsNullOrEmpty(culture.Name) ? "invariant" : culture.Name;
        return _resourceCache.GetOrAdd(cacheKey, _ => BuildDynamicResourceMap(culture));
    }

    private static IReadOnlyDictionary<string, string> BuildDynamicResourceMap(CultureInfo culture)
    {
        var rm = RustPlusDesk.Properties.Resources.ResourceManager;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        var neutralSet = rm.GetResourceSet(CultureInfo.InvariantCulture, true, false);
        if (neutralSet != null)
        {
            foreach (System.Collections.DictionaryEntry entry in neutralSet)
            {
                if (entry.Key is string key && entry.Value is string value && !string.IsNullOrWhiteSpace(value))
                    values[key] = value;
            }
        }

        var resourceSet = rm.GetResourceSet(culture, true, true);
        if (resourceSet != null)
        {
            foreach (System.Collections.DictionaryEntry entry in resourceSet)
            {
                if (entry.Key is string key && entry.Value is string value && !string.IsNullOrWhiteSpace(value))
                    values[key] = value;
            }
        }

        return values;
    }

    private void ApplyDynamicResources(IReadOnlyDictionary<string, string> resourceMap)
    {
        var replacement = new ResourceDictionary();
        foreach (var entry in resourceMap)
            replacement[entry.Key] = entry.Value;

        replacement["AppTitle"] = "AlpRust+";
        replacement["OpenRustPlusDesk"] = "AlpRust+ Aç";
        replacement["TrayIconDefault"] = "AlpRust+";

        // Replacing one merged dictionary causes a single resource-tree refresh.
        // Updating ~1,800 Application resources individually made WPF re-evaluate
        // DynamicResource bindings repeatedly and visibly froze the settings UI.
        if (_localizedResources != null)
            Resources.MergedDictionaries.Remove(_localizedResources);
        _localizedResources = replacement;
        Resources.MergedDictionaries.Add(replacement);
    }

    private async Task StartPipeServerAsync()
    {
        while (true)
        {
            using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                                                         PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            try
            {
                await server.WaitForConnectionAsync();
                using var reader = new StreamReader(server, Encoding.UTF8);
                var link = await reader.ReadLineAsync();
                if (!string.IsNullOrWhiteSpace(link) && _main != null)
                {
                    _main.Dispatcher.Invoke(() =>
                    {
                        if (link == "SHOWUI")
                            ShowMainWindow();
                        else if (link.StartsWith("rustplus://", StringComparison.OrdinalIgnoreCase))
                        {
                            ShowMainWindow();
                            _main.HandleRustPlusLink(link);
                        }
                    });
                }
                else if (link == "SHOWUI")
                {
                    Dispatcher.Invoke(ShowMainWindow);
                }
            }
            catch { }
        }
    }

    private static void CleanupLegacyNodeRuntime()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RustPlusDesk", "runtime", "rustplus-cli");
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to remove legacy Node runtime: {ex.Message}");
        }
    }

    private static void CleanupLegacyInnoSetupInstallation()
    {
        try
        {
            string baseDir = AppContext.BaseDirectory;
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!baseDir.StartsWith(localAppData, StringComparison.OrdinalIgnoreCase))
                return;

            string[] possibleKeys = new[]
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{E8E0C4C1-2E2F-4D2D-9BE7-3B19F0C1ABCD}}_is1",
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{E8E0C4C1-2E2F-4D2D-9BE7-3B19F0C1ABCD}_is1"
            };

            string? uninstallString = null;
            foreach (var keyPath in possibleKeys)
            {
                using (var baseKey64 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var subKey64 = baseKey64.OpenSubKey(keyPath))
                    uninstallString = subKey64?.GetValue("UninstallString")?.ToString();

                if (string.IsNullOrEmpty(uninstallString))
                {
                    using (var baseKey32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
                    using (var subKey32 = baseKey32.OpenSubKey(keyPath))
                        uninstallString = subKey32?.GetValue("UninstallString")?.ToString();
                }

                if (!string.IsNullOrEmpty(uninstallString))
                    break;
            }

            if (string.IsNullOrEmpty(uninstallString)) return;

            string uninstallerExe = uninstallString.Replace("\"", "").Trim();
            if (!File.Exists(uninstallerExe)) return;

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = uninstallerExe,
                Arguments = "/SILENT /SUPPRESSMSGBOXES /NORESTART",
                UseShellExecute = true,
                Verb = "runas"
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to trigger legacy cleanup: {ex.Message}");
        }
    }
}
