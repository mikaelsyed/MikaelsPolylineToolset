using ArcGIS.Core.Data;
using ArcGIS.Desktop.Catalog;
using ArcGIS.Desktop.Core.Geoprocessing;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using MikaelsPolylineToolset.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace MikaelsPolylineToolset.UnconnectedPolylineEnds
{
    public partial class UnconnectedPolylineEndsWindow : ArcGIS.Desktop.Framework.Controls.ProWindow
    {
        private readonly System.Windows.Forms.TextBox _logTextBox;

        public UnconnectedPolylineEndsWindow()
        {
            InitializeComponent();

            _logTextBox = QcUiHelpers.CreateLogTextBox();
            LogHost.Child = _logTextBox;
            QcUiHelpers.ConfigureDefaultGdbToggle(UseDefaultGdbCheck, OutputPathTextBox, BrowseButton, "UnconnectedEndsOutput");

            Loaded += UnconnectedPolylineEndsWindow_Loaded;
        }

        private void SetLog(IEnumerable<string> lines) => _logTextBox.Text = string.Join(Environment.NewLine, lines);

        private async void UnconnectedPolylineEndsWindow_Loaded(object sender, RoutedEventArgs e)
        {
            var lineLayers = await QcUiHelpers.GetPolylineLayerItemsAsync();
            LineLayerCombo.ItemsSource = lineLayers;
            if (lineLayers.Count > 0) LineLayerCombo.SelectedIndex = 0;

            var polyLayers = await QcUiHelpers.GetPolygonLayerItemsAsync();
            PolygonLayerCombo.ItemsSource = polyLayers;
            if (polyLayers.Count > 0) PolygonLayerCombo.SelectedIndex = 0;

            if (lineLayers.Count == 0 || polyLayers.Count == 0)
                SetLog(new[] { "Need at least one polyline layer and one polygon layer in the active map." });
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
            var polygonLayer = (PolygonLayerCombo.SelectedItem as QcUiHelpers.LayerItem)?.Layer;
            if (lineLayer == null || polygonLayer == null)
            {
                MessageBox.Show("Please select an input line layer and an input polygon layer.", "Unconnected Polyline Ends", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            double tolerance = 0;
            var toleranceText = ToleranceTextBox.Text?.Trim();
            if (!string.IsNullOrEmpty(toleranceText) && !double.TryParse(toleranceText, out tolerance))
            {
                MessageBox.Show("Match tolerance must be a number.", "Unconnected Polyline Ends", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string outputPath = string.IsNullOrWhiteSpace(OutputPathTextBox.Text) ? null : OutputPathTextBox.Text.Trim();

            RunButton.IsEnabled = false;
            CloseButton.IsEnabled = false;
            SetLog(new[] { "Running..." });

            try
            {
                var (count, messages) = await QueuedTask.Run(() =>
                {
                    var log = new List<string>();
                    var oids = UnconnectedEndsFinder.FindUnconnectedEndOids(lineLayer, polygonLayer, tolerance, msg => log.Add(msg));

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
                        messages.Add("No unconnected polyline ends found; output not created.");
                    }
                }

                SetLog(messages);
            }
            catch (Exception ex)
            {
                SetLog(new[] { "Error: " + ex.Message });
            }
            finally
            {
                RunButton.IsEnabled = true;
                CloseButton.IsEnabled = true;
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}
