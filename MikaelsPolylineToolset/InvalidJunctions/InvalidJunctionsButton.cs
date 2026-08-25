using ArcGIS.Desktop.Framework.Contracts;

namespace MikaelsPolylineToolset.InvalidJunctions
{
    internal class InvalidJunctionsButton : Button
    {
        protected override void OnClick()
        {
            var window = new InvalidJunctionsWindow { Owner = System.Windows.Application.Current.MainWindow };
            window.ShowDialog();
        }
    }
}
