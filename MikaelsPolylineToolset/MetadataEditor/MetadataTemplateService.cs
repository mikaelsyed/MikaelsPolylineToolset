using System.Text;

namespace MikaelsPolylineToolset.MetadataEditor
{
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

            var desc = new StringBuilder();
            desc.AppendLine($"- Last Edited By: {t.Name}");
            desc.AppendLine($"- Last Date Edited: {t.DateLastModified}");
            desc.AppendLine("- QC Tool Output:");
            desc.AppendLine(QcLine("Isolated Lines", t.IsolatedLinesErrors, t.IsolatedLinesFalsePositives));
            desc.AppendLine(QcLine("Crossing Polylines", t.CrossingPolylinesErrors, t.CrossingPolylinesFalsePositives));
            desc.AppendLine(QcLine("Overlap Polylines", t.OverlapPolylinesErrors, t.OverlapPolylinesFalsePositives));
            desc.AppendLine(QcLine("Converging Ends", t.ConvergingEndsErrors, t.ConvergingEndsFalsePositives));
            desc.AppendLine(QcLine("N-Valence Junctions (3)", t.NValence3Errors, t.NValence3FalsePositives));
            desc.AppendLine(QcLine("N-Valence Junctions (4)", t.NValence4Errors, t.NValence4FalsePositives));
            desc.AppendLine(QcLine("Vertex Count Check", t.CulvertVertexCountErrors, t.CulvertVertexCountFalsePositives));
            desc.AppendLine(QcLine("Unconnected Ends", t.UnconnectedEndsErrors, t.UnconnectedEndsFalsePositives));
            desc.Append(QcLine("Validate Inflow/Outflow", t.ValidateInflowOutflowErrors, t.ValidateInflowOutflowFalsePositives));

            if (!string.IsNullOrWhiteSpace(t.Notes))
            {
                desc.AppendLine();
                desc.Append($"- {t.Notes.Trim()}");
            }

            return new ShapefileMetadata
            {
                Title = title,
                Tags = tags,
                Summary = summary,
                Description = desc.ToString(),
                Credits = t.Name,
                Constraints = ""
            };
        }

        private static string QcLine(string label, int errors, int falsePositives) =>
            $"\t- {label}: {errors} | {falsePositives} (False Positives)";
    }
}
