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

namespace MikaelsPolylineToolset.NValenceJunctions
{
    public partial class NValenceJunctionsWindow : ArcGIS.Desktop.Framework.Controls.ProWindow
    {
        private readonly System.Windows.Forms.TextBox _logTextBox;

        public NValenceJunctionsWindow()
        {
            InitializeComponent();

            _logTextBox = QcUiHelpers.CreateLogTextBox();
            LogHost.Child = _logTextBox;
            QcUiHelpers.ConfigureDefaultGdbToggle(UseDefaultGdbCheck, OutputPathTextBox, BrowseButton, "ValenceJunctionsOutput");

            Loaded += NValenceJunctionsWindow_Loaded;
        }

        private void SetLog(IEnumerable<string> lines) => _logTextBox.Text = string.Join(Environment.NewLine, lines);

        private async void NValenceJunctionsWindow_Loaded(object sender, RoutedEventArgs e)
        {
            var lineLayers = await QcUiHelpers.GetPolylineLayerItemsAsync();
            LineLayerCombo.ItemsSource = lineLayers;
            if (lineLayers.Count > 0) LineLayerCombo.SelectedIndex = 0;
            else SetLog(new[] { "No polyline layers found in the active map." });
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
            if (lineLayer == null)
            {
                MessageBox.Show("Please select an input line layer.", "N-Valence Junctions", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!int.TryParse(ValenceTextBox.Text?.Trim(), out int valence) || valence < 1)
            {
                MessageBox.Show("Valence must be a positive whole number.", "N-Valence Junctions", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            double? clusterTol = null;
            var clusterTolText = ClusterTolTextBox.Text?.Trim();
            if (!string.IsNullOrEmpty(clusterTolText))
            {
                if (!double.TryParse(clusterTolText, out double parsedTol))
                {
                    MessageBox.Show("Cluster tolerance must be a number.", "N-Valence Junctions", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                clusterTol = parsedTol;
            }

            bool onePerJunction = OnePerJunctionCheck.IsChecked == true;
            string outputPath = string.IsNullOrWhiteSpace(OutputPathTextBox.Text) ? null : OutputPathTextBox.Text.Trim();

            RunButton.IsEnabled = false;
            CloseButton.IsEnabled = false;
            SetLog(new[] { "Running..." });

            try
            {
                var (count, messages) = await QueuedTask.Run(() =>
                {
                    var log = new List<string>();
                    var oids = ValenceJunctionFinder.FindValenceJunctionOids(lineLayer, valence, onePerJunction, clusterTol, msg => log.Add(msg));

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
                            messages.Add($"Copied junction lines to {outputPath}");
                    }
                    else
                    {
                        messages.Add("No matching junctions found; output not created.");
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
