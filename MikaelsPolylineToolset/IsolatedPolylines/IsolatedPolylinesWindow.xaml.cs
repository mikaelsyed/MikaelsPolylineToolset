using ArcGIS.Core.CIM;
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

namespace MikaelsPolylineToolset.IsolatedPolylines
{
    public partial class IsolatedPolylinesWindow : ArcGIS.Desktop.Framework.Controls.ProWindow
    {
        private class LayerItem
        {
            public string Name { get; set; }
            public FeatureLayer Layer { get; set; }
            public override string ToString() => Name;
        }

        private readonly System.Windows.Forms.TextBox _logTextBox;

        public IsolatedPolylinesWindow()
        {
            InitializeComponent();

            _logTextBox = QcUiHelpers.CreateLogTextBox();
            LogHost.Child = _logTextBox;
            QcUiHelpers.ConfigureDefaultGdbToggle(UseDefaultGdbCheck, OutputPathTextBox, BrowseButton, "IsolatedLinesOutput");

            Loaded += IsolatedPolylinesWindow_Loaded;
        }

        private void SetLog(IEnumerable<string> lines)
        {
            _logTextBox.Text = string.Join(Environment.NewLine, lines);
        }

        private async void IsolatedPolylinesWindow_Loaded(object sender, RoutedEventArgs e)
        {
            var (lineLayers, polyLayers) = await QueuedTask.Run(() =>
            {
                var map = MapView.Active?.Map;
                var featureLayers = map?.GetLayersAsFlattenedList().OfType<FeatureLayer>().ToList()
                    ?? new List<FeatureLayer>();
                var lines = featureLayers
                    .Where(l => l.ShapeType == esriGeometryType.esriGeometryPolyline)
                    .Select(l => new LayerItem { Name = l.Name, Layer = l })
                    .ToList();
                var polys = featureLayers
                    .Where(l => l.ShapeType == esriGeometryType.esriGeometryPolygon)
                    .Select(l => new LayerItem { Name = l.Name, Layer = l })
                    .ToList();
                return (lines, polys);
            });

            LineLayerCombo.ItemsSource = lineLayers;
            if (lineLayers.Count > 0) LineLayerCombo.SelectedIndex = 0;

            var polyOptions = new List<LayerItem> { new LayerItem { Name = "<None>", Layer = null } };
            polyOptions.AddRange(polyLayers);
            PolygonLayerCombo.ItemsSource = polyOptions;
            PolygonLayerCombo.SelectedIndex = 0;

            if (lineLayers.Count == 0)
                SetLog(new[] { "No polyline layers found in the active map." });
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var saveDialog = new SaveItemDialog
            {
                Title = "Output Feature Class",
                OverwritePrompt = true
            };
            bool? ok = saveDialog.ShowDialog();
            if (ok == true)
                OutputPathTextBox.Text = saveDialog.FilePath;
        }

        private async void RunButton_Click(object sender, RoutedEventArgs e)
        {
            var lineLayer = (LineLayerCombo.SelectedItem as LayerItem)?.Layer;
            if (lineLayer == null)
            {
                MessageBox.Show("Please select an input line layer.", "Isolated Polylines", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            double tolerance = 0;
            var toleranceText = ToleranceTextBox.Text?.Trim();
            if (!string.IsNullOrEmpty(toleranceText) && !double.TryParse(toleranceText, out tolerance))
            {
                MessageBox.Show("Snap tolerance must be a number.", "Isolated Polylines", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var polygonLayer = (PolygonLayerCombo.SelectedItem as LayerItem)?.Layer;
            string outputPath = string.IsNullOrWhiteSpace(OutputPathTextBox.Text) ? null : OutputPathTextBox.Text.Trim();

            RunButton.IsEnabled = false;
            CloseButton.IsEnabled = false;
            SetLog(new[] { "Running..." });

            try
            {
                var (isolatedCount, messages) = await QueuedTask.Run(() =>
                {
                    var log = new List<string>();
                    var isolatedOids = IsolatedLineFinder.FindIsolatedLineOids(lineLayer, tolerance, polygonLayer, msg => log.Add(msg));

                    if (isolatedOids.Count > 0)
                        lineLayer.Select(new QueryFilter { ObjectIDs = isolatedOids }, SelectionCombinationMethod.New);
                    else
                        lineLayer.ClearSelection();

                    return (isolatedOids.Count, log);
                });

                if (!string.IsNullOrEmpty(outputPath))
                {
                    if (isolatedCount > 0)
                    {
                        var gpParams = Geoprocessing.MakeValueArray(lineLayer, outputPath);
                        var gpResult = await Geoprocessing.ExecuteToolAsync("management.CopyFeatures", gpParams,
                            null, null, null, GPExecuteToolFlags.None);
                        if (gpResult.IsFailed)
                            messages.Add("CopyFeatures failed: " + string.Join("; ", gpResult.ErrorMessages.Select(m => m.Text)));
                        else
                            messages.Add($"Copied isolated lines to {outputPath}");
                    }
                    else
                    {
                        messages.Add("No isolated lines found; output not created.");
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

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
