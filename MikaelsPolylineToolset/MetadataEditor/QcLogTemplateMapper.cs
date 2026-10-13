using MikaelsPolylineToolset.Common;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace MikaelsPolylineToolset.MetadataEditor
{
    /// <summary>
    /// Turns the QC output log written by the Polyline QC / Reviewer QC batch tools into the numbers the
    /// metadata template needs: for one layer in the log, each check's error count and how many of those
    /// were expected false positives.
    /// </summary>
    internal static class QcLogTemplateMapper
    {
        /// <summary>Reviewer checks in the order they appear in the template. Keys match the batch tool's step keys.</summary>
        public static readonly (string Key, string Label)[] ReviewerChecks =
        {
            ("PartCount", "Evaluate Part Count"),
            ("VertexTolerance", "Vertex Tolerance Check"),
            ("OverlapSliver", "Polygon Overlap is Sliver"),
            ("GapSliver", "Polygon Gap is Sliver"),
            ("PolygonHoles", "Find Polygons with Holes"),
            ("DuplicateFeature", "Duplicate Feature"),
            ("UniqueFieldValue", "Unique Field Value"),
            ("CheckGeometry", "Check Geometry"),
            ("ZeroLengthArea", "Zero Length / Zero Area"),
        };

        /// <summary>A layer the log has results for.</summary>
        public class LayerChoice
        {
            public string Name { get; set; }
            public string Role { get; set; }
            public string Dataset { get; set; }
            public string Source { get; set; }
            public override string ToString() => string.IsNullOrEmpty(Role) ? Name : $"{Name} ({Role.ToLowerInvariant()})";
        }

        /// <summary>One check's numbers for the chosen layer.</summary>
        public class Entry
        {
            public string Key { get; set; }
            public string Label { get; set; }
            public bool Filled { get; set; }
            public int Errors { get; set; }
            public int FalsePositives { get; set; }
            /// <summary>Why the numbers could not be filled (skipped, not run...), or a caution when they were.</summary>
            public string Note { get; set; } = "";
        }

        public static List<LayerChoice> LayerChoices(QcRunLog log)
        {
            var choices = new List<LayerChoice>();
            foreach (var layer in log.Layers.Where(l => l.Role == "Polylines" || l.Role == "Polygons"))
            {
                string dataset = log.Steps.SelectMany(s => s.Layers).FirstOrDefault(r => r.Layer == layer.Name && !string.IsNullOrEmpty(r.Dataset))?.Dataset;
                if (string.IsNullOrEmpty(dataset)) dataset = Path.GetFileName(layer.Source ?? "");
                choices.Add(new LayerChoice { Name = layer.Name, Role = layer.Role, Dataset = dataset, Source = layer.Source });
            }
            return choices;
        }

        /// <summary>The log layer that is the feature class being edited, or the first one if none match.</summary>
        public static LayerChoice PickDefault(List<LayerChoice> choices, string targetPath)
        {
            if (choices.Count == 0) return null;
            if (string.IsNullOrWhiteSpace(targetPath)) return choices[0];

            string fileName = Path.GetFileName(targetPath.TrimEnd('\\', '/'));
            string withoutExt = Path.GetFileNameWithoutExtension(fileName);

            LayerChoice Match(Func<LayerChoice, bool> test) => choices.FirstOrDefault(test);
            return Match(c => string.Equals(c.Source, targetPath, StringComparison.OrdinalIgnoreCase))
                ?? Match(c => string.Equals(c.Dataset, fileName, StringComparison.OrdinalIgnoreCase) || string.Equals(c.Dataset, withoutExt, StringComparison.OrdinalIgnoreCase))
                ?? Match(c => string.Equals(c.Name, fileName, StringComparison.OrdinalIgnoreCase) || string.Equals(c.Name, withoutExt, StringComparison.OrdinalIgnoreCase))
                ?? choices[0];
        }

        /// <summary>Reads every step of the log for one layer.</summary>
        public static Dictionary<string, Entry> Extract(QcRunLog log, string layerName)
        {
            var entries = new Dictionary<string, Entry>();
            foreach (var step in log.Steps)
            {
                var entry = new Entry { Key = step.Key, Label = step.Name };
                entries[step.Key] = entry;

                if (step.Skipped)
                {
                    entry.Note = step.Status;
                    continue;
                }

                var result = step.Layers.FirstOrDefault(r => string.Equals(r.Layer, layerName, StringComparison.OrdinalIgnoreCase));
                if (result == null)
                {
                    entry.Note = step.Status == QcStatus.NotRun || step.Status == QcStatus.Cancelled
                        ? "not run (" + step.Status.ToLowerInvariant() + ")"
                        : "not run on this layer";
                    continue;
                }
                if (!result.IsValid)
                {
                    entry.Note = "could not be checked on this layer";
                    continue;
                }

                entry.Filled = true;
                entry.Errors = result.Found;
                entry.FalsePositives = result.FalsePositives;
                if (step.Status == QcStatus.FpMismatch)
                    entry.Note = $"false-positive count did not match (expected {step.ExpectedFp:N0}); false positives left at 0";
            }
            return entries;
        }

        /// <summary>The valence the N-Valence step used (for the template's "(3)" / "(4)" labels), if the log recorded it.</summary>
        public static int? ValenceOf(QcRunLog log, string stepKey)
        {
            var step = log.Steps.FirstOrDefault(s => s.Key == stepKey);
            if (step == null || step.Skipped) return null;
            var m = Regex.Match(step.Parameters ?? "", @"Valence = (\d+)");
            return m.Success ? int.Parse(m.Groups[1].Value) : (int?)null;
        }

        /// <summary>One line describing the run, e.g. for the template description.</summary>
        public static string DescribeRun(QcRunLog log)
        {
            var t = TimeSpan.FromSeconds(Math.Max(0, log.TotalSeconds));
            string run = $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
            return $"{log.ToolbarName} run {log.Started:M/d/yyyy h:mm tt} (run time {run}, {log.Outcome.ToLowerInvariant()})";
        }
    }
}
