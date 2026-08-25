using System;
using System.Windows;
using System.Windows.Controls;

namespace MikaelsPolylineToolset.MetadataEditor
{
    public partial class MetadataTemplateWindow : ArcGIS.Desktop.Framework.Controls.ProWindow
    {
        /// <summary>Populated after ShowDialog() returns true.</summary>
        internal ShapefileMetadata Result { get; private set; }

        public MetadataTemplateWindow()
        {
            InitializeComponent();

            ProcessCombo.ItemsSource = MetadataTemplateService.ProcessOptions;
            DlmTextBox.Text = DateTime.Now.ToString("M/d/yyyy");
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            StatusTextBlock.Text = "";

            string subarea = SubareaTextBox.Text?.Trim();
            string process = (ProcessCombo.Text ?? "").Trim();
            string revision = RevisionTextBox.Text?.Trim();
            string name = NameTextBox.Text?.Trim();
            string dlm = DlmTextBox.Text?.Trim();

            if (string.IsNullOrEmpty(subarea) || string.IsNullOrEmpty(process) ||
                string.IsNullOrEmpty(revision) || string.IsNullOrEmpty(name) || string.IsNullOrEmpty(dlm))
            {
                StatusTextBlock.Text = "Subarea, Process, Revision, Name, and Date Last Modified are required.";
                return;
            }

            if (!TryReadCount(IsolatedLinesErrorsBox, "Isolated Lines Errors", out int isolatedLinesErrors) ||
                !TryReadCount(IsolatedLinesFpBox, "Isolated Lines False Positives", out int isolatedLinesFp) ||
                !TryReadCount(CrossingPolylinesErrorsBox, "Crossing Flow Errors", out int crossingFlowErrors) ||
                !TryReadCount(CrossingPolylinesFpBox, "Crossing Flow False Positives", out int crossingFlowFp) ||
                !TryReadCount(OverlapPolylinesErrorsBox, "Overlap Flow Errors", out int overlapFlowErrors) ||
                !TryReadCount(OverlapPolylinesFpBox, "Overlap Flow False Positives", out int overlapFlowFp) ||
                !TryReadCount(ConvergingEndsErrorsBox, "Converging Ends Errors", out int convergingEndsErrors) ||
                !TryReadCount(ConvergingEndsFpBox, "Converging Ends False Positives", out int convergingEndsFp) ||
                !TryReadCount(NValence3ErrorsBox, "N-Valence Junctions (3) Errors", out int nValence3Errors) ||
                !TryReadCount(NValence3FpBox, "N-Valence Junctions (3) False Positives", out int nValence3Fp) ||
                !TryReadCount(NValence4ErrorsBox, "N-Valence Junctions (4) Errors", out int nValence4Errors) ||
                !TryReadCount(NValence4FpBox, "N-Valence Junctions (4) False Positives", out int nValence4Fp) ||
                !TryReadCount(CulvertVertexErrorsBox, "Vertex Count Check Errors", out int culvertErrors) ||
                !TryReadCount(CulvertVertexFpBox, "Vertex Count Check False Positives", out int culvertFp) ||
                !TryReadCount(UnconnectedEndsErrorsBox, "Unconnected Ends Errors", out int unconnectedErrors) ||
                !TryReadCount(UnconnectedEndsFpBox, "Unconnected Ends False Positives", out int unconnectedFp) ||
                !TryReadCount(ValidateInOutErrorsBox, "Validate Inflow/Outflow Errors", out int validateErrors) ||
                !TryReadCount(ValidateInOutFpBox, "Validate Inflow/Outflow False Positives", out int validateFp))
            {
                return;
            }

            var input = new MetadataTemplateInput
            {
                Subarea = subarea,
                Process = process,
                Revision = revision,
                Name = name,
                DateLastModified = dlm,
                Notes = NotesTextBox.Text?.Trim() ?? "",
                IsolatedLinesErrors = isolatedLinesErrors,
                IsolatedLinesFalsePositives = isolatedLinesFp,
                CrossingPolylinesErrors = crossingFlowErrors,
                CrossingPolylinesFalsePositives = crossingFlowFp,
                OverlapPolylinesErrors = overlapFlowErrors,
                OverlapPolylinesFalsePositives = overlapFlowFp,
                ConvergingEndsErrors = convergingEndsErrors,
                ConvergingEndsFalsePositives = convergingEndsFp,
                NValence3Errors = nValence3Errors,
                NValence3FalsePositives = nValence3Fp,
                NValence4Errors = nValence4Errors,
                NValence4FalsePositives = nValence4Fp,
                CulvertVertexCountErrors = culvertErrors,
                CulvertVertexCountFalsePositives = culvertFp,
                UnconnectedEndsErrors = unconnectedErrors,
                UnconnectedEndsFalsePositives = unconnectedFp,
                ValidateInflowOutflowErrors = validateErrors,
                ValidateInflowOutflowFalsePositives = validateFp
            };

            Result = MetadataTemplateService.Apply(input);
            DialogResult = true;
            Close();
        }

        private bool TryReadCount(TextBox box, string label, out int value)
        {
            string text = box.Text?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                value = 0;
                return true;
            }

            if (!int.TryParse(text, out value) || value < 0)
            {
                StatusTextBlock.Text = $"{label} must be a whole number of 0 or more.";
                box.Focus();
                box.SelectAll();
                return false;
            }
            return true;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
