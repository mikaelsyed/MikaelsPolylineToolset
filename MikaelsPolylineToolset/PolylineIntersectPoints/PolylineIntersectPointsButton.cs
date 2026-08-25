using ArcGIS.Desktop.Framework.Contracts;

namespace MikaelsPolylineToolset.PolylineIntersectPoints
{
    internal class PolylineIntersectPointsButton : Button
    {
        protected override void OnClick()
        {
            var window = new PolylineIntersectPointsWindow { Owner = System.Windows.Application.Current.MainWindow };
            window.ShowDialog();
        }
    }
}
