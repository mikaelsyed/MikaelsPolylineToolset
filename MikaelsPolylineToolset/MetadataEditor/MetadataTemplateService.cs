using System.Collections.Generic;
using System.Text;

namespace MikaelsPolylineToolset.MetadataEditor
{
    /// <summary>One Reviewer QC check's counts for the template description.</summary>
    internal class ReviewerQcLine
    {
        public string Label { get; set; } = "";
        public int Errors { get; set; }
        public int FalsePositives { get; set; }
    }

    /// <summary>Values collected by the Fill Template prompt.</summary>
    internal class MetadataTemplateInput
    {
        public string Subarea { get; set; } = "";
        public string Process { get; set; } = "";
        public string Revision { get; set; } = "";
        public string Name { get; set; } = "";
        public string DateLastModified { get; set; } = "";
        public string Notes { get; set; } = "";

        public int IsolatedLinesErrors { get; set; }
        public int IsolatedLinesFalsePositives { get; set; }
        public int CrossingPolylinesErrors { get; set; }
        public int CrossingPolylinesFalsePositives { get; set; }
        public int OverlapPolylinesErrors { get; set; }
        public int OverlapPolylinesFalsePositives { get; set; }
        public int ConvergingEndsErrors { get; set; }
        public int ConvergingEndsFalsePositives { get; set; }
        public int NValence3Errors { get; set; }
        public int NValence3FalsePositives { get; set; }
        public int NValence4Errors { get; set; }
        public int NValence4FalsePositives { get; set; }
        public int CulvertVertexCountErrors { get; set; }
        public int CulvertVertexCountFalsePositives { get; set; }
        public int UnconnectedEndsErrors { get; set; }
        public int UnconnectedEndsFalsePositives { get; set; }
        public int ValidateInflowOutflowErrors { get; set; }
        public int ValidateInflowOutflowFalsePositives { get; set; }

        /// <summary>The two N-Valence rows are labelled with the valence the batch run actually used (3 and 4 by default).</summary>
        public string NValence3Label { get; set; } = "N-Valence Junctions (3)";
        public string NValence4Label { get; set; } = "N-Valence Junctions (4)";

        /// <summary>The Reviewer QC checks to list (only the ones that apply -- ticked in the template).</summary>
        public List<ReviewerQcLine> ReviewerLines { get; } = new List<ReviewerQcLine>();

        /// <summary>When QC output logs were loaded: one line per run (date, run time, outcome).</summary>
        public List<string> QcRuns { get; } = new List<string>();
    }

    /// <summary>
    /// Builds the standard polyline-QC metadata write-up (Title/Tags/Summary/Description/
    /// Credits) from a MetadataTemplateInput. The result is dropped into the editor's fields,
    /// which stay fully editable afterward -- this is a starting point, not a locked format.
    /// </summary>
    internal static class MetadataTemplateService
    {
        public static readonly string[] ProcessOptions = { "Merged", "GMI_FCoded", "3D_ZValues", "Breaklines" };

        public static ShapefileMetadata Apply(MetadataTemplateInput t)
        {
            string title = $"{t.Subarea}_{t.Process}_Revision_{t.Revision}";
            string tags = t.Process;
            string summary = $"The finalized, {t.Process} polyline shapefile for {t.Subarea}. This is revision {t.Revision}. Completed by {t.Name} on {t.DateLastModified}.";

            var lines = new List<string>
            {
                $"- Last Edited By: {t.Name}",
                $"- Last Date Edited: {t.DateLastModified}",
                "- QC Tool Output:",
                QcLine("Isolated Lines", t.IsolatedLinesErrors, t.IsolatedLinesFalsePositives),
                QcLine("Crossing Polylines", t.CrossingPolylinesErrors, t.CrossingPolylinesFalsePositives),
                QcLine("Overlap Polylines", t.OverlapPolylinesErrors, t.OverlapPolylinesFalsePositives),
                QcLine("Converging Ends", t.ConvergingEndsErrors, t.ConvergingEndsFalsePositives),
                QcLine(t.NValence3Label, t.NValence3Errors, t.NValence3FalsePositives),
                QcLine(t.NValence4Label, t.NValence4Errors, t.NValence4FalsePositives),
                QcLine("Vertex Count Check", t.CulvertVertexCountErrors, t.CulvertVertexCountFalsePositives),
                QcLine("Unconnected Ends", t.UnconnectedEndsErrors, t.UnconnectedEndsFalsePositives),
                QcLine("Validate Inflow/Outflow", t.ValidateInflowOutflowErrors, t.ValidateInflowOutflowFalsePositives),
            };

            if (t.ReviewerLines.Count > 0)
            {
                lines.Add("- Reviewer QC Tool Output:");
                foreach (var r in t.ReviewerLines)
                    lines.Add(QcLine(r.Label, r.Errors, r.FalsePositives));
            }

            if (t.QcRuns.Count > 0)
            {
                lines.Add("- QC Runs:");
                foreach (var run in t.QcRuns)
                    lines.Add($"\t- {run}");
            }

            if (!string.IsNullOrWhiteSpace(t.Notes))
                lines.Add($"- {t.Notes.Trim()}");

            return new ShapefileMetadata
            {
                Title = title,
                Tags = tags,
                Summary = summary,
                Description = string.Join(System.Environment.NewLine, lines),
                Credits = t.Name,
                Constraints = ""
            };
        }

        private static string QcLine(string label, int errors, int falsePositives) =>
            $"\t- {label}: {errors} | {falsePositives} (False Positives)";
    }
}
