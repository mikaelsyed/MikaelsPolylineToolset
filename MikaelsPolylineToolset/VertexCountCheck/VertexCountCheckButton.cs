using ArcGIS.Desktop.Framework.Contracts;

namespace MikaelsPolylineToolset.VertexCountCheck
{
    internal class VertexCountCheckButton : Button
    {
        protected override void OnClick()
        {
            var window = new VertexCountCheckWindow { Owner = System.Windows.Application.Current.MainWindow };
            window.ShowDialog();
        }
    }
}
