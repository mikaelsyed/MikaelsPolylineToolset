using ArcGIS.Core.Data;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using MikaelsPolylineToolset.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;

namespace MikaelsPolylineToolset.VertexConditioning
{
    public partial class VertexConditioningWindow : ArcGIS.Desktop.Framework.Controls.ProWindow
    {
        private readonly System.Windows.Forms.TextBox _logTextBox;
        private List<VertexConditioningService.FeatureResult> _lastResults;
        private FeatureLayer _lastLayer;
        private CancellationTokenSource _cts;

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
            var cts = new CancellationTokenSource();
            _cts = cts;
            CancelButton.Content = "Cancel";
            CancelButton.IsEnabled = true;
            CancelButton.Visibility = Visibility.Visible;
            QcRunProgress run = null;

            try
            {
                long records = await QcRunUi.CountAsync(lineLayer);
                run = QcRunProgress.ForSingle("Recondition Polyline Vertices", lineLayer.Name, records, cts.Token);
                ProgressPanel.Start(run);
                run.BeginUnit("Recondition Polyline Vertices", lineLayer.Name);

                var (results, messages) = await VertexConditioningService.ComputeAsync(
                    lineLayer, rasterLayer, minSpacing, maxSpacing, searchRadius, selectedOnly, run.Current);

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
            catch (OperationCanceledException)
            {
                SetLog(new[] { "Cancelled." });
                InvalidateApply();
            }
            catch (Exception ex)
            {
                SetLog(new[] { "Error: " + ex.Message });
                InvalidateApply();
            }
            finally
            {
                run?.EndUnit();
                ProgressPanel.Stop(cts.IsCancellationRequested ? "Cancelled" : "Finished");
                CancelButton.Visibility = Visibility.Collapsed;
                _cts = null;
                cts.Dispose();
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

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            CancelButton.IsEnabled = false;
            CancelButton.Content = "Cancelling...";
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}
