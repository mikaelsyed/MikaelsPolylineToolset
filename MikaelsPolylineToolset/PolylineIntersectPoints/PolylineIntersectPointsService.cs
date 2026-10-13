using ArcGIS.Core.Data;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using MikaelsPolylineToolset.Common;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MikaelsPolylineToolset.PolylineIntersectPoints
{
    /// <summary>
    /// Generates a point feature class wherever flowlines touch a polygon layer's boundary
    /// (crossings AND touch/tangent cases). Ported from TotalFlowlineQCScript.pyt's "Line
    /// Polygon Pairwise Intersect" step, re-expressed as a direct in-memory geometry
    /// computation (GeometryEngine intersection at each line/polygon-boundary pair) instead
    /// of chained PairwiseIntersect/PolygonToLine/Merge/DeleteIdentical GP calls -- this also
    /// gets the connected-line count at each point for free, as a bonus LINE_COUNT field.
    ///
    /// Split into Compute/Write so callers that need a false-positive gate (count first,
    /// decide whether to write) -- like the Run Flowline QC Checks master tool -- can do so
    /// without writing output twice. RunAsync is a thin wrapper preserving the original
    /// single-call behavior for the standalone tool window.
    /// </summary>
    internal static class PolylineIntersectPointsService
    {
        public static async Task<(List<PolylineIntersectionCore.IntersectionCluster> Clusters, SpatialReference Sr, List<string> Messages)> ComputeAsync(
            FeatureLayer lineLayer, FeatureLayer polygonLayer, double tolerance, QcProgress progress = null)
        {
            var messages = new List<string>();
            List<PolylineIntersectionCore.IntersectionCluster> clusters = null;
            SpatialReference sr = null;
            await QueuedTask.Run(() =>
            {
                clusters = PolylineIntersectionCore.ComputeIntersectionClusters(
                    lineLayer, polygonLayer, tolerance, msg => messages.Add(msg), out sr, progress, 0.95);
            });
            return (clusters, sr, messages);
        }

        public static async Task<(bool Success, List<string> Messages)> WriteOutputAsync(
            List<PolylineIntersectionCore.IntersectionCluster> clusters, SpatialReference sr, string outputPath)
        {
            var messages = new List<string>();

            if (clusters.Count == 0)
            {
                messages.Add("No intersection points found; output not created.");
                return (true, messages);
            }

            var (workspace, name) = GdbOutputHelper.SplitOutputPath(outputPath);
            var (createOk, createMsg) = await GdbOutputHelper.CreateFeatureClassAsync(workspace, name, "POINT", sr);
            if (!createOk)
            {
                messages.Add(createMsg);
                return (false, messages);
            }

            var (fieldOk, fieldMsg) = await GdbOutputHelper.AddFieldAsync(outputPath, "LINE_COUNT", "LONG");
            if (!fieldOk)
            {
                messages.Add(fieldMsg);
                return (false, messages);
            }

            await QueuedTask.Run(() =>
            {
                var (gdb, fc) = GdbOutputHelper.OpenFileGdbFeatureClass(workspace, name);
                using (gdb)
                using (fc)
                using (var insertCursor = fc.CreateInsertCursor())
                {
                    foreach (var cluster in clusters)
                    {
                        using var buffer = fc.CreateRowBuffer();
                        buffer["Shape"] = cluster.Point;
                        buffer["LINE_COUNT"] = cluster.LineOids.Count;
                        insertCursor.Insert(buffer);
                    }
                    insertCursor.Flush();
                }
            });

            messages.Add($"Created {clusters.Count} intersection point(s) at {outputPath}");
            return (true, messages);
        }

        public static async Task<(bool Success, int Count, List<string> Messages)> RunAsync(
            FeatureLayer lineLayer, FeatureLayer polygonLayer, double tolerance, string outputPath, QcProgress progress = null)
        {
            var (clusters, sr, computeMessages) = await ComputeAsync(lineLayer, polygonLayer, tolerance, progress);
            progress?.Working("Writing the output feature class", 0.95, 0.05);
            var (success, writeMessages) = await WriteOutputAsync(clusters, sr, outputPath);
            computeMessages.AddRange(writeMessages);
            return (success, clusters.Count, computeMessages);
        }
    }
}
