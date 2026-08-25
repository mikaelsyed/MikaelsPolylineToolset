using ArcGIS.Desktop.Catalog;
using ArcGIS.Desktop.Core;
using MikaelsPolylineToolset.Common;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace MikaelsPolylineToolset.MetadataExport
{
    public partial class MetadataExportWindow : ArcGIS.Desktop.Framework.Controls.ProWindow
    {
        private readonly System.Windows.Forms.TextBox _logTextBox;

        public MetadataExportWindow()
        {
            InitializeComponent();

            _logTextBox = QcUiHelpers.CreateLogTextBox();
            LogHost.Child = _logTextBox;

            Loaded += MetadataExportWindow_Loaded;
        }

        private void SetLog(string line) => _logTextBox.Text = line;

        private async void MetadataExportWindow_Loaded(object sender, RoutedEventArgs e) => await RefreshMapLayersAsync();

        private async void RefreshLayersButton_Click(object sender, RoutedEventArgs e) => await RefreshMapLayersAsync();

        private async Task RefreshMapLayersAsync()
        {
            var layers = await QcUiHelpers.GetEditableFeatureLayerItemsAsync();
            MapLayerCombo.ItemsSource = layers;
            if (layers.Count == 0)
                SetLog("No shapefile or file geodatabase feature layers found in the active map. Use Browse to pick one from disk.");
        }

        private void MapLayerCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            var selected = MapLayerCombo.SelectedItem as QcUiHelpers.LayerItem;
            if (selected?.Path == null) return;

            SetShapefilePath(selected.Path);
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var openDlg = new OpenItemDialog
            {
                Title = "Select a Feature Class",
                MultiSelect = false,
                BrowseFilter = BrowseProjectFilter.GetFilter(ItemFilters.FeatureClasses_All)
            };
            if (openDlg.ShowDialog() != true) return;

            var selected = openDlg.Items?.FirstOrDefault();
            if (selected == null) return;

            SetShapefilePath(selected.Path);
        }

        private void SetShapefilePath(string path)
        {
            ShapefilePathTextBox.Text = path;

            if (string.IsNullOrWhiteSpace(OutputFolderTextBox.Text))
                OutputFolderTextBox.Text = QcUiHelpers.GetDefaultSiblingFolder(path);

            if (string.IsNullOrWhiteSpace(OutputFileNameTextBox.Text))
                OutputFileNameTextBox.Text = Path.GetFileNameWithoutExtension(path) + "_metadata.txt";
        }

        private void BrowseFolderButton_Click(object sender, RoutedEventArgs e)
        {
            using var folderDlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Choose a folder to export the metadata text file to"
            };
            if (!string.IsNullOrWhiteSpace(OutputFolderTextBox.Text) && Directory.Exists(OutputFolderTextBox.Text))
                folderDlg.SelectedPath = OutputFolderTextBox.Text;

            if (folderDlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                OutputFolderTextBox.Text = folderDlg.SelectedPath;
        }

        private async void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            string shapefilePath = ShapefilePathTextBox.Text?.Trim();
            if (string.IsNullOrEmpty(shapefilePath))
            {
                MessageBox.Show("Please choose a feature class first.", "Metadata Export", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!QcUiHelpers.FeatureClassPathExists(shapefilePath))
            {
                MessageBox.Show("The selected feature class could not be found on disk.", "Metadata Export", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string folder = OutputFolderTextBox.Text?.Trim();
            if (string.IsNullOrEmpty(folder))
            {
                MessageBox.Show("Please choose an output folder.", "Metadata Export", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!Directory.Exists(folder))
            {
                MessageBox.Show("The chosen output folder does not exist.", "Metadata Export", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string fileName = OutputFileNameTextBox.Text?.Trim();
            if (string.IsNullOrEmpty(fileName))
            {
                MessageBox.Show("Please enter an output filename.", "Metadata Export", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                fileName += ".txt";

            string outputPath = Path.Combine(folder, fileName);
            if (File.Exists(outputPath))
            {
                var result = MessageBox.Show($"'{fileName}' already exists in this folder. Overwrite it?", "Metadata Export",
                    MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result != MessageBoxResult.Yes) return;
            }

            ExportButton.IsEnabled = false;
            SetLog("Exporting...");

            try
            {
                var (success, message) = await MetadataExportService.ExportAsync(shapefilePath, outputPath);
                SetLog(message);
            }
            catch (Exception ex)
            {
                SetLog("Error: " + ex.Message);
            }
            finally
            {
                ExportButton.IsEnabled = true;
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}
