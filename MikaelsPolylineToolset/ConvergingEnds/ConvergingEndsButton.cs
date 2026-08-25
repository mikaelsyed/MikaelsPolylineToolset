using ArcGIS.Desktop.Framework.Contracts;

namespace MikaelsPolylineToolset.ConvergingEnds
{
    internal class ConvergingEndsButton : Button
    {
        protected override void OnClick()
        {
            var window = new ConvergingEndsWindow { Owner = System.Windows.Application.Current.MainWindow };
            window.ShowDialog();
        }
    }
}
