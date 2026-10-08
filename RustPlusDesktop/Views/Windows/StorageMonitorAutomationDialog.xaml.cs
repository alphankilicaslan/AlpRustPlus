using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using RustPlusDesk.Models;
using WpfUi = Wpf.Ui.Controls;

namespace RustPlusDesk.Views.Windows
{
    public partial class StorageMonitorAutomationDialog : WpfUi.FluentWindow
    {
        private readonly SmartDevice _device;
        public LogicRule? CreatedRule { get; private set; }

        public StorageMonitorAutomationDialog(SmartDevice device, IEnumerable<SmartDevice> availableSwitches)
        {
            InitializeComponent();
            _device = device;
            TxtDeviceName.Text = $"{device.DisplayName} (#{device.EntityId})";

            var switchList = availableSwitches.Where(s => s.IsSwitch || s.Kind == "SmartSwitch" || s.Kind == "Smart Switch").ToList();
            CmbTargetSwitch.ItemsSource = switchList;
            if (switchList.Count > 0) CmbTargetSwitch.SelectedIndex = 0;
        }

        private void CmbItemPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TxtCustomItem == null) return;
            if (CmbItemPreset?.SelectedItem is ComboBoxItem item)
            {
                var tag = item.Tag?.ToString();
                TxtCustomItem.Visibility = tag == "CUSTOM" ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void BtnSearchAllItems_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new ChangeDeviceIconDialog(null, TxtCustomItem.Text, "Storage")
            {
                Owner = this
            };
            dlg.ShowDialog();
            if (dlg.IsSaved && !string.IsNullOrWhiteSpace(dlg.SelectedIconShortName))
            {
                TxtCustomItem.Text = dlg.SelectedIconShortName;
                TxtCustomItem.Visibility = Visibility.Visible;
                foreach (var it in CmbItemPreset.Items)
                {
                    if (it is ComboBoxItem c && c.Tag?.ToString() == "CUSTOM")
                    {
                        CmbItemPreset.SelectedItem = c;
                        break;
                    }
                }
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            Err.IsOpen = false;

            // Resolve Item
            string itemShortName = "*";
            string itemDisplay = "Herhangi bir eşya";
            if (CmbItemPreset.SelectedItem is ComboBoxItem cItem)
            {
                var tag = cItem.Tag?.ToString() ?? "*";
                if (tag == "CUSTOM")
                {
                    itemShortName = (TxtCustomItem.Text ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(itemShortName))
                    {
                        Err.Message = "Lütfen takip edilecek eşya adını girin.";
                        Err.IsOpen = true;
                        return;
                    }
                    itemDisplay = itemShortName;
                }
                else
                {
                    itemShortName = tag;
                    itemDisplay = cItem.Content?.ToString() ?? tag;
                }
            }

            // Resolve Condition
            string condition = "ItemAdded";
            string conditionDisplay = "Eklendiğinde";
            if (CmbCondition.SelectedItem is ComboBoxItem condItem)
            {
                condition = condItem.Tag?.ToString() ?? "ItemAdded";
                conditionDisplay = condItem.Content?.ToString() ?? condition;
            }

            // Resolve Threshold
            if (!int.TryParse(TxtThreshold.Text, out var threshold) || threshold < 1)
            {
                Err.Message = "Miktar eşiği en az 1 olmalıdır.";
                Err.IsOpen = true;
                return;
            }

            // Resolve Target Switch
            if (CmbTargetSwitch.SelectedItem is not SmartDevice targetSwitch)
            {
                Err.Message = "Lütfen tetiklenecek bir akıllı şalter (Smart Switch) seçin.";
                Err.IsOpen = true;
                return;
            }

            // Resolve Switch Action
            bool? toggleState = true;
            string actionText = "AÇ";
            if (CmbSwitchAction.SelectedItem is ComboBoxItem actItem)
            {
                var actTag = actItem.Tag?.ToString();
                if (actTag == "OFF") { toggleState = false; actionText = "KAPAT"; }
                else if (actTag == "TOGGLE") { toggleState = null; actionText = "DEĞİŞTİR"; }
            }

            var rule = new LogicRule
            {
                Name = $"{_device.PureName}: {itemShortName} -> {targetSwitch.PureName} ({actionText})",
                TriggerType = "StorageMonitor",
                TriggerEntityId = _device.EntityId,
                StorageItemShortName = itemShortName,
                StorageCondition = condition,
                StorageThreshold = threshold,
                IsEnabled = true
            };

            var step = new LogicStep
            {
                StepType = "Toggle",
                TargetEntityId = targetSwitch.EntityId,
                ToggleState = toggleState
            };
            rule.Steps.Add(step);

            CreatedRule = rule;
            DialogResult = true;
            Close();
        }
    }
}
