using ArcGIS.Desktop.Framework.Contracts;

namespace MikaelsPolylineToolset.IsolatedPolylines
{
    internal class IsolatedPolylinesButton : Button
    {
        protected override void OnClick()
        {
            var window = new IsolatedPolylinesWindow
            {
                Owner = System.Windows.Application.Current.MainWindow
            };
            window.ShowDialog();
        }
    }
}
