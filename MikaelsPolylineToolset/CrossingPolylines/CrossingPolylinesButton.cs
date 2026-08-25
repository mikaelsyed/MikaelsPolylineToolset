using ArcGIS.Desktop.Framework.Contracts;

namespace MikaelsPolylineToolset.CrossingPolylines
{
    internal class CrossingPolylinesButton : Button
    {
        protected override void OnClick()
        {
            var window = new CrossingPolylinesWindow { Owner = System.Windows.Application.Current.MainWindow };
            window.ShowDialog();
        }
    }
}
