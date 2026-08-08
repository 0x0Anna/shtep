using System.Globalization;
using System.Windows.Controls;
using TelemetryExportPlugin.Config;

namespace TelemetryExportPlugin
{
    public partial class Control : UserControl
    {
        private readonly Plugin _plugin;
        private bool _loading;

        public Control(Plugin plugin)
        {
            InitializeComponent();
            _plugin = plugin;

            _loading = true;
            TempDirBox.Text = _plugin.Settings.TempDir;
            OutputDirBox.Text = _plugin.Settings.OutputDir;
            SampleRateBox.Text = _plugin.Settings.SampleRateHz.ToString(CultureInfo.InvariantCulture);
            PurgeCheckBox.IsChecked = _plugin.Settings.PurgeIncompleteOnStartup;

            RecordingTriggerCombo.ItemsSource = System.Enum.GetValues(typeof(RecordingTriggerMode));
            RecordingTriggerCombo.SelectedItem = _plugin.Settings.RecordingTrigger;

            PitDebounceBox.Text = _plugin.Settings.PitLaneDebounceMs.ToString(CultureInfo.InvariantCulture);
            DisconnectGraceBox.Text = _plugin.Settings.DisconnectGraceMs.ToString(CultureInfo.InvariantCulture);
            HeuristicSpeedBox.Text = _plugin.Settings.HeuristicDiscontinuitySpeedKmh.ToString(CultureInfo.InvariantCulture);

            DiscontinuityModeCombo.ItemsSource = System.Enum.GetValues(typeof(DiscontinuityDetectionMode));
            DiscontinuityModeCombo.SelectedItem = _plugin.Settings.DiscontinuityDetection;

            RewindHandlingCombo.ItemsSource = System.Enum.GetValues(typeof(RewindHandlingMode));
            RewindHandlingCombo.SelectedItem = _plugin.Settings.RewindHandling;

            ExportMotecLdCheckBox.IsChecked = _plugin.Settings.ExportMotecLd;
            MotecOutputDirBox.Text = _plugin.Settings.MotecOutputDir;

            ExportIbtCheckBox.IsChecked = _plugin.Settings.ExportIbt;
            IbtOutputDirBox.Text = _plugin.Settings.IbtOutputDir;
            IbtTickRateBox.Text = _plugin.Settings.IbtTickRateHz.ToString(CultureInfo.InvariantCulture);

            VerboseDiagnosticLoggingCheckBox.IsChecked = _plugin.Settings.VerboseDiagnosticLogging;
            _loading = false;
        }

        // Validated at save time (each field's TextChanged), per SCHEMA.md
        // "Startup & config validation" - not deferred to first recording attempt.
        private void TempDirBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (_loading) return;
            _plugin.Settings.TempDir = TempDirBox.Text;
            ShowPathWarning(PathValidation.ValidateWritableDirectory(_plugin.Settings.TempDir, "TempDir"));
        }

        private void OutputDirBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (_loading) return;
            _plugin.Settings.OutputDir = OutputDirBox.Text;
            ShowPathWarning(PathValidation.ValidateWritableDirectory(_plugin.Settings.OutputDir, "OutputDir"));
        }

        private void ShowPathWarning(PathValidationResult result)
        {
            PathWarningText.Text = result.Success ? string.Empty : result.Message;
        }

        private void SampleRateBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (_loading) return;
            if (int.TryParse(SampleRateBox.Text, out var hz) && hz > 0)
            {
                _plugin.Settings.SampleRateHz = hz;
            }
        }

        private void PurgeCheckBox_Changed(object sender, System.Windows.RoutedEventArgs e)
        {
            if (_loading) return;
            _plugin.Settings.PurgeIncompleteOnStartup = PurgeCheckBox.IsChecked == true;
        }

        private void RecordingTriggerCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_loading) return;
            if (RecordingTriggerCombo.SelectedItem is RecordingTriggerMode mode)
            {
                _plugin.Settings.RecordingTrigger = mode;
            }
        }

        private void PitDebounceBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (_loading) return;
            if (int.TryParse(PitDebounceBox.Text, out var ms) && ms >= 0)
            {
                _plugin.Settings.PitLaneDebounceMs = ms;
            }
        }

        private void DisconnectGraceBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (_loading) return;
            if (int.TryParse(DisconnectGraceBox.Text, out var ms) && ms >= 0)
            {
                _plugin.Settings.DisconnectGraceMs = ms;
            }
        }

        private void HeuristicSpeedBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (_loading) return;
            if (int.TryParse(HeuristicSpeedBox.Text, out var kmh) && kmh > 0)
            {
                _plugin.Settings.HeuristicDiscontinuitySpeedKmh = kmh;
            }
        }

        private void DiscontinuityModeCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_loading) return;
            if (DiscontinuityModeCombo.SelectedItem is DiscontinuityDetectionMode mode)
            {
                _plugin.Settings.DiscontinuityDetection = mode;
            }
        }

        private void RewindHandlingCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_loading) return;
            if (RewindHandlingCombo.SelectedItem is RewindHandlingMode mode)
            {
                _plugin.Settings.RewindHandling = mode;
            }
        }

        private void ExportMotecLdCheckBox_Changed(object sender, System.Windows.RoutedEventArgs e)
        {
            if (_loading) return;
            _plugin.Settings.ExportMotecLd = ExportMotecLdCheckBox.IsChecked == true;
        }

        private void MotecOutputDirBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (_loading) return;
            _plugin.Settings.MotecOutputDir = MotecOutputDirBox.Text;
        }

        private void ExportIbtCheckBox_Changed(object sender, System.Windows.RoutedEventArgs e)
        {
            if (_loading) return;
            _plugin.Settings.ExportIbt = ExportIbtCheckBox.IsChecked == true;
        }

        private void IbtOutputDirBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (_loading) return;
            _plugin.Settings.IbtOutputDir = IbtOutputDirBox.Text;
        }

        private void IbtTickRateBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (_loading) return;
            if (int.TryParse(IbtTickRateBox.Text, out var hz) && hz > 0)
            {
                _plugin.Settings.IbtTickRateHz = hz;
            }
        }

        private void VerboseDiagnosticLoggingCheckBox_Changed(object sender, System.Windows.RoutedEventArgs e)
        {
            if (_loading) return;
            _plugin.Settings.VerboseDiagnosticLogging = VerboseDiagnosticLoggingCheckBox.IsChecked == true;
        }
    }
}
