using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;

namespace RustPlusDesk.Services
{
    public sealed class NumpadSwitchHookService : IDisposable
    {
        private static readonly Lazy<NumpadSwitchHookService> _instance = new(() => new NumpadSwitchHookService());
        public static NumpadSwitchHookService Instance => _instance.Value;

        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;

        private const int VK_NUMPAD0 = 0x60;
        private const int VK_NUMPAD9 = 0x69;

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        private readonly LowLevelKeyboardProc _hookCallback;
        private IntPtr _hookId = IntPtr.Zero;

        // Key: digit (0..9) -> entityId (uint)
        private readonly Dictionary<int, uint> _digitToEntity = new();
        private readonly Dictionary<int, string> _digitToName = new();
        private readonly object _lock = new();

        public event Action<int, uint>? NumpadKeyPressed;

        private bool _isEnabled = true;
        public bool IsEnabled
        {
            get => _isEnabled;
            set => _isEnabled = value;
        }

        private string? _currentServerKey;

        private NumpadSwitchHookService()
        {
            _hookCallback = HookProcedure;
            InstallHook();
            LoadBindings();
        }

        public void SetServer(string? serverKey)
        {
            lock (_lock)
            {
                if (_currentServerKey == serverKey) return;
                _currentServerKey = serverKey;
                LoadBindings();
            }
        }

        private void InstallHook()
        {
            if (_hookId != IntPtr.Zero) return;
            try
            {
                using var curProcess = Process.GetCurrentProcess();
                using var curModule = curProcess.MainModule;
                var moduleHandle = GetModuleHandle(curModule?.ModuleName);
                _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _hookCallback, moduleHandle, 0);
            }
            catch { }
        }

        private void UninstallHook()
        {
            if (_hookId != IntPtr.Zero)
            {
                try { UnhookWindowsHookEx(_hookId); } catch { }
                _hookId = IntPtr.Zero;
            }
        }

        private IntPtr HookProcedure(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && _isEnabled && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
            {
                int vkCode = Marshal.ReadInt32(lParam);
                if (vkCode >= VK_NUMPAD0 && vkCode <= VK_NUMPAD9)
                {
                    int digit = vkCode - VK_NUMPAD0;
                    uint entityId = 0;
                    lock (_lock)
                    {
                        _digitToEntity.TryGetValue(digit, out entityId);
                    }

                    if (entityId != 0)
                    {
                        ThreadPool.QueueUserWorkItem(_ =>
                        {
                            try { NumpadKeyPressed?.Invoke(digit, entityId); } catch { }
                        });
                    }
                }
            }
            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        public void Bind(int digit, uint entityId, string deviceName)
        {
            if (digit < 0 || digit > 9) return;
            lock (_lock)
            {
                // Remove existing if bound to this entity
                var keysToRemove = new List<int>();
                foreach (var (k, v) in _digitToEntity)
                {
                    if (v == entityId) keysToRemove.Add(k);
                }
                foreach (var k in keysToRemove)
                {
                    _digitToEntity.Remove(k);
                    _digitToName.Remove(k);
                }

                _digitToEntity[digit] = entityId;
                _digitToName[digit] = deviceName;
                SaveBindings();
            }
        }

        public void Unbind(int digit)
        {
            lock (_lock)
            {
                _digitToEntity.Remove(digit);
                _digitToName.Remove(digit);
                SaveBindings();
            }
        }

        public void UnbindEntity(uint entityId)
        {
            lock (_lock)
            {
                var keysToRemove = new List<int>();
                foreach (var (k, v) in _digitToEntity)
                {
                    if (v == entityId) keysToRemove.Add(k);
                }
                foreach (var k in keysToRemove)
                {
                    _digitToEntity.Remove(k);
                    _digitToName.Remove(k);
                }
                SaveBindings();
            }
        }

        public int? GetDigitForEntity(uint entityId)
        {
            lock (_lock)
            {
                foreach (var (k, v) in _digitToEntity)
                {
                    if (v == entityId) return k;
                }
                return null;
            }
        }

        public uint GetEntityForDigit(int digit)
        {
            lock (_lock)
            {
                return _digitToEntity.TryGetValue(digit, out var id) ? id : 0;
            }
        }

        public string? GetNameForDigit(int digit)
        {
            lock (_lock)
            {
                return _digitToName.TryGetValue(digit, out var n) ? n : null;
            }
        }

        public Dictionary<int, uint> GetAllBindings()
        {
            lock (_lock)
            {
                return new Dictionary<int, uint>(_digitToEntity);
            }
        }

        private string GetConfigFilePath()
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AlpRust+");
            Directory.CreateDirectory(folder);
            var sKey = string.IsNullOrWhiteSpace(_currentServerKey) ? "global" : _currentServerKey.Replace(":", "_").Replace("/", "_").Replace("\\", "_");
            return Path.Combine(folder, $"numpad_bindings_{sKey}.json");
        }

        private void SaveBindings()
        {
            try
            {
                var file = GetConfigFilePath();
                var data = new Dictionary<string, uint>();
                foreach (var (k, v) in _digitToEntity)
                    data[k.ToString()] = v;
                var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(file, json);
            }
            catch { }
        }

        private void LoadBindings()
        {
            try
            {
                _digitToEntity.Clear();
                _digitToName.Clear();
                var file = GetConfigFilePath();
                if (File.Exists(file))
                {
                    var json = File.ReadAllText(file);
                    var data = JsonSerializer.Deserialize<Dictionary<string, uint>>(json);
                    if (data != null)
                    {
                        foreach (var (kStr, v) in data)
                        {
                            if (int.TryParse(kStr, out var d) && d >= 0 && d <= 9)
                                _digitToEntity[d] = v;
                        }
                    }
                }
            }
            catch { }
        }

        public void Dispose()
        {
            UninstallHook();
        }
    }
}
