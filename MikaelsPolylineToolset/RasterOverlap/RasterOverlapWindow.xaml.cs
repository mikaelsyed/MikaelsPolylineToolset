using ArcGIS.Desktop.Catalog;
using MikaelsPolylineToolset.Common;
using System;
using System.Collections.Generic;
using System.Windows;

namespace MikaelsPolylineToolset.RasterOverlap
{
    public partial class RasterOverlapWindow : ArcGIS.Desktop.Framework.Controls.ProWindow
    {
        private readonly System.Windows.Forms.TextBox _logTextBox;

        public RasterOverlapWindow()
        {
            InitializeComponent();

            _logTextBox = QcUiHelpers.CreateLogTextBox();
            LogHost.Child = _logTextBox;
            QcUiHelpers.ConfigureDefaultGdbToggle(UseDefaultGdbCheck, OutputPathTextBox, BrowseButton, "RasterOverlapOutput");

            Loaded += RasterOverlapWindow_Loaded;
        }

        private void SetLog(IEnumerable<string> lines) => _logTextBox.Text = string.Join(Environment.NewLine, lines);

        private async void RasterOverlapWindow_Loaded(object sender, RoutedEventArgs e)
        {
            var lineLayers = await QcUiHelpers.GetPolylineLayerItemsAsync();
            LineLayerCombo.ItemsSource = lineLayers;
            if (lineLayers.Count > 0) LineLayerCombo.SelectedIndex = 0;

            var rasterLayers = await QcUiHelpers.GetRasterLayerItemsAsync();
            RasterLayerCombo.ItemsSource = rasterLayers;
            if (rasterLayers.Count > 0) RasterLayerCombo.SelectedIndex = 0;

            if (lineLayers.Count == 0 || rasterLayers.Count == 0)
                SetLog(new[] { "Need at least one polyline layer and one raster layer in the active map." });
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
            var rasterLayer = (RasterLayerCombo.SelectedItem as QcUiHelpers.RasterLayerItem)?.Layer;
            if (lineLayer == null || rasterLayer == null)
            {
                MessageBox.Show("Please select an input line layer and an input raster layer.", "Raster % Overlap", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!double.TryParse(IntervalTextBox.Text?.Trim(), out double interval) || interval <= 0)
            {
                MessageBox.Show("Sample interval must be a number greater than 0.", "Raster % Overlap", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string outputPath = OutputPathTextBox.Text?.Trim();
            if (string.IsNullOrEmpty(outputPath))
            {
                MessageBox.Show("Please choose an output feature class.", "Raster % Overlap", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            RunButton.IsEnabled = false;
            CloseButton.IsEnabled = false;
            SetLog(new[] { "Running..." });

            try
            {
                var (success, count, messages) = await RasterOverlapService.RunAsync(lineLayer, rasterLayer, interval, outputPath);
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
