using ArcGIS.Core.Data;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using MikaelsPolylineToolset.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace MikaelsPolylineToolset.VertexConditioning
{
    public partial class VertexConditioningWindow : ArcGIS.Desktop.Framework.Controls.ProWindow
    {
        private readonly System.Windows.Forms.TextBox _logTextBox;
        private List<VertexConditioningService.FeatureResult> _lastResults;
        private FeatureLayer _lastLayer;

        public VertexConditioningWindow()
        {
            InitializeComponent();

            _logTextBox = QcUiHelpers.CreateLogTextBox();
            LogHost.Child = _logTextBox;

            Loaded += VertexConditioningWindow_Loaded;
        }

        private void SetLog(IEnumerable<string> lines) => _logTextBox.Text = string.Join(Environment.NewLine, lines);

        private async void VertexConditioningWindow_Loaded(object sender, RoutedEventArgs e)
        {
            var lineLayers = await QcUiHelpers.GetPolylineLayerItemsAsync();
            LineLayerCombo.ItemsSource = lineLayers;
            if (lineLayers.Count > 0) LineLayerCombo.SelectedIndex = 0;

            var rasterLayers = await QcUiHelpers.GetRasterLayerItemsAsync();
            RasterLayerCombo.ItemsSource = rasterLayers;
            if (rasterLayers.Count > 0) RasterLayerCombo.SelectedIndex = 0;

            if (lineLayers.Count == 0 || rasterLayers.Count == 0)
                SetLog(new[] { "Need at least one polyline layer and one raster (DEM) layer in the active map." });
        }

        private void InvalidateApply()
        {
            ApplyButton.IsEnabled = false;
            _lastResults = null;
            _lastLayer = null;
        }

        private async void PreviewButton_Click(object sender, RoutedEventArgs e)
        {
            var lineLayer = (LineLayerCombo.SelectedItem as QcUiHelpers.LayerItem)?.Layer;
            var rasterLayer = (RasterLayerCombo.SelectedItem as QcUiHelpers.RasterLayerItem)?.Layer;
            if (lineLayer == null || rasterLayer == null)
            {
                MessageBox.Show("Please select an input polyline layer and an input DEM raster layer.", "Recondition Polyline Vertices", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!double.TryParse(MinSpacingTextBox.Text?.Trim(), out double minSpacing) || minSpacing <= 0)
            {
                MessageBox.Show("Min Vertex Spacing must be a number greater than 0.", "Recondition Polyline Vertices", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!double.TryParse(MaxSpacingTextBox.Text?.Trim(), out double maxSpacing) || maxSpacing <= minSpacing)
            {
                MessageBox.Show("Max Vertex Spacing must be a number greater than Min Vertex Spacing.", "Recondition Polyline Vertices", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!double.TryParse(SearchRadiusTextBox.Text?.Trim(), out double searchRadius) || searchRadius <= 0)
            {
                MessageBox.Show("Elevation Fix Search Radius must be a number greater than 0.", "Recondition Polyline Vertices", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            bool selectedOnly = SelectedOnlyCheck.IsChecked == true;

            PreviewButton.IsEnabled = false;
            ApplyButton.IsEnabled = false;
            CloseButton.IsEnabled = false;
            SetLog(new[] { "Computing preview..." });

            try
            {
                var (results, messages) = await VertexConditioningService.ComputeAsync(
                    lineLayer, rasterLayer, minSpacing, maxSpacing, searchRadius, selectedOnly);

                _lastResults = results;
                _lastLayer = lineLayer;

                await QueuedTask.Run(() =>
                {
                    var flagged = results.Where(r => r.Flags.Count > 0).Select(r => r.Oid).ToList();
                    var changed = results.Where(r => r.Changed && !r.Skipped).Select(r => r.Oid).ToList();
                    var toSelect = flagged.Count > 0 ? flagged : changed;
                    if (toSelect.Count > 0)
                        lineLayer.Select(new QueryFilter { ObjectIDs = toSelect }, SelectionCombinationMethod.New);
                    else
                        lineLayer.ClearSelection();
                });

                SetLog(messages);
                ApplyButton.IsEnabled = results.Any(r => r.Changed && !r.Skipped);
            }
            catch (Exception ex)
            {
                SetLog(new[] { "Error: " + ex.Message });
                InvalidateApply();
            }
            finally
            {
                PreviewButton.IsEnabled = true;
                CloseButton.IsEnabled = true;
            }
        }

        private async void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            if (_lastResults == null || _lastLayer == null)
                return;

            int changeCount = _lastResults.Count(r => r.Changed && !r.Skipped);
            var confirm = MessageBox.Show(
                $"Apply changes to {changeCount} polyline(s)? This edits the input layer directly (undoable from Pro's Edit > Undo).",
                "Recondition Polyline Vertices", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes)
                return;

            PreviewButton.IsEnabled = false;
            ApplyButton.IsEnabled = false;
            CloseButton.IsEnabled = false;

            try
            {
                var (success, count, message) = await VertexConditioningService.ApplyAsync(_lastLayer, _lastResults);
                var log = _logTextBox.Text.Split(new[] { Environment.NewLine }, StringSplitOptions.None).ToList();
                log.Add("");
                log.Add(message);
                SetLog(log);
            }
            catch (Exception ex)
            {
                var log = _logTextBox.Text.Split(new[] { Environment.NewLine }, StringSplitOptions.None).ToList();
                log.Add("");
                log.Add("Error applying edits: " + ex.Message);
                SetLog(log);
            }
            finally
            {
                // Geometries on disk have moved on from what was previewed; require a fresh
                // Preview before allowing another Apply.
                InvalidateApply();
                PreviewButton.IsEnabled = true;
                CloseButton.IsEnabled = true;
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}
