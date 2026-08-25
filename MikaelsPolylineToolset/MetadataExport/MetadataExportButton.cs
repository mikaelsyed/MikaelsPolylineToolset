using ArcGIS.Desktop.Framework.Contracts;

namespace MikaelsPolylineToolset.MetadataExport
{
    internal class MetadataExportButton : Button
    {
        protected override void OnClick()
        {
            var window = new MetadataExportWindow { Owner = System.Windows.Application.Current.MainWindow };
            window.ShowDialog();
        }
    }
}
