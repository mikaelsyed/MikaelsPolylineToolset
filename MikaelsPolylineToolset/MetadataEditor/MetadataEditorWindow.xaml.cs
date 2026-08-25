using ArcGIS.Desktop.Catalog;
using ArcGIS.Desktop.Core;
using MikaelsPolylineToolset.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace MikaelsPolylineToolset.MetadataEditor
{
    public partial class MetadataEditorWindow : ArcGIS.Desktop.Framework.Controls.ProWindow
    {
        private readonly System.Windows.Forms.TextBox _logTextBox;
        private bool _canEdit;

        public MetadataEditorWindow()
        {
            InitializeComponent();

            _logTextBox = QcUiHelpers.CreateLogTextBox();
            LogHost.Child = _logTextBox;
            SetFieldsEnabled(false);

            Loaded += MetadataEditorWindow_Loaded;
        }

        private async void MetadataEditorWindow_Loaded(object sender, RoutedEventArgs e) => await RefreshMapLayersAsync();

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

        private void UseTemplateCheck_Checked(object sender, RoutedEventArgs e) => FillTemplateButton.IsEnabled = true;

        private void UseTemplateCheck_Unchecked(object sender, RoutedEventArgs e) => FillTemplateButton.IsEnabled = false;

        private void FillTemplateButton_Click(object sender, RoutedEventArgs e)
        {
            if (!TitleTextBox.IsEnabled)
            {
                MessageBox.Show("Load a feature class first (Browse or pick from the map above), then fill in the template.",
                    "Metadata Editor", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var templateWindow = new MetadataTemplateWindow { Owner = this };
            if (templateWindow.ShowDialog() != true) return;

            var result = templateWindow.Result;
            TitleTextBox.Text = result.Title;
            TagsTextBox.Text = result.Tags;
            SummaryTextBox.Text = result.Summary;
            DescriptionTextBox.Text = result.Description;
            CreditsTextBox.Text = result.Credits;
            ConstraintsTextBox.Text = result.Constraints;

            SetLog("Template applied -- review the fields below, then Save.");
        }

        private void SetLog(string line) => _logTextBox.Text = line;
        private void SetLog(IEnumerable<string> lines) => _logTextBox.Text = string.Join(Environment.NewLine, lines);

        private void SetFieldsEnabled(bool enabled)
        {
            TitleTextBox.IsEnabled = enabled;
            TagsTextBox.IsEnabled = enabled;
            SummaryTextBox.IsEnabled = enabled;
            DescriptionTextBox.IsEnabled = enabled;
            CreditsTextBox.IsEnabled = enabled;
            ConstraintsTextBox.IsEnabled = enabled;
            SaveButton.IsEnabled = enabled && _canEdit;
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
                MessageBox.Show("Please choose a feature class first.", "Metadata Editor", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!QcUiHelpers.FeatureClassPathExists(path))
            {
                MessageBox.Show("The selected feature class could not be found on disk.", "Metadata Editor", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            LoadButton.IsEnabled = false;
            SetFieldsEnabled(false);
            StatusTextBlock.Text = "";
            SetLog("Loading metadata...");

            try
            {
                var (success, canEdit, metadata, message) = await MetadataEditorService.LoadAsync(path);
                _canEdit = canEdit;

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

                SetFieldsEnabled(true);
                StatusTextBlock.Text = canEdit ? "" : "Read-only -- this item's metadata cannot be saved.";
                SetLog(message);
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

        private async void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            string path = ShapefilePathTextBox.Text?.Trim();
            if (string.IsNullOrEmpty(path) || !QcUiHelpers.FeatureClassPathExists(path))
            {
                MessageBox.Show("Please load a valid feature class first.", "Metadata Editor", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var metadata = new ShapefileMetadata
            {
                Title = TitleTextBox.Text,
                Tags = TagsTextBox.Text,
                Summary = SummaryTextBox.Text,
                Description = DescriptionTextBox.Text,
                Credits = CreditsTextBox.Text,
                Constraints = ConstraintsTextBox.Text
            };

            SaveButton.IsEnabled = false;
            LoadButton.IsEnabled = false;
            SetLog("Saving...");

            try
            {
                var (success, message) = await MetadataEditorService.SaveAsync(path, metadata);
                SetLog(message);
            }
            catch (Exception ex)
            {
                SetLog("Error: " + ex.Message);
            }
            finally
            {
                SaveButton.IsEnabled = _canEdit;
                LoadButton.IsEnabled = true;
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}
