using MikaelsPolylineToolset.MetadataEditor;
using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace MikaelsPolylineToolset.MetadataExport
{
    /// <summary>Reads a feature class's metadata and writes it out as a plain-text (.txt) file.</summary>
    internal static class MetadataExportService
    {
        public static async Task<(bool Success, string Message)> ExportAsync(string featureClassPath, string outputFilePath)
        {
            var (success, _, metadata, loadMessage) = await MetadataEditorService.LoadAsync(featureClassPath);
            if (!success)
                return (false, loadMessage);

            string text = BuildText(featureClassPath, metadata);

            try
            {
                await Task.Run(() => File.WriteAllText(outputFilePath, text));
            }
            catch (Exception ex)
            {
                return (false, $"Could not write '{outputFilePath}': {ex.Message}");
            }

            return (true, $"Metadata exported to {outputFilePath}");
        }

        private static string BuildText(string featureClassPath, ShapefileMetadata metadata)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Metadata Export");
            sb.AppendLine($"Source: {featureClassPath}");
            sb.AppendLine($"Exported: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();

            AppendField(sb, "Title", metadata.Title);
            AppendField(sb, "Tags", metadata.Tags);
            AppendField(sb, "Summary", metadata.Summary);
            AppendField(sb, "Description", metadata.Description);
            AppendField(sb, "Credits", metadata.Credits);
            AppendField(sb, "Use Limitation", metadata.Constraints);

            return sb.ToString();
        }

        private static void AppendField(StringBuilder sb, string label, string value)
        {
            sb.AppendLine($"{label}:");
            sb.AppendLine(string.IsNullOrWhiteSpace(value) ? "(none)" : value);
            sb.AppendLine();
        }
    }
}
