using ArcGIS.Desktop.Catalog;
using ArcGIS.Desktop.Core;
using MikaelsPolylineToolset.Common;
using MikaelsPolylineToolset.MetadataEditor;
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace MikaelsPolylineToolset.MetadataViewer
{
    public partial class MetadataViewerWindow : ArcGIS.Desktop.Framework.Controls.ProWindow
    {
        private readonly System.Windows.Forms.TextBox _logTextBox;

        public MetadataViewerWindow()
        {
            InitializeComponent();

            _logTextBox = QcUiHelpers.CreateLogTextBox();
            LogHost.Child = _logTextBox;

            Loaded += MetadataViewerWindow_Loaded;
        }

        private void SetLog(string line) => _logTextBox.Text = line;

        private void ClearFields()
        {
            TitleTextBox.Text = "";
            TagsTextBox.Text = "";
            SummaryTextBox.Text = "";
            DescriptionTextBox.Text = "";
            CreditsTextBox.Text = "";
            ConstraintsTextBox.Text = "";
        }

        private async void MetadataViewerWindow_Loaded(object sender, RoutedEventArgs e) => await RefreshMapLayersAsync();

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

            ShapefilePathTextBox.Text = selected.Path;
            _ = LoadMetadataAsync();
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

            ShapefilePathTextBox.Text = selected.Path;
            _ = LoadMetadataAsync();
        }

        private async void LoadButton_Click(object sender, RoutedEventArgs e) => await LoadMetadataAsync();

        private async Task LoadMetadataAsync()
        {
            string path = ShapefilePathTextBox.Text?.Trim();
            if (string.IsNullOrEmpty(path))
            {
                MessageBox.Show("Please choose a feature class first.", "Metadata Viewer", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!QcUiHelpers.FeatureClassPathExists(path))
            {
                MessageBox.Show("The selected feature class could not be found on disk.", "Metadata Viewer", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            LoadButton.IsEnabled = false;
            StatusTextBlock.Text = "";
            ClearFields();
            SetLog("Loading metadata...");

            try
            {
                var (success, _, metadata, message) = await MetadataEditorService.LoadAsync(path);
                if (!success)
                {
                    SetLog(message);
                    return;
                }

                TitleTextBox.Text = metadata.Title;
                TagsTextBox.Text = metadata.Tags;
                SummaryTextBox.Text = metadata.Summary;
                DescriptionTextBox.Text = metadata.Description;
                CreditsTextBox.Text = metadata.Credits;
                ConstraintsTextBox.Text = metadata.Constraints;

                SetLog($"Metadata loaded from {path}");
            }
            catch (Exception ex)
            {
                SetLog("Error: " + ex.Message);
            }
            finally
            {
                LoadButton.IsEnabled = true;
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}
