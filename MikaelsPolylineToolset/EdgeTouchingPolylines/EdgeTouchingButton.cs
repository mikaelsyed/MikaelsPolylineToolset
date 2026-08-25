using ArcGIS.Desktop.Framework.Contracts;

namespace MikaelsPolylineToolset.EdgeTouchingPolylines
{
    internal class EdgeTouchingButton : Button
    {
        protected override void OnClick()
        {
            var window = new EdgeTouchingWindow { Owner = System.Windows.Application.Current.MainWindow };
            window.ShowDialog();
        }
    }
}
