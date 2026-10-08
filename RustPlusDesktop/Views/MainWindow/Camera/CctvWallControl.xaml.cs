using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Media;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RustPlusApi.Data.Cameras;
using RustPlusDesk.Models;
using RustPlusDesk.Services;
using RustPlusDesk.Services.Camera;
using RustPlusDesk.ViewModels;
using WpfUi = Wpf.Ui.Controls;

namespace RustPlusDesk.Views
{
    public partial class CctvWallControl : UserControl
    {
        private RustPlusClientReal? _real;
        private MainViewModel? _vm;
        private IRustPlusClient? _rust;

        private int _slotCount = 9;
        private int? _maximizedSlot = null;
        private int _targetFps = 15;
        private float _sensitivityMultiplier = 0.06f;
        private bool _isWasdEnabled = false;
        private bool _isThermalEnabled = false;
        private bool _isTurboFpvEnabled = false;
        private int? _activeSlotIndex;
        private CameraButtons _lastSentButtons = CameraButtons.None;
        private readonly System.Windows.Threading.DispatcherTimer _wasdTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
        private readonly HashSet<Key> _heldKeys = new();

        // Slot index -> Camera ID
        private readonly Dictionary<int, string> _slotCameraAssignments = new();
        // Slot index -> CameraSession
        private readonly Dictionary<int, CameraSession> _activeSessions = new();
        // Slot index -> Controls for rapid updates
        private readonly Dictionary<int, SlotControls> _slotControls = new();

        public event EventHandler? RequestBackToMap;

        public bool TryGetCameraFrame(string cameraId, out ImageSource? frame)
        {
            frame = null;
            foreach (var kvp in _slotCameraAssignments)
            {
                if (string.Equals(kvp.Value, cameraId, StringComparison.OrdinalIgnoreCase))
                {
                    if (_slotControls.TryGetValue(kvp.Key, out var ctrl) && ctrl.Img.Source != null)
                    {
                        frame = ctrl.Img.Source;
                        return true;
                    }
                }
            }
            return false;
        }

        private sealed class SlotControls
        {
            public int SlotIndex;
            public Border Container = null!;
            public Image Img = null!;
            public TextBlock StatusText = null!;
            public System.Windows.Shapes.Ellipse StatusDot = null!;
            public ComboBox CamSelector = null!;
            public TextBlock FpsBadge = null!;
            public Button BtnZoom = null!;
            public Button BtnFire = null!;
            public FrameworkElement Crosshair = null!;
            public Border ControlsHud = null!;
            public Border ActiveBadge = null!;
            public Button HudZoomBtn = null!;
            public Button HudFireBtn = null!;
            public Button HudReloadBtn = null!;
            public int FrameCount;
            public DateTime LastFrameTime;
            public bool IsConnecting;
            public Point DragStart;
            public bool IsDragging;
        }

        public CctvWallControl()
        {
            InitializeComponent();
            Focusable = true;
            _wasdTimer.Tick += WasdTimer_Tick;
            PreviewKeyDown += (_, e) => HandlePreviewKeyDown(e);
            PreviewKeyUp += (_, e) => HandlePreviewKeyUp(e);
        }

        public void Activate(RustPlusClientReal? real, MainViewModel vm, IRustPlusClient rust)
        {
            _real = real;
            _vm = vm;
            _rust = rust;

            LoadSlotAssignments();
            HighlightLayoutButton(_slotCount);
            RebuildWallGrid();
        }

        public async Task DeactivateAsync()
        {
            ClearHeldKeys();
            SetWasdEnabled(false);
            await StopAllSessionsAsync().ConfigureAwait(false);
        }

        private void HighlightLayoutButton(int count)
        {
            foreach (var child in LayoutButtonsPanel.Children.OfType<Button>())
            {
                if (child.Tag?.ToString() == count.ToString())
                {
                    child.Tag = "Active";
                    child.Background = new SolidColorBrush(Color.FromRgb(16, 185, 129));
                    child.Foreground = Brushes.White;
                }
                else
                {
                    child.Tag = child.Content?.ToString();
                    child.Background = new SolidColorBrush(Color.FromRgb(34, 39, 48));
                    child.Foreground = new SolidColorBrush(Color.FromRgb(229, 231, 235));
                }
            }
        }

        private void LayoutBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && int.TryParse(btn.Content?.ToString(), out var count))
            {
                if (_slotCount != count || _maximizedSlot != null)
                {
                    _maximizedSlot = null;
                    _slotCount = count;
                    HighlightLayoutButton(count);
                    SaveSlotAssignments();
                    RebuildWallGrid();
                }
            }
        }

        private void CmbWallFps_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbWallFps?.SelectedItem is ComboBoxItem item)
            {
                var content = item.Content?.ToString() ?? "";
                if (content.Contains("5 FPS") || content == "5") _targetFps = 5;
                else if (content.Contains("10 FPS") || content == "10") _targetFps = 10;
                else if (content.Contains("15 FPS") || content == "15") _targetFps = 15;
                else if (content.Contains("30")) _targetFps = 30;
                else if (content.Contains("60")) _targetFps = 60;
                else if (content.Contains("120")) _targetFps = 120;
                else if (content.Contains("Max")) _targetFps = 1000;

                foreach (var session in _activeSessions.Values)
                {
                    session.TargetFps = _targetFps;
                }
            }
        }

        private void CmbSensitivity_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbSensitivity?.SelectedItem is ComboBoxItem item &&
                float.TryParse(item.Tag as string, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float val))
            {
                _sensitivityMultiplier = val;
            }
        }

        private void BtnBackToMap_Click(object sender, RoutedEventArgs e)
        {
            RequestBackToMap?.Invoke(this, EventArgs.Empty);
        }

        // =========================================================================
        // GLOBAL REFRESH (SKIPS DRONES FOR SAFETY)
        // =========================================================================

        private void BtnRefreshAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var kvp in _slotCameraAssignments.ToList())
            {
                int slotIndex = kvp.Key;
                string camId = kvp.Value;
                if (string.IsNullOrWhiteSpace(camId)) continue;

                // Drone DÃ¼ÅŸme KorumasÄ±: Havadaki drone'un motorlarÄ± durup dÃ¼ÅŸmesin!
                if (_activeSessions.TryGetValue(slotIndex, out var ses) && ses.IsDrone)
                {
                    continue;
                }

                _ = StartSlotSessionAsync(slotIndex, camId);
            }
        }

        // =========================================================================
        // TURBO FPV MODE (LOW-RES SUPERPIXEL FAST SPLATTING)
        // =========================================================================

        private void BtnToggleTurboFpv_Click(object sender, RoutedEventArgs e)
        {
            SetTurboFpvEnabled(!_isTurboFpvEnabled);
        }

        public void SetTurboFpvEnabled(bool enabled)
        {
            _isTurboFpvEnabled = enabled;
            if (_isTurboFpvEnabled)
            {
                BtnToggleTurboFpv.Background = new SolidColorBrush(Color.FromRgb(59, 130, 246)); // Vivid Blue
                BtnToggleTurboFpv.Foreground = Brushes.White;
                TxtTurboFpvStatus.Text = "Turbo FPV: AÃ§Ä±k";
            }
            else
            {
                BtnToggleTurboFpv.Background = new SolidColorBrush(Color.FromRgb(34, 39, 48));
                BtnToggleTurboFpv.Foreground = new SolidColorBrush(Color.FromRgb(229, 231, 235));
                TxtTurboFpvStatus.Text = "Turbo FPV: KapalÄ±";
            }

            foreach (var session in _activeSessions.Values)
            {
                session.IsTurboFpvMode = _isTurboFpvEnabled;
            }
        }

        // =========================================================================
        // THERMAL VISION MODE (FLIR)
        // =========================================================================

        private void BtnToggleThermal_Click(object sender, RoutedEventArgs e)
        {
            SetThermalEnabled(!_isThermalEnabled);
        }

        public void SetThermalEnabled(bool enabled)
        {
            _isThermalEnabled = enabled;
            if (_isThermalEnabled)
            {
                BtnToggleThermal.Background = new SolidColorBrush(Color.FromRgb(245, 158, 11)); // Amber
                BtnToggleThermal.Foreground = Brushes.Black;
                TxtThermalStatus.Text = "Termal: AÃ§Ä±k";
            }
            else
            {
                BtnToggleThermal.Background = new SolidColorBrush(Color.FromRgb(34, 39, 48));
                BtnToggleThermal.Foreground = new SolidColorBrush(Color.FromRgb(229, 231, 235));
                TxtThermalStatus.Text = "Termal: KapalÄ±";
            }

            foreach (var session in _activeSessions.Values)
            {
                session.IsThermalMode = _isThermalEnabled;
                session.IsTurboFpvMode = _isTurboFpvEnabled;
            }
        }

        // =========================================================================
        // WASD KEYBOARD & MOUSE NAVIGATION
        // =========================================================================

        private void BtnToggleWasd_Click(object sender, RoutedEventArgs e)
        {
            SetWasdEnabled(!_isWasdEnabled);
        }

        public void SetWasdEnabled(bool enabled)
        {
            _isWasdEnabled = enabled;
            if (_isWasdEnabled)
            {
                _wasdTimer.Start();
                BtnToggleWasd.Background = new SolidColorBrush(Color.FromRgb(16, 185, 129));
                BtnToggleWasd.Foreground = Brushes.White;
                TxtWasdIcon.Text = "ğŸ®";
                TxtWasdStatus.Text = "WASD: AÃ§Ä±k";

                if (!_activeSlotIndex.HasValue || !_activeSessions.ContainsKey(_activeSlotIndex.Value))
                {
                    if (_maximizedSlot.HasValue && _activeSessions.ContainsKey(_maximizedSlot.Value))
                    {
                        _activeSlotIndex = _maximizedSlot.Value;
                    }
                    else
                    {
                        var first = _activeSessions.Keys.OrderBy(k => k).FirstOrDefault();
                        _activeSlotIndex = first;
                    }
                }
            }
            else
            {
                _wasdTimer.Stop();
                ClearHeldKeys();
                BtnToggleWasd.Background = new SolidColorBrush(Color.FromRgb(34, 39, 48));
                BtnToggleWasd.Foreground = new SolidColorBrush(Color.FromRgb(229, 231, 235));
                TxtWasdIcon.Text = "âŒ¨ï¸";
                TxtWasdStatus.Text = "WASD: KapalÄ±";
            }
            UpdateActiveSlotVisuals();
        }

        public void SetActiveSlot(int slotIndex)
        {
            _activeSlotIndex = slotIndex;
            UpdateActiveSlotVisuals();
        }

        public void UpdateActiveSlotVisuals()
        {
            foreach (var kvp in _slotControls)
            {
                int idx = kvp.Key;
                var ctrl = kvp.Value;
                bool isActive = _isWasdEnabled && _activeSlotIndex == idx;

                if (isActive)
                {
                    ctrl.Container.BorderBrush = new SolidColorBrush(Color.FromRgb(16, 185, 129));
                    ctrl.Container.BorderThickness = new Thickness(2);
                    if (ctrl.ActiveBadge != null) ctrl.ActiveBadge.Visibility = Visibility.Visible;
                }
                else
                {
                    ctrl.Container.BorderBrush = new SolidColorBrush(Color.FromRgb(31, 36, 45));
                    ctrl.Container.BorderThickness = new Thickness(1);
                    if (ctrl.ActiveBadge != null) ctrl.ActiveBadge.Visibility = Visibility.Collapsed;
                }
            }
        }

        private static bool IsControlKey(Key k) =>
            k is Key.W or Key.A or Key.S or Key.D or
                 Key.Up or Key.Down or Key.Left or Key.Right or
                 Key.Space or Key.LeftShift or Key.RightShift or
                 Key.LeftCtrl or Key.RightCtrl or Key.C or Key.R;

        public bool HandlePreviewKeyDown(KeyEventArgs e)
        {
            if (!_isWasdEnabled) return false;

            var focused = Keyboard.FocusedElement;
            if (focused is System.Windows.Controls.Primitives.TextBoxBase ||
                focused is PasswordBox ||
                focused is ComboBox)
            {
                return false;
            }

            // Space veya Escape: Acil Fren! TÃ¼m hareket girdilerini anÄ±nda keser, drone havada hover moduna geÃ§er
            if (e.Key == Key.Space || e.Key == Key.Escape)
            {
                ClearHeldKeys();

                if (_activeSlotIndex.HasValue && _activeSessions.TryGetValue(_activeSlotIndex.Value, out var ses))
                {
                    if (ses.IsDrone || ses.ControlFlags.HasFlag(CameraControlFlags.Movement))
                    {
                        _ = ses.SendInputAsync(CameraButtons.None, 0f, 0f);
                        _lastSentButtons = CameraButtons.None;
                        if (TrackingService.DroneDynamicFpvEnabled && !_isTurboFpvEnabled && ses.IsTurboFpvMode)
                        {
                            ses.IsTurboFpvMode = false; // Hover/Durdu: otomatik anÄ±nda HD moda dÃ¶n
                        }
                    }
                }

                e.Handled = true;
                return true;
            }

            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (IsControlKey(key))
            {
                lock (_heldKeys) { _heldKeys.Add(key); }
                e.Handled = true;
                return true;
            }
            return false;
        }

        public bool HandlePreviewKeyUp(KeyEventArgs e)
        {
            if (!_isWasdEnabled) return false;

            var focused = Keyboard.FocusedElement;
            if (focused is System.Windows.Controls.Primitives.TextBoxBase ||
                focused is PasswordBox ||
                focused is ComboBox)
            {
                return false;
            }

            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (IsControlKey(key))
            {
                lock (_heldKeys) { _heldKeys.Remove(key); }
                e.Handled = true;
                return true;
            }
            return false;
        }

        public void ClearHeldKeys()
        {
            lock (_heldKeys) { _heldKeys.Clear(); }
            if (_activeSlotIndex.HasValue && _activeSessions.TryGetValue(_activeSlotIndex.Value, out var s))
            {
                if (s.IsDrone || s.ControlFlags.HasFlag(CameraControlFlags.Movement))
                {
                    _ = s.SendInputAsync(CameraButtons.None, 0f, 0f);
                    _lastSentButtons = CameraButtons.None;
                    if (TrackingService.DroneDynamicFpvEnabled && !_isTurboFpvEnabled && s.IsTurboFpvMode)
                    {
                        s.IsTurboFpvMode = false;
                    }
                }
            }
        }

        private void WasdTimer_Tick(object? sender, EventArgs e)
        {
            if (!_isWasdEnabled || !_activeSlotIndex.HasValue) return;
            if (!_activeSessions.TryGetValue(_activeSlotIndex.Value, out var session)) return;

            HashSet<Key> keys;
            lock (_heldKeys) { keys = new HashSet<Key>(_heldKeys); }

            bool isMovementDrone = session.IsDrone || session.ControlFlags.HasFlag(CameraControlFlags.Movement);

            if (keys.Count == 0)
            {
                if (isMovementDrone)
                {
                    if (_lastSentButtons != CameraButtons.None)
                    {
                        _ = session.SendInputAsync(CameraButtons.None, 0f, 0f);
                        _lastSentButtons = CameraButtons.None;
                    }

                    // Dinamik FPV: Drone hareket etmediÄŸinde otomatik HD moda dÃ¶n
                    if (TrackingService.DroneDynamicFpvEnabled && !_isTurboFpvEnabled && session.IsTurboFpvMode)
                    {
                        session.IsTurboFpvMode = false;
                    }
                }
                return;
            }

            bool isForward = keys.Contains(Key.W) || keys.Contains(Key.Up);
            bool isBackward = keys.Contains(Key.S) || keys.Contains(Key.Down);
            bool isLeft = keys.Contains(Key.A) || keys.Contains(Key.Left);
            bool isRight = keys.Contains(Key.D) || keys.Contains(Key.Right);
            bool isUp = keys.Contains(Key.LeftShift) || keys.Contains(Key.RightShift); // Shift = YukarÄ± (Ascend)
            bool isDown = keys.Contains(Key.LeftCtrl) || keys.Contains(Key.RightCtrl) || keys.Contains(Key.C); // Ctrl = AÅŸaÄŸÄ± (Descend)
            bool isReload = keys.Contains(Key.R);

            if (isReload && (session.IsAutoTurret || session.ControlFlags.HasFlag(CameraControlFlags.Reload)))
            {
                _ = session.ReloadAsync();
            }

            if (isMovementDrone)
            {
                // Dinamik FPV: Drone hareket halindeyken otomatik Turbo FPV moduna geÃ§!
                if (TrackingService.DroneDynamicFpvEnabled && !session.IsTurboFpvMode)
                {
                    session.IsTurboFpvMode = true;
                }

                var buttons = CameraButtons.None;
                if (isForward) buttons |= CameraButtons.Forward;
                if (isBackward) buttons |= CameraButtons.Backward;
                if (isLeft) buttons |= CameraButtons.Left;
                if (isRight) buttons |= CameraButtons.Right;
                if (isUp) buttons |= CameraButtons.Jump;   // Shift = YukarÄ±
                if (isDown) buttons |= CameraButtons.Duck; // Ctrl = AÅŸaÄŸÄ±

                _ = session.SendInputAsync(buttons, 0f, 0f);
                _lastSentButtons = buttons;
            }
            else
            {
                // PTZ / Turret / Static camera look control
                float baseSpeed = 4.0f * (_sensitivityMultiplier / 0.06f);
                if (isUp) baseSpeed *= 1.8f;

                float dx = 0;
                float dy = 0;

                if (isLeft) dx -= baseSpeed;
                if (isRight) dx += baseSpeed;
                if (isForward) dy += baseSpeed;  // Tilt up
                if (isBackward) dy += baseSpeed; // Tilt down

                if (dx != 0 || dy != 0)
                {
                    _ = session.LookAsync(dx, dy);
                }
            }
        }

        // =========================================================================
        // CAMERA WALL GRID BUILDER
        // =========================================================================

        public void RebuildWallGrid()
        {
            CameraWallGrid.Children.Clear();
            CameraWallGrid.RowDefinitions.Clear();
            CameraWallGrid.ColumnDefinitions.Clear();

            int rows, cols;
            if (_maximizedSlot.HasValue)
            {
                rows = 1;
                cols = 1;
            }
            else
            {
                (rows, cols) = GetGridDimensions(_slotCount);
            }

            for (int r = 0; r < rows; r++)
                CameraWallGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            for (int c = 0; c < cols; c++)
                CameraWallGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var availableCamIds = _vm?.Selected?.CameraIds?.ToList() ?? new List<string>();

            int count = _maximizedSlot.HasValue ? 1 : _slotCount;
            for (int i = 0; i < count; i++)
            {
                int slotIndex = _maximizedSlot.HasValue ? _maximizedSlot.Value : i;

                // Auto-assign camera if unassigned and available
                if (!_slotCameraAssignments.ContainsKey(slotIndex) && i < availableCamIds.Count)
                {
                    _slotCameraAssignments[slotIndex] = availableCamIds[i];
                }

                var tile = BuildSlotTile(slotIndex, availableCamIds);

                int r = _maximizedSlot.HasValue ? 0 : (i / cols);
                int c = _maximizedSlot.HasValue ? 0 : (i % cols);

                Grid.SetRow(tile, r);
                Grid.SetColumn(tile, c);
                CameraWallGrid.Children.Add(tile);

                // Start session if assigned
                if (_slotCameraAssignments.TryGetValue(slotIndex, out var camId) && !string.IsNullOrWhiteSpace(camId))
                {
                    _ = StartSlotSessionAsync(slotIndex, camId);
                }
            }

            // Prune sessions for slots no longer visible
            var validIndices = _maximizedSlot.HasValue
                ? new HashSet<int> { _maximizedSlot.Value }
                : Enumerable.Range(0, _slotCount).ToHashSet();

            var toRemove = _activeSessions.Keys.Where(k => !validIndices.Contains(k)).ToList();
            foreach (var idx in toRemove)
            {
                StopSlotSession(idx);
            }

            UpdateActiveSlotVisuals();
        }

        private static (int rows, int cols) GetGridDimensions(int count) => count switch
        {
            1 => (1, 1),
            2 => (1, 2),
            4 => (2, 2),
            6 => (2, 3),
            8 => (2, 4),
            9 => (3, 3),
            10 => (2, 5),
            12 => (3, 4),
            _ => (3, 3)
        };

        private Border BuildSlotTile(int slotIndex, List<string> availableCamIds)
        {
            var container = new Border
            {
                Margin = new Thickness(1),
                Background = new SolidColorBrush(Color.FromRgb(14, 17, 22)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(31, 36, 45)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                ClipToBounds = true
            };

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Header bar
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Video feed

            // Top Header Bar (Ultra-slim 20-22px)
            var topBar = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(220, 18, 22, 29)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(31, 36, 45)),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(4, 2, 4, 2)
            };

            var topDock = new DockPanel { LastChildFill = true };

            // Left: Slot badge
            var slotBadge = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(3, 0, 3, 0),
                Margin = new Thickness(0, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            slotBadge.Child = new TextBlock
            {
                Text = $"#{slotIndex + 1}",
                FontSize = 9.5,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White
            };
            DockPanel.SetDock(slotBadge, Dock.Left);
            topDock.Children.Add(slotBadge);

            // Right buttons: PTZ Zoom, Turret Fire, FPS badge, Reconnect, Maximize
            var rightStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(rightStack, Dock.Right);

            var btnZoom = new Button
            {
                Content = "ğŸ”",
                ToolTip = "YakÄ±nlaÅŸtÄ±r / Zoom (PTZ)",
                Width = 18,
                Height = 18,
                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                Foreground = new SolidColorBrush(Color.FromRgb(96, 165, 250)),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                FontSize = 10,
                Margin = new Thickness(0, 0, 2, 0),
                Visibility = Visibility.Collapsed
            };
            btnZoom.Click += (_, __) =>
            {
                if (_activeSessions.TryGetValue(slotIndex, out var s)) _ = s.ZoomAsync();
            };
            rightStack.Children.Add(btnZoom);

            var btnFire = new Button
            {
                Content = "ğŸ”¥",
                ToolTip = "AteÅŸ Et / Fire (Taret)",
                Width = 18,
                Height = 18,
                Padding = new Thickness(0),
                Background = new SolidColorBrush(Color.FromRgb(185, 28, 28)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                FontSize = 10,
                Margin = new Thickness(0, 0, 3, 0),
                Visibility = Visibility.Collapsed
            };
            btnFire.Click += (_, __) =>
            {
                if (_activeSessions.TryGetValue(slotIndex, out var s)) _ = s.ShootAsync();
            };
            rightStack.Children.Add(btnFire);

            var fpsBadge = new TextBlock
            {
                Text = "0 FPS",
                FontSize = 9,
                Foreground = new SolidColorBrush(Color.FromRgb(156, 163, 175)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0)
            };
            rightStack.Children.Add(fpsBadge);

            var btnReconnect = new Button
            {
                Content = "â†»",
                ToolTip = "KamerayÄ± yeniden baÄŸla",
                Width = 18,
                Height = 18,
                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                Foreground = Brushes.LightGray,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                FontSize = 11,
                Margin = new Thickness(0, 0, 2, 0)
            };
            btnReconnect.Click += (_, __) =>
            {
                if (_slotCameraAssignments.TryGetValue(slotIndex, out var cid) && !string.IsNullOrWhiteSpace(cid))
                {
                    _ = StartSlotSessionAsync(slotIndex, cid);
                }
            };
            rightStack.Children.Add(btnReconnect);

            var btnMaximize = new Button
            {
                Content = _maximizedSlot == slotIndex ? "â" : "â›¶",
                ToolTip = _maximizedSlot == slotIndex ? "Izgara gÃ¶rÃ¼nÃ¼mÃ¼ne dÃ¶n" : "Tam ekran yap",
                Width = 18,
                Height = 18,
                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                Foreground = Brushes.LightGray,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                FontSize = 10
            };
            btnMaximize.Click += (_, __) => ToggleMaximizeSlot(slotIndex);
            rightStack.Children.Add(btnMaximize);

            topDock.Children.Add(rightStack);

            // Center: Status dot + Selector ComboBox (Compact)
            var centerStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            var statusDot = new System.Windows.Shapes.Ellipse
            {
                Width = 6,
                Height = 6,
                Fill = new SolidColorBrush(Color.FromRgb(107, 114, 128)),
                Margin = new Thickness(0, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            centerStack.Children.Add(statusDot);

            var camSelector = new ComboBox
            {
                Height = 20,
                FontSize = 10,
                MinWidth = 70,
                MaxWidth = 135,
                Padding = new Thickness(3, 0, 3, 0),
                VerticalAlignment = VerticalAlignment.Center
            };

            camSelector.Items.Add(new ComboBoxItem { Content = "-- BoÅŸ --", Tag = "" });
            foreach (var cid in availableCamIds)
            {
                var friendlyName = _vm?.Selected?.CameraNames != null && _vm.Selected.CameraNames.TryGetValue(cid, out var n)
                    ? $"{cid} ({n})"
                    : cid;
                camSelector.Items.Add(new ComboBoxItem { Content = friendlyName, Tag = cid });
            }

            // Select active camera
            if (_slotCameraAssignments.TryGetValue(slotIndex, out var currentCid))
            {
                var match = camSelector.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (i.Tag as string) == currentCid);
                if (match != null) camSelector.SelectedItem = match;
            }
            else
            {
                camSelector.SelectedIndex = 0;
            }

            camSelector.SelectionChanged += (_, __) =>
            {
                if (camSelector.SelectedItem is ComboBoxItem selItem)
                {
                    var chosenId = (selItem.Tag as string) ?? "";
                    if (string.IsNullOrWhiteSpace(chosenId))
                    {
                        _slotCameraAssignments.Remove(slotIndex);
                        StopSlotSession(slotIndex);
                    }
                    else
                    {
                        _slotCameraAssignments[slotIndex] = chosenId;
                        _ = StartSlotSessionAsync(slotIndex, chosenId);
                    }
                    SaveSlotAssignments();
                }
            };
            centerStack.Children.Add(camSelector);

            topDock.Children.Add(centerStack);
            topBar.Child = topDock;
            Grid.SetRow(topBar, 0);
            grid.Children.Add(topBar);

            // Video Center Area
            var videoContainer = new Grid
            {
                Background = new SolidColorBrush(Color.FromRgb(8, 10, 14)),
                ClipToBounds = true
            };

            var img = new Image
            {
                Stretch = Stretch.Uniform,
                SnapsToDevicePixels = true,
                UseLayoutRounding = true
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.Linear);
            videoContainer.Children.Add(img);

            // Crosshair overlay (Auto-turret or crosshair-enabled cameras)
            var crosshair = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            crosshair.Children.Add(new Border
            {
                Width = 20, Height = 1.5,
                Background = new SolidColorBrush(Color.FromArgb(190, 239, 68, 68))
            });
            crosshair.Children.Add(new Border
            {
                Width = 1.5, Height = 20,
                Background = new SolidColorBrush(Color.FromArgb(190, 239, 68, 68))
            });
            crosshair.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 6, Height = 6,
                Stroke = new SolidColorBrush(Color.FromArgb(210, 239, 68, 68)),
                StrokeThickness = 1
            });
            videoContainer.Children.Add(crosshair);

            var statusText = new TextBlock
            {
                Text = "Kamera SeÃ§in",
                Foreground = new SolidColorBrush(Color.FromRgb(107, 114, 128)),
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            videoContainer.Children.Add(statusText);

            // Double click to maximize / restore
            videoContainer.MouseLeftButtonDown += (s, ev) =>
            {
                if (ev.ClickCount == 2)
                {
                    ToggleMaximizeSlot(slotIndex);
                    ev.Handled = true;
                }
            };

            // On-screen HUD Controls Overlay (Bottom center of videoContainer)
            var controlsHud = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(200, 15, 23, 42)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(160, 51, 65, 85)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Margin = new Thickness(0, 0, 0, 6),
                Padding = new Thickness(4, 2, 4, 2),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Visibility = Visibility.Collapsed
            };

            var hudPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            Button MakeHudBtn(string text, string tip, Action action)
            {
                var b = new Button
                {
                    Content = text,
                    ToolTip = tip,
                    Width = 22, Height = 20,
                    Margin = new Thickness(1, 0, 1, 0),
                    Padding = new Thickness(0),
                    Background = new SolidColorBrush(Color.FromArgb(220, 30, 41, 59)),
                    Foreground = Brushes.White,
                    BorderBrush = new SolidColorBrush(Color.FromArgb(180, 71, 85, 105)),
                    BorderThickness = new Thickness(1),
                    FontSize = 10,
                    Cursor = Cursors.Hand
                };
                b.Click += (_, __) => action();
                return b;
            }

            hudPanel.Children.Add(MakeHudBtn("â—€", "Sola Ã‡evir (Pan Sol)", () => {
                if (_activeSessions.TryGetValue(slotIndex, out var s)) _ = s.LookAsync(-3f, 0);
            }));
            hudPanel.Children.Add(MakeHudBtn("â–²", "YukarÄ± Ã‡evir (Tilt YukarÄ±)", () => {
                if (_activeSessions.TryGetValue(slotIndex, out var s)) _ = s.LookAsync(0, 3f);
            }));
            hudPanel.Children.Add(MakeHudBtn("â–¼", "AÅŸaÄŸÄ± Ã‡evir (Tilt AÅŸaÄŸÄ±)", () => {
                if (_activeSessions.TryGetValue(slotIndex, out var s)) _ = s.LookAsync(0, -3f);
            }));
            hudPanel.Children.Add(MakeHudBtn("â–¶", "SaÄŸa Ã‡evir (Pan SaÄŸ)", () => {
                if (_activeSessions.TryGetValue(slotIndex, out var s)) _ = s.LookAsync(3f, 0);
            }));

            var hudZoomBtn = MakeHudBtn("ğŸ”", "YakÄ±nlaÅŸtÄ±r / Zoom (PTZ)", () => {
                if (_activeSessions.TryGetValue(slotIndex, out var s)) _ = s.ZoomAsync();
            });
            hudPanel.Children.Add(hudZoomBtn);

            var hudFireBtn = MakeHudBtn("ğŸ”¥", "AteÅŸ Et / Fire (Taret)", () => {
                if (_activeSessions.TryGetValue(slotIndex, out var s)) _ = s.ShootAsync();
            });
            hudFireBtn.Background = new SolidColorBrush(Color.FromArgb(220, 185, 28, 28));
            hudFireBtn.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 239, 68, 68));
            hudPanel.Children.Add(hudFireBtn);

            var hudReloadBtn = MakeHudBtn("ğŸ”„", "Mermi Doldur / Reload", () => {
                if (_activeSessions.TryGetValue(slotIndex, out var s)) _ = s.ReloadAsync();
            });
            hudPanel.Children.Add(hudReloadBtn);

            var hudCrossBtn = MakeHudBtn("ğŸ¯", "Hedef NoktasÄ± (Crosshair) AÃ§/Kapa", () => {
                crosshair.Visibility = crosshair.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            });
            hudPanel.Children.Add(hudCrossBtn);

            controlsHud.Child = hudPanel;
            videoContainer.Children.Add(controlsHud);

            // Active WASD Slot Badge Overlay (Top Left)
            var activeBadge = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(220, 6, 78, 59)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(240, 52, 211, 153)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2, 6, 2),
                Margin = new Thickness(6, 6, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false
            };
            activeBadge.Child = new TextBlock
            {
                Text = "ğŸ® WASD & FARE AKTÄ°F",
                FontSize = 9.5,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(209, 250, 229))
            };
            videoContainer.Children.Add(activeBadge);

            var slotCtrl = new SlotControls
            {
                SlotIndex = slotIndex,
                Container = container,
                Img = img,
                StatusText = statusText,
                StatusDot = statusDot,
                CamSelector = camSelector,
                FpsBadge = fpsBadge,
                BtnZoom = btnZoom,
                BtnFire = btnFire,
                Crosshair = crosshair,
                ControlsHud = controlsHud,
                ActiveBadge = activeBadge,
                HudZoomBtn = hudZoomBtn,
                HudFireBtn = hudFireBtn,
                HudReloadBtn = hudReloadBtn
            };
            _slotControls[slotIndex] = slotCtrl;

            videoContainer.MouseEnter += (_, __) => {
                if (_activeSessions.ContainsKey(slotIndex)) controlsHud.Visibility = Visibility.Visible;
            };
            videoContainer.MouseLeave += (_, __) => {
                if (!slotCtrl.IsDragging) controlsHud.Visibility = Visibility.Collapsed;
            };

            // Interactive Controls: Mouse Drag Look, Right Click Shoot, Wheel Zoom
            videoContainer.MouseDown += (s, ev) =>
            {
                SetActiveSlot(slotIndex);
                Focus();

                if (ev.ChangedButton == MouseButton.Left)
                {
                    if (_activeSessions.TryGetValue(slotIndex, out var ses) &&
                        (ses.IsPtzCamera || ses.IsAutoTurret || ses.ControlFlags.HasFlag(CameraControlFlags.Mouse)))
                    {
                        slotCtrl.IsDragging = true;
                        slotCtrl.DragStart = ev.GetPosition(videoContainer);
                        videoContainer.CaptureMouse();
                    }
                }
                else if (ev.ChangedButton == MouseButton.Right)
                {
                    // Right-click fires auto turret
                    if (_activeSessions.TryGetValue(slotIndex, out var ses) && ses.IsAutoTurret)
                    {
                        _ = ses.ShootAsync();
                        ev.Handled = true;
                    }
                }
            };

            videoContainer.MouseMove += (s, ev) =>
            {
                if (slotCtrl.IsDragging && _activeSessions.TryGetValue(slotIndex, out var ses))
                {
                    var pos = ev.GetPosition(videoContainer);
                    var dx = (float)(pos.X - slotCtrl.DragStart.X);
                    var dy = (float)(pos.Y - slotCtrl.DragStart.Y);
                    slotCtrl.DragStart = pos;
                    // Standard inverted Y for pitch, controlled via _sensitivityMultiplier
                    _ = ses.LookAsync(dx * _sensitivityMultiplier, -dy * _sensitivityMultiplier);
                }
            };

            videoContainer.MouseUp += (s, ev) =>
            {
                if (ev.ChangedButton == MouseButton.Left && slotCtrl.IsDragging)
                {
                    slotCtrl.IsDragging = false;
                    videoContainer.ReleaseMouseCapture();
                }
            };

            videoContainer.MouseWheel += (s, ev) =>
            {
                if (_activeSessions.TryGetValue(slotIndex, out var ses) && ses.IsPtzCamera)
                {
                    _ = ses.ZoomAsync();
                    ev.Handled = true;
                }
            };

            Grid.SetRow(videoContainer, 1);
            grid.Children.Add(videoContainer);

            container.Child = grid;
            return container;
        }

        private void ToggleMaximizeSlot(int slotIndex)
        {
            if (_maximizedSlot == slotIndex)
            {
                _maximizedSlot = null;
            }
            else
            {
                _maximizedSlot = slotIndex;
            }
            RebuildWallGrid();
        }

        // =========================================================================
        // CAMERA SESSION LIFECYCLE & COORDINATION
        // =========================================================================

        public bool TryReleaseCameraSession(string cameraId)
        {
            foreach (var kvp in _slotCameraAssignments.ToList())
            {
                if (string.Equals(kvp.Value, cameraId, StringComparison.OrdinalIgnoreCase))
                {
                    StopSlotSession(kvp.Key);
                    return true;
                }
            }
            return false;
        }

        public void ResumeCameraSession(string cameraId)
        {
            foreach (var kvp in _slotCameraAssignments)
            {
                if (string.Equals(kvp.Value, cameraId, StringComparison.OrdinalIgnoreCase))
                {
                    _ = StartSlotSessionAsync(kvp.Key, kvp.Value);
                }
            }
        }

        public void RefreshSwitchesPanel()
        {
            // Switches panel is no longer hosted inside CCTV Wall (handled via main sidebar)
        }

        private async Task StartSlotSessionAsync(int slotIndex, string cameraId)
        {
            if (_real == null || string.IsNullOrWhiteSpace(cameraId)) return;

            StopSlotSession(slotIndex);

            if (!_slotControls.TryGetValue(slotIndex, out var ctrl)) return;

            ctrl.IsConnecting = true;
            ctrl.StatusText.Text = $"{cameraId} BaÄŸlanÄ±yorâ€¦";
            ctrl.StatusText.Visibility = Visibility.Visible;
            ctrl.StatusDot.Fill = new SolidColorBrush(Color.FromRgb(245, 158, 11)); // Yellow
            ctrl.FrameCount = 0;

            try
            {
                var session = await _real.CreateCameraSessionAsync(cameraId);
                session.TargetFps = _targetFps;
                session.IsThermalMode = _isThermalEnabled;
                session.IsTurboFpvMode = _isTurboFpvEnabled;

                _activeSessions[slotIndex] = session;

                // Update slot capabilities (PTZ / Auto Turret controls & crosshair)
                Dispatcher.Invoke(() =>
                {
                    if (!_slotControls.TryGetValue(slotIndex, out var c)) return;
                    c.BtnZoom.Visibility = session.IsPtzCamera ? Visibility.Visible : Visibility.Collapsed;
                    c.BtnFire.Visibility = session.IsAutoTurret ? Visibility.Visible : Visibility.Collapsed;
                    c.HudZoomBtn.Visibility = session.IsPtzCamera ? Visibility.Visible : Visibility.Collapsed;
                    c.HudFireBtn.Visibility = session.IsAutoTurret ? Visibility.Visible : Visibility.Collapsed;
                    c.HudReloadBtn.Visibility = (session.IsAutoTurret || session.ControlFlags.HasFlag(CameraControlFlags.Reload)) ? Visibility.Visible : Visibility.Collapsed;
                    c.Crosshair.Visibility = (session.IsAutoTurret || session.ControlFlags.HasFlag(CameraControlFlags.Crosshair))
                        ? Visibility.Visible : Visibility.Collapsed;
                    c.ControlsHud.Visibility = Visibility.Visible;
                });

                session.FrameBitmapRendered += bmp =>
                {
                    Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, () =>
                    {
                        if (!_slotControls.TryGetValue(slotIndex, out var c)) return;
                        c.Img.Source = bmp;
                        c.StatusText.Visibility = Visibility.Collapsed;
                        c.StatusDot.Fill = new SolidColorBrush(Color.FromRgb(16, 185, 129)); // Green Live

                        c.FrameCount++;
                        var now = DateTime.UtcNow;
                        if ((now - c.LastFrameTime).TotalSeconds >= 1)
                        {
                            c.FpsBadge.Text = $"{c.FrameCount} FPS";
                            c.FrameCount = 0;
                            c.LastFrameTime = now;
                        }
                    });
                };

                session.KeepAliveFailed += err =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (!_slotControls.TryGetValue(slotIndex, out var c)) return;
                        c.StatusDot.Fill = new SolidColorBrush(Color.FromRgb(239, 68, 68)); // Red
                        c.StatusText.Text = $"Koptu: {err}";
                        c.StatusText.Visibility = Visibility.Visible;
                    });
                };
            }
            catch (Exception ex)
            {
                ctrl.IsConnecting = false;
                ctrl.StatusDot.Fill = new SolidColorBrush(Color.FromRgb(239, 68, 68)); // Red
                ctrl.StatusText.Text = $"Hata: {ex.Message}";
                ctrl.StatusText.Visibility = Visibility.Visible;
            }
        }

        private void StopSlotSession(int slotIndex)
        {
            if (_activeSessions.Remove(slotIndex, out var session))
            {
                _ = session.DisposeAsync();
            }

            if (_slotControls.TryGetValue(slotIndex, out var ctrl))
            {
                ctrl.Img.Source = null;
                ctrl.FpsBadge.Text = "0 FPS";
                ctrl.StatusDot.Fill = new SolidColorBrush(Color.FromRgb(107, 114, 128)); // Grey
                ctrl.StatusText.Text = "Kamera SeÃ§in";
                ctrl.StatusText.Visibility = Visibility.Visible;
                ctrl.BtnZoom.Visibility = Visibility.Collapsed;
                ctrl.BtnFire.Visibility = Visibility.Collapsed;
                ctrl.Crosshair.Visibility = Visibility.Collapsed;
                ctrl.ControlsHud.Visibility = Visibility.Collapsed;
            }
        }

        private async Task StopAllSessionsAsync()
        {
            var sessions = _activeSessions.Values.ToList();
            _activeSessions.Clear();
            foreach (var s in sessions)
            {
                try { await s.DisposeAsync().ConfigureAwait(false); } catch { }
            }
        }

        // =========================================================================
        // PERSISTENCE
        // =========================================================================

        private string GetConfigPath()
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AlpRust+");
            Directory.CreateDirectory(dir);
            var serverKey = _vm?.Selected != null ? $"{_vm.Selected.Host}:{_vm.Selected.Port}" : "default";
            var clean = string.Join("_", serverKey.Split(Path.GetInvalidFileNameChars()));
            return Path.Combine(dir, $"cctv_wall_{clean}.json");
        }

        private void SaveSlotAssignments()
        {
            try
            {
                var model = new WallConfigModel
                {
                    SlotCount = _slotCount,
                    Assignments = _slotCameraAssignments
                };
                var json = JsonSerializer.Serialize(model, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(GetConfigPath(), json);
            }
            catch { }
        }

        private void LoadSlotAssignments()
        {
            try
            {
                var path = GetConfigPath();
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    var model = JsonSerializer.Deserialize<WallConfigModel>(json);
                    if (model != null)
                    {
                        if (model.SlotCount > 0) _slotCount = model.SlotCount;
                        _slotCameraAssignments.Clear();
                        if (model.Assignments != null)
                        {
                            foreach (var (k, v) in model.Assignments)
                                _slotCameraAssignments[k] = v;
                        }
                    }
                }
            }
            catch { }
        }

        private sealed class WallConfigModel
        {
            public int SlotCount { get; set; } = 9;
            public Dictionary<int, string> Assignments { get; set; } = new();
        }
    }
}
