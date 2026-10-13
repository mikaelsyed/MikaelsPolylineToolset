using ArcGIS.Desktop.Catalog;
using MikaelsPolylineToolset.Common;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;

namespace MikaelsPolylineToolset.PolylineIntersectPoints
{
    public partial class PolylineIntersectPointsWindow : ArcGIS.Desktop.Framework.Controls.ProWindow
    {
        private readonly System.Windows.Forms.TextBox _logTextBox;
        private CancellationTokenSource _cts;

        public PolylineIntersectPointsWindow()
        {
            InitializeComponent();

            _logTextBox = QcUiHelpers.CreateLogTextBox();
            LogHost.Child = _logTextBox;
            QcUiHelpers.ConfigureDefaultGdbToggle(UseDefaultGdbCheck, OutputPathTextBox, BrowseButton, "PolylineIntersectPointsOutput");

            Loaded += PolylineIntersectPointsWindow_Loaded;
        }

        private void SetLog(IEnumerable<string> lines) => _logTextBox.Text = string.Join(Environment.NewLine, lines);

        private async void PolylineIntersectPointsWindow_Loaded(object sender, RoutedEventArgs e)
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
                MessageBox.Show("Please select an input line layer and an input polygon layer.", "Polyline Intersect Points", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            double tolerance = 0;
            var toleranceText = ToleranceTextBox.Text?.Trim();
            if (!string.IsNullOrEmpty(toleranceText) && !double.TryParse(toleranceText, out tolerance))
            {
                MessageBox.Show("Snap tolerance must be a number.", "Polyline Intersect Points", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string outputPath = OutputPathTextBox.Text?.Trim();
            if (string.IsNullOrEmpty(outputPath))
            {
                MessageBox.Show("Please choose an output feature class.", "Polyline Intersect Points", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

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
                long records = await QcRunUi.CountAsync(lineLayer, polygonLayer);
                run = QcRunProgress.ForSingle("Polyline Intersect Points", lineLayer.Name, records, cts.Token);
                ProgressPanel.Start(run);
                run.BeginUnit("Polyline Intersect Points", lineLayer.Name);
                var progress = run.Current;

                var (success, count, messages) = await PolylineIntersectPointsService.RunAsync(lineLayer, polygonLayer, tolerance, outputPath, run.Current);
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
