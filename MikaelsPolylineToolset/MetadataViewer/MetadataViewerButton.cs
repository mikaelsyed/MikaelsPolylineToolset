using ArcGIS.Desktop.Framework.Contracts;

namespace MikaelsPolylineToolset.MetadataViewer
{
    internal class MetadataViewerButton : Button
    {
        protected override void OnClick()
        {
            var window = new MetadataViewerWindow { Owner = System.Windows.Application.Current.MainWindow };
            window.ShowDialog();
        }
    }
}
