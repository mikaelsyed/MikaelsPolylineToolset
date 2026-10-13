using ArcGIS.Core.Data;
using ArcGIS.Desktop.Catalog;
using ArcGIS.Desktop.Core.Geoprocessing;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using MikaelsPolylineToolset.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;

namespace MikaelsPolylineToolset.VertexCountCheck
{
    public partial class VertexCountCheckWindow : ArcGIS.Desktop.Framework.Controls.ProWindow
    {
        private readonly System.Windows.Forms.TextBox _logTextBox;
        private CancellationTokenSource _cts;

        public VertexCountCheckWindow()
        {
            InitializeComponent();

            _logTextBox = QcUiHelpers.CreateLogTextBox();
            LogHost.Child = _logTextBox;
            QcUiHelpers.ConfigureDefaultGdbToggle(UseDefaultGdbCheck, OutputPathTextBox, BrowseButton, "VertexCountOutput");

            Loaded += VertexCountCheckWindow_Loaded;
        }

        private void SetLog(IEnumerable<string> lines) => _logTextBox.Text = string.Join(Environment.NewLine, lines);

        private async void VertexCountCheckWindow_Loaded(object sender, RoutedEventArgs e)
        {
            var lineLayers = await QcUiHelpers.GetPolylineLayerItemsAsync();
            LineLayerCombo.ItemsSource = lineLayers;
            if (lineLayers.Count > 0) LineLayerCombo.SelectedIndex = 0;
            else SetLog(new[] { "No polyline layers found in the active map." });
        }

        private async void LineLayerCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            var lineLayer = (LineLayerCombo.SelectedItem as QcUiHelpers.LayerItem)?.Layer;
            var fields = await QcUiHelpers.GetBroadAttributeFieldItemsAsync(lineLayer);
            FieldCombo.ItemsSource = fields;
            if (fields.Count > 0) FieldCombo.SelectedIndex = 0;
            else FieldValueCombo.ItemsSource = null;
        }

        private async void FieldCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            var lineLayer = (LineLayerCombo.SelectedItem as QcUiHelpers.LayerItem)?.Layer;
            var field = (FieldCombo.SelectedItem as QcUiHelpers.FieldItem)?.Name;
            if (lineLayer == null || field == null)
            {
                FieldValueCombo.ItemsSource = null;
                return;
            }
            var values = await QcUiHelpers.GetDistinctFieldValuesAsync(lineLayer, field);
            FieldValueCombo.ItemsSource = values;
            if (values.Count > 0) FieldValueCombo.SelectedIndex = 0;
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var saveDialog = new SaveItemDialog { Title = "Output Feature Class", OverwritePrompt = true };
            if (saveDialog.ShowDialog() == true)
                OutputPathTextBox.Text = saveDialog.FilePath;
        }

        private async void RunButton_Click(object sender, RoutedEventArgs e)
        {
            var lineLayer = (LineLayerCombo.SelectedItem as QcUiHelpers.LayerItem)?.Layer;
            var field = (FieldCombo.SelectedItem as QcUiHelpers.FieldItem)?.Name;
            string fieldValue = (FieldValueCombo.SelectedItem as string) ?? FieldValueCombo.Text?.Trim();

            if (lineLayer == null || string.IsNullOrEmpty(field) || string.IsNullOrEmpty(fieldValue))
            {
                MessageBox.Show("Please select the input line layer, attribute field, and field value.", "Vertex Count Check", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!int.TryParse(ExpectedTextBox.Text?.Trim(), out int expected) || expected < 0)
            {
                MessageBox.Show("Expected vertex count must be a whole number.", "Vertex Count Check", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string outputPath = string.IsNullOrWhiteSpace(OutputPathTextBox.Text) ? null : OutputPathTextBox.Text.Trim();

            RunButton.IsEnabled = false;
            CloseButton.IsEnabled = false;
            SetLog(new[] { "Running..." });
            var cts = new CancellationTokenSource();
            _cts = cts;
            CancelButton.Content = "Cancel";
            CancelButton.IsEnabled = true;
            CancelButton.Visibility = Visibility.Visible;
            QcRunProgress run = null;

            try
            {
                long records = await QcRunUi.CountAsync(lineLayer);
                run = QcRunProgress.ForSingle("Vertex Count Check", lineLayer.Name, records, cts.Token);
                ProgressPanel.Start(run);
                run.BeginUnit("Vertex Count Check", lineLayer.Name);
                var progress = run.Current;

                var (count, messages) = await QueuedTask.Run(() =>
                {
                    var log = new List<string>();
                    var oids = VertexCountFinder.FindVertexCountFlags(lineLayer, field, fieldValue, expected, msg => log.Add(msg), progress);

                    if (oids.Count > 0)
                        lineLayer.Select(new QueryFilter { ObjectIDs = oids }, SelectionCombinationMethod.New);
                    else
                        lineLayer.ClearSelection();

                    return (oids.Count, log);
                });

                if (!string.IsNullOrEmpty(outputPath))
                {
                    if (count > 0)
                    {
                        var gpParams = Geoprocessing.MakeValueArray(lineLayer, outputPath);
                        var gpResult = await Geoprocessing.ExecuteToolAsync("management.CopyFeatures", gpParams,
                            null, null, null, GPExecuteToolFlags.None);
                        if (gpResult.IsFailed)
                            messages.Add("CopyFeatures failed: " + string.Join("; ", gpResult.ErrorMessages.Select(m => m.Text)));
                        else
                            messages.Add($"Copied flagged lines to {outputPath}");
                    }
                    else
                    {
                        messages.Add("No flagged lines found; output not created.");
                    }
                }

                SetLog(messages);
            }
            catch (OperationCanceledException)
            {
                SetLog(new[] { "Cancelled." });
            }
            catch (Exception ex)
            {
                SetLog(new[] { "Error: " + ex.Message });
            }
            finally
            {
                run?.EndUnit();
                ProgressPanel.Stop(cts.IsCancellationRequested ? "Cancelled" : "Finished");
                CancelButton.Visibility = Visibility.Collapsed;
                _cts = null;
                cts.Dispose();
                RunButton.IsEnabled = true;
                CloseButton.IsEnabled = true;
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            CancelButton.IsEnabled = false;
            CancelButton.Content = "Cancelling...";
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}
