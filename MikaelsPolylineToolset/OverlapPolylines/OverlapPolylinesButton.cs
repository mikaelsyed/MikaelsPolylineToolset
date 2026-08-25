using ArcGIS.Desktop.Framework.Contracts;

namespace MikaelsPolylineToolset.OverlapPolylines
{
    internal class OverlapPolylinesButton : Button
    {
        protected override void OnClick()
        {
            var window = new OverlapPolylinesWindow { Owner = System.Windows.Application.Current.MainWindow };
            window.ShowDialog();
        }
    }
}
