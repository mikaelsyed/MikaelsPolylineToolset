using ArcGIS.Desktop.Framework.Contracts;

namespace MikaelsPolylineToolset.NValenceJunctions
{
    internal class NValenceJunctionsButton : Button
    {
        protected override void OnClick()
        {
            var window = new NValenceJunctionsWindow { Owner = System.Windows.Application.Current.MainWindow };
            window.ShowDialog();
        }
    }
}
