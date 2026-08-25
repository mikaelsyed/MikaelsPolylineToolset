using ArcGIS.Desktop.Framework.Contracts;

namespace MikaelsPolylineToolset.RasterOverlap
{
    internal class RasterOverlapButton : Button
    {
        protected override void OnClick()
        {
            var window = new RasterOverlapWindow { Owner = System.Windows.Application.Current.MainWindow };
            window.ShowDialog();
        }
    }
}
