using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Uniwill_Control_Utility.Properties;
using Uniwill_Control_Utility.Services;
using Uniwill_Control_Utility.Views.Pages;
using UCU.Core.Supervision;

namespace Uniwill_Control_Utility.Views.Windows
{
    /// <summary>
    /// Add/edit dialog for one watcher rule (story U-201). The control sets
    /// map onto the flat <see cref="WatcherRule"/> record; every label and
    /// option string is composed through LocalizationService.Get, so the
    /// dialog is culture-clean while the persisted rule stays culture-neutral
    /// (BuildRule writes enums and raw values, never localized text).
    ///
    /// ComboBoxes are index-parallel with their enum/option lists: the
    /// displayed item is the localized string, the meaning is SelectedIndex.
    /// Save runs the rule through WatcherPolicy.Validate before accepting,
    /// so the store's contract is enforced at the editor too.
    /// </summary>
    public partial class WatcherRuleDialog : Wpf.Ui.Controls.FluentWindow
    {
        /// <summary>The edited rule, once saved.</summary>
        public WatcherRule? Rule { get; private set; }

        /// <summary>Identity of the rule being edited (the list slot key);
        /// null when adding.</summary>
        public string? EditedId { get; }

        private readonly string? _originalName;

        public WatcherRuleDialog(WatcherRule? rule, IReadOnlyList<string> presetDisplayNames)
        {
            InitializeComponent();

            List<string> conditionItems = WatcherVocabulary.ConditionNames.ToList();
            List<string> powerStateItems = WatcherVocabulary.PowerStateNames.ToList();
            List<string> edgeItems = WatcherVocabulary.EdgeNames.ToList();
            List<string> powerEventItems = WatcherVocabulary.PowerEventNames.ToList();
            List<string> valueSourceItems = WatcherVocabulary.ValueSourceNames.ToList();
            List<string> turboItems = WatcherVocabulary.TurboNames.ToList();
            List<string> actionItems = WatcherVocabulary.ActionNames.ToList();

            TitleBar.Title = LocalizationService.Get("Watcher Rule");
            lblName.Text = LocalizationService.Get("Name");
            lblCooldown.Text = LocalizationService.Get("Cooldown (seconds)");
            lblCooldownHint.Text = LocalizationService.Get("Minimum seconds between triggers, to keep the rule from re-firing every second");
            lblCondition.Text = LocalizationService.Get("When");
            lblCpuTempAbove.Text = LocalizationService.Get("CPU temp above (°C)");
            lblCpuTempBelow.Text = LocalizationService.Get("CPU temp below (°C)");
            lblGpuTempAbove.Text = LocalizationService.Get("GPU temp above (°C)");
            lblGpuTempBelow.Text = LocalizationService.Get("GPU temp below (°C)");
            lblCpuUsageAbove.Text = LocalizationService.Get("CPU usage above (%)");
            lblGpuUsageAbove.Text = LocalizationService.Get("GPU usage above (%)");
            lblFpsAbove.Text = LocalizationService.Get("FPS above");
            lblPowerState.Text = LocalizationService.Get("Power state");
            lblForegroundContains.Text = LocalizationService.Get("Foreground app contains");
            lblTelemetryHint.Text = LocalizationService.Get("Leave a threshold empty to ignore it. The rule fires only while every filled gate matches.");
            lblTargetApps.Text = LocalizationService.Get("App names (comma separated)");
            lblTargetAppsHint.Text = LocalizationService.Get("Executable names without paths, e.g. Cyberpunk2077, epic");
            lblEdge.Text = LocalizationService.Get("Trigger");
            lblPowerEvent.Text = LocalizationService.Get("Event");
            lblWatchedValue.Text = LocalizationService.Get("Watched value");
            lblWatchedValueHint.Text = LocalizationService.Get("The value is polled every 5 seconds; the rule fires when it changes.");
            lblAction.Text = LocalizationService.Get("Then");
            lblPreset.Text = LocalizationService.Get("Preset");
            lblModeNumber.Text = LocalizationService.Get("Q-mode number (0-3)");
            lblTurboState.Text = LocalizationService.Get("Fan turbo");
            lblNotifyTitle.Text = LocalizationService.Get("Notification title");
            lblNotifyMessage.Text = LocalizationService.Get("Notification message");
            btnCancel.Content = LocalizationService.Get("Cancel");
            btnSave.Content = LocalizationService.Get("Save");

            cbxCondition.ItemsSource = conditionItems;
            cbxPowerState.ItemsSource = powerStateItems;
            cbxEdge.ItemsSource = edgeItems;
            cbxPowerEvent.ItemsSource = powerEventItems;
            cbxWatchedValue.ItemsSource = valueSourceItems;
            cbxTurboState.ItemsSource = turboItems;
            cbxAction.ItemsSource = actionItems;
            cbxPreset.ItemsSource = presetDisplayNames;

            if (rule is null)
            {
                cbxCondition.SelectedIndex = 0;
                cbxPowerState.SelectedIndex = 0;
                cbxEdge.SelectedIndex = 0;
                cbxPowerEvent.SelectedIndex = 0;
                cbxWatchedValue.SelectedIndex = 0;
                cbxTurboState.SelectedIndex = 0;
                cbxAction.SelectedIndex = 0;
                cbxPreset.SelectedIndex = 0;
                nbCooldown.Value = 5;
            }
            else
            {
                EditedId = rule.Id;
                _originalName = rule.Name;

                txtName.Text = rule.Name;
                nbCooldown.Value = rule.CooldownSeconds;
                cbxCondition.SelectedIndex = (int)rule.Kind;
                cbxAction.SelectedIndex = (int)rule.Action;

                nbCpuTempAbove.Value = rule.CpuTempGt;
                nbCpuTempBelow.Value = rule.CpuTempLt;
                nbGpuTempAbove.Value = rule.GpuTempGt;
                nbGpuTempBelow.Value = rule.GpuTempLt;
                nbCpuUsageAbove.Value = rule.CpuUsageGt;
                nbGpuUsageAbove.Value = rule.GpuUsageGt;
                nbFpsAbove.Value = rule.FpsGt;
                cbxPowerState.SelectedIndex = rule.OnAc is null ? 0 : (rule.OnAc.Value ? 1 : 2);
                txtForegroundContains.Text = rule.ForegroundExe ?? string.Empty;

                txtTargetApps.Text = string.Join(", ", rule.ForegroundTargets);
                cbxEdge.SelectedIndex = (int)rule.EdgeKind;

                cbxPowerEvent.SelectedIndex = rule.PowerEvent is null ? 0 : (int)rule.PowerEvent.Value;

                cbxWatchedValue.SelectedIndex = rule.ValueSource is null ? 0 : (int)rule.ValueSource.Value;

                cbxPreset.SelectedItem = PresetProfileApplier.DisplayName(rule.ProfileName ?? string.Empty);
                if (cbxPreset.SelectedIndex < 0)
                    cbxPreset.SelectedIndex = 0;
                nbMode.Value = rule.PerformanceMode ?? 0;
                cbxTurboState.SelectedIndex = rule.FanTurboOn is true ? 0 : 1;
                txtNotifyTitle.Text = rule.NotifyTitle ?? string.Empty;
                txtNotifyMessage.Text = rule.NotifyMessage ?? string.Empty;
            }

            UpdatePanels();
        }

        private void cbxCondition_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdatePanels();

        private void cbxAction_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdatePanels();

        private void UpdatePanels()
        {
            // SelectionChanged fires while ItemsSource populates: guard every
            // index before using it.
            if (cbxCondition.SelectedIndex < 0 || cbxAction.SelectedIndex < 0)
                return;

            panelTelemetry.Visibility = PanelKind(cbxCondition.SelectedIndex, 0);
            panelForeground.Visibility = PanelKind(cbxCondition.SelectedIndex, 1);
            panelPower.Visibility = PanelKind(cbxCondition.SelectedIndex, 2);
            panelValue.Visibility = PanelKind(cbxCondition.SelectedIndex, 3);

            panelPreset.Visibility = PanelKind(cbxAction.SelectedIndex, 0);
            panelMode.Visibility = PanelKind(cbxAction.SelectedIndex, 1);
            panelTurbo.Visibility = PanelKind(cbxAction.SelectedIndex, 2);
            panelNotify.Visibility = PanelKind(cbxAction.SelectedIndex, 3);
        }

        private static Visibility PanelKind(int selected, int expected) =>
            selected == expected ? Visibility.Visible : Visibility.Collapsed;

        private void btnCancel_Click(object sender, RoutedEventArgs e) => Close();

        private void btnSave_Click(object sender, RoutedEventArgs e)
        {
            WatcherRule? rule = BuildRule();
            if (rule is null)
                return;

            string? why = WatcherPolicy.Validate(rule);
            if (why is not null)
            {
                MessageBox.Show(LocalizationService.Get("Invalid rule: ") + LocalizationService.Get(why),
                    LocalizationService.Get("Watcher Rule"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Rule = rule;
            DialogResult = true;
            Close();
        }

        /// <summary>Control set to rule record (culture-neutral). Null while
        /// the combo indexes are not ready.</summary>
        private WatcherRule? BuildRule()
        {
            if (cbxCondition.SelectedIndex < 0 || cbxAction.SelectedIndex < 0)
                return null;

            // Keep the identity of the rule being edited: the cooldown ledger
            // and the value-change seeds key off the rule id.
            WatcherRule rule = EditedId is null
                ? new WatcherRule()
                : new WatcherRule { Id = EditedId };

            string ruleName = (txtName.Text ?? string.Empty).Trim();

            rule = rule with
            {
                Name = ruleName.Length > 0
                    ? ruleName
                    : _originalName ?? LocalizationService.Get("Watcher rule"),
                CooldownSeconds = (int)(nbCooldown.Value ?? 5),
                Kind = (WatcherConditionKind)cbxCondition.SelectedIndex,
                Action = (WatcherActionKind)cbxAction.SelectedIndex,
            };

            // Condition payload
            rule = rule with
            {
                CpuTempGt = Threshold(nbCpuTempAbove),
                CpuTempLt = Threshold(nbCpuTempBelow),
                GpuTempGt = Threshold(nbGpuTempAbove),
                GpuTempLt = Threshold(nbGpuTempBelow),
                CpuUsageGt = Threshold(nbCpuUsageAbove),
                GpuUsageGt = Threshold(nbGpuUsageAbove),
                FpsGt = Threshold(nbFpsAbove),
                OnAc = cbxPowerState.SelectedIndex == 0 ? null
                     : cbxPowerState.SelectedIndex == 1 ? true : false,
                ForegroundExe = NullIfEmpty(txtForegroundContains.Text),
                ForegroundTargets = (txtTargetApps.Text ?? string.Empty)
                    .Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(t => t.Trim())
                    .Where(t => t.Length > 0)
                    .ToList(),
                EdgeKind = (WatcherEdgeKind)(cbxEdge.SelectedIndex < 0 ? 0 : cbxEdge.SelectedIndex),
                PowerEvent = (WatcherPowerEventKind)(cbxPowerEvent.SelectedIndex < 0 ? 0 : cbxPowerEvent.SelectedIndex),
                ValueSource = (WatcherValueSource)(cbxWatchedValue.SelectedIndex < 0 ? 0 : cbxWatchedValue.SelectedIndex),
            };

            // Action payload
            rule = rule with
            {
                ProfileName = cbxAction.SelectedIndex == 0
                    ? Pages.Automations.ResolveProfileName(cbxPreset.SelectedItem?.ToString())
                    : null,
                PerformanceMode = cbxAction.SelectedIndex == 1 ? (int)(nbMode.Value ?? 0) : (int?)null,
                FanTurboOn = cbxAction.SelectedIndex == 2 ? cbxTurboState.SelectedIndex == 0 : (bool?)null,
                NotifyTitle = cbxAction.SelectedIndex == 3 ? NullIfEmpty(txtNotifyTitle.Text) : null,
                NotifyMessage = cbxAction.SelectedIndex == 3 ? NullIfEmpty(txtNotifyMessage.Text) : null,
            };

            // Condition payload for gates of OTHER kinds must not leak: a
            // Telemetry rule carries no PowerEvent, and validation counts the
            // gates per kind, so clear the payload fields the kind does not use.
            rule = rule.Kind switch
            {
                WatcherConditionKind.Telemetry => rule with
                {
                    ForegroundTargets = Array.Empty<string>(),
                    PowerEvent = null,
                    ValueSource = null,
                },
                WatcherConditionKind.ForegroundProcess => rule with
                {
                    CpuTempGt = null, CpuTempLt = null, GpuTempGt = null, GpuTempLt = null,
                    CpuUsageGt = null, GpuUsageGt = null, FpsGt = null, OnAc = null, ForegroundExe = null,
                    PowerEvent = null,
                    ValueSource = null,
                },
                WatcherConditionKind.PowerEvent => rule with
                {
                    CpuTempGt = null, CpuTempLt = null, GpuTempGt = null, GpuTempLt = null,
                    CpuUsageGt = null, GpuUsageGt = null, FpsGt = null, OnAc = null, ForegroundExe = null,
                    ForegroundTargets = Array.Empty<string>(),
                    ValueSource = null,
                },
                _ => rule with
                {
                    CpuTempGt = null, CpuTempLt = null, GpuTempGt = null, GpuTempLt = null,
                    CpuUsageGt = null, GpuUsageGt = null, FpsGt = null, OnAc = null, ForegroundExe = null,
                    ForegroundTargets = Array.Empty<string>(),
                    PowerEvent = null,
                },
            };

            return rule;
        }

        private static int? Threshold(Wpf.Ui.Controls.NumberBox box) =>
            box.Value is double v ? (int)Math.Round(v) : (int?)null;

        private static string? NullIfEmpty(string? text) =>
            string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }
}
