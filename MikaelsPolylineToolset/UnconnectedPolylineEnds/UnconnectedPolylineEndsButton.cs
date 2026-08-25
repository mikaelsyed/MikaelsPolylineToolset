using ArcGIS.Desktop.Framework.Contracts;

namespace MikaelsPolylineToolset.UnconnectedPolylineEnds
{
    internal class UnconnectedPolylineEndsButton : Button
    {
        protected override void OnClick()
        {
            var window = new UnconnectedPolylineEndsWindow { Owner = System.Windows.Application.Current.MainWindow };
            window.ShowDialog();
        }
    }
}
