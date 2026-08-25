using ArcGIS.Desktop.Framework.Contracts;

namespace MikaelsPolylineToolset.VertexConditioning
{
    internal class VertexConditioningButton : Button
    {
        protected override void OnClick()
        {
            var window = new VertexConditioningWindow { Owner = System.Windows.Application.Current.MainWindow };
            window.ShowDialog();
        }
    }
}
