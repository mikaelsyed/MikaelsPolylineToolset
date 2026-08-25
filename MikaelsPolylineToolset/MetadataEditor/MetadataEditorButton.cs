using ArcGIS.Desktop.Framework.Contracts;

namespace MikaelsPolylineToolset.MetadataEditor
{
    internal class MetadataEditorButton : Button
    {
        protected override void OnClick()
        {
            var window = new MetadataEditorWindow { Owner = System.Windows.Application.Current.MainWindow };
            window.ShowDialog();
        }
    }
}
