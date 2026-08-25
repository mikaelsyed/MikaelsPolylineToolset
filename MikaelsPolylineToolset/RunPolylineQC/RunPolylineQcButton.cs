using ArcGIS.Desktop.Framework.Contracts;

namespace MikaelsPolylineToolset.RunPolylineQC
{
    internal class RunPolylineQcButton : Button
    {
        protected override void OnClick()
        {
            var window = new RunPolylineQcWindow { Owner = System.Windows.Application.Current.MainWindow };
            window.ShowDialog();
        }
    }
}
