using ArcGIS.Desktop.Catalog;
using MikaelsPolylineToolset.Common;
using System;
using System.Collections.Generic;
using System.Windows;

namespace MikaelsPolylineToolset.ValidateInflowOutflow
{
    public partial class ValidateInflowOutflowWindow : ArcGIS.Desktop.Framework.Controls.ProWindow
    {
        private readonly System.Windows.Forms.TextBox _logTextBox;

        public ValidateInflowOutflowWindow()
        {
            InitializeComponent();

            _logTextBox = QcUiHelpers.CreateLogTextBox();
            LogHost.Child = _logTextBox;
            QcUiHelpers.ConfigureDefaultGdbToggle(UseDefaultGdbCheck, OutputPathTextBox, BrowseButton, "InflowOutflowValidationOutput");

            Loaded += ValidateInflowOutflowWindow_Loaded;
        }

        private void SetLog(IEnumerable<string> lines) => _logTextBox.Text = string.Join(Environment.NewLine, lines);

        private async void ValidateInflowOutflowWindow_Loaded(object sender, RoutedEventArgs e)
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

        private async void PolygonLayerCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            var polygonLayer = (PolygonLayerCombo.SelectedItem as QcUiHelpers.LayerItem)?.Layer;
            var fields = await QcUiHelpers.GetAttributeFieldItemsAsync(polygonLayer);
            TypeFieldCombo.ItemsSource = fields;
            if (fields.Count > 0) TypeFieldCombo.SelectedIndex = 0;
            else OceanValueCombo.ItemsSource = null;
        }

        private async void TypeFieldCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            var polygonLayer = (PolygonLayerCombo.SelectedItem as QcUiHelpers.LayerItem)?.Layer;
            var field = (TypeFieldCombo.SelectedItem as QcUiHelpers.FieldItem)?.Name;
            if (polygonLayer == null || field == null)
            {
                OceanValueCombo.ItemsSource = null;
                return;
            }
            var values = await QcUiHelpers.GetDistinctFieldValuesAsync(polygonLayer, field);
            OceanValueCombo.ItemsSource = values;
            if (values.Count > 0) OceanValueCombo.SelectedIndex = 0;
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
            var typeField = (TypeFieldCombo.SelectedItem as QcUiHelpers.FieldItem)?.Name;
            string oceanValue = (OceanValueCombo.SelectedItem as string) ?? OceanValueCombo.Text?.Trim();

            if (lineLayer == null || polygonLayer == null || string.IsNullOrEmpty(typeField) || string.IsNullOrEmpty(oceanValue))
            {
                MessageBox.Show("Please select the input layers, polygon type field, and ocean attribute value.", "Validate Inflow/Outflow", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            double tolerance = 0;
            var toleranceText = ToleranceTextBox.Text?.Trim();
            if (!string.IsNullOrEmpty(toleranceText) && !double.TryParse(toleranceText, out tolerance))
            {
                MessageBox.Show("Snap tolerance must be a number.", "Validate Inflow/Outflow", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string outputPath = OutputPathTextBox.Text?.Trim();
            if (string.IsNullOrEmpty(outputPath))
            {
                MessageBox.Show("Please choose an output feature class.", "Validate Inflow/Outflow", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            RunButton.IsEnabled = false;
            CloseButton.IsEnabled = false;
            SetLog(new[] { "Running..." });

            try
            {
                var (success, count, checkCount, messages) = await ValidateInflowOutflowService.RunAsync(
                    lineLayer, polygonLayer, tolerance, typeField, oceanValue, outputPath);
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
