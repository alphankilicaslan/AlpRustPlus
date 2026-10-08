using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using RustPlusDesk.Services;
using Wpf.Ui.Controls;

namespace RustPlusDesk.Views.Windows;

public partial class PotentialTeammatesWindow : FluentWindow
{
    private readonly TrackedPlayer _targetPlayer;
    private List<PotentialTeammate> _allTeammates = new();

    public PotentialTeammatesWindow(TrackedPlayer player)
    {
        InitializeComponent();
        _targetPlayer = player;

        TxtTargetName.Text = player.Name;
        TxtTargetSteamId.Text = $"Steam ID: {player.BMId}";
        TxtTargetGroup.Text = string.IsNullOrWhiteSpace(player.GroupName) ? "Grup Yok" : player.GroupName;

        Loaded += async (s, e) => await LoadTeammatesAsync();
    }

    private async Task LoadTeammatesAsync()
    {
        try
        {
            PanelLoading.Visibility = Visibility.Visible;
            PanelPrivateNotice.Visibility = Visibility.Collapsed;
            ListTeammates.Visibility = Visibility.Collapsed;
            BtnAddAllRust.IsEnabled = false;
            BtnAddAll.IsEnabled = false;

            _allTeammates = await TrackingService.FetchPotentialTeammatesAsync(_targetPlayer.BMId);

            int rustCount = _allTeammates.Count(t => t.IsInRust);
            int onlineCount = _allTeammates.Count(t => t.IsOnline && !t.IsInRust);
            int trackedCount = _allTeammates.Count(t => t.IsAlreadyTracked);

            TxtSummaryStats.Text = $"Toplam: {_allTeammates.Count} Arkadaş | 🟢 {rustCount} Rust'ta | 🔵 {onlineCount} Steam Çevrimiçi | 📌 {trackedCount} Takipte";

            if (_allTeammates.Count == 0)
            {
                PanelPrivateNotice.Visibility = Visibility.Visible;
                ListTeammates.Visibility = Visibility.Collapsed;
            }
            else
            {
                PanelPrivateNotice.Visibility = Visibility.Collapsed;
                ListTeammates.Visibility = Visibility.Visible;
                BtnAddAllRust.IsEnabled = rustCount > 0;
                BtnAddAll.IsEnabled = true;
                ApplyFilter();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TEAMMATES-WIN] Error: {ex.Message}");
            PanelPrivateNotice.Visibility = Visibility.Visible;
        }
        finally
        {
            PanelLoading.Visibility = Visibility.Collapsed;
        }
    }

    private void ApplyFilter()
    {
        string filter = TxtFilter.Text?.Trim().ToLowerInvariant() ?? "";
        if (string.IsNullOrEmpty(filter))
        {
            ListTeammates.ItemsSource = _allTeammates;
        }
        else
        {
            ListTeammates.ItemsSource = _allTeammates.Where(t => 
                t.Name.ToLowerInvariant().Contains(filter) || 
                t.SteamId.Contains(filter) ||
                t.CurrentGame.ToLowerInvariant().Contains(filter)).ToList();
        }
    }

    private void TxtFilter_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFilter();
    }

    private void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        _ = LoadTeammatesAsync();
    }

    private void BtnAddSingle_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is PotentialTeammate tm)
        {
            AddTeammateToTracking(tm);
            if (sender is Wpf.Ui.Controls.Button btn)
            {
                btn.Content = "✓ Eklendi";
                btn.IsEnabled = false;
                btn.Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary;
            }
        }
    }

    private void BtnAddAllRust_Click(object sender, RoutedEventArgs e)
    {
        var rustTeammates = _allTeammates.Where(t => t.IsInRust && !t.IsAlreadyTracked).ToList();
        foreach (var tm in rustTeammates)
        {
            AddTeammateToTracking(tm);
        }
        ApplyFilter();
    }

    private void BtnAddAll_Click(object sender, RoutedEventArgs e)
    {
        var untracked = _allTeammates.Where(t => !t.IsAlreadyTracked).ToList();
        foreach (var tm in untracked)
        {
            AddTeammateToTracking(tm);
        }
        ApplyFilter();
    }

    private void AddTeammateToTracking(PotentialTeammate tm)
    {
        string serverName = _targetPlayer.LastServerName;
        if (string.IsNullOrEmpty(serverName)) serverName = TrackingService.LastServer.name;

        TrackingService.TrackPlayer(tm.SteamId, tm.Name, serverName);

        if (!string.IsNullOrWhiteSpace(_targetPlayer.GroupName))
        {
            TrackingService.SetPlayerGroup(tm.SteamId, _targetPlayer.GroupName, _targetPlayer.GroupColor);
            tm.ExistingGroupName = _targetPlayer.GroupName;
        }

        tm.IsAlreadyTracked = true;
    }
}
