using ArcGIS.Desktop.Framework.Contracts;

namespace MikaelsPolylineToolset.ValidateInflowOutflow
{
    internal class ValidateInflowOutflowButton : Button
    {
        protected override void OnClick()
        {
            var window = new ValidateInflowOutflowWindow { Owner = System.Windows.Application.Current.MainWindow };
            window.ShowDialog();
        }
    }
}
