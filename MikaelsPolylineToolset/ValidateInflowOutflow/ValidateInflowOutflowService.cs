using ArcGIS.Core.Data;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using MikaelsPolylineToolset.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MikaelsPolylineToolset.ValidateInflowOutflow
{
    /// <summary>
    /// For every point where a polyline touches a polygon boundary, counts how
    /// many distinct polylines connect there and finds the nearest polygon's type, then flags:
    ///   OK     -- 2+ polylines connect (a normal pass-through / confluence at the boundary)
    ///   OCEAN  -- exactly 1 polyline, and the nearest polygon is the configured boundary type
    ///   CHECK  -- exactly 1 polyline, and the nearest polygon is NOT the configured boundary type
    /// Ported from TotalFlowlineQCScript.pyt's "Validate Point Connectivity" step. Reuses
    /// PolylineIntersectionCore for the intersection points and per-point line counts (so,
    /// unlike the original, there's no separate SpatialJoin/search-radius step -- a line only
    /// counts as "connected" if it actually produced an intersection point there).
    ///
    /// Split into Compute/Write so callers that need a false-positive gate (count first,
    /// decide whether to write) -- like the Run Flowline QC Checks master tool -- can do so
    /// without writing output twice. RunAsync is a thin wrapper preserving the original
    /// single-call behavior for the standalone tool window.
    /// </summary>
    internal static class ValidateInflowOutflowService
    {
        public class PointResult
        {
            public MapPoint Point;
            public int LineCount;
            public string Status;
            public string PolyType;
            public double NearDist;
        }

        public static async Task<(List<PointResult> Results, SpatialReference Sr, List<string> Messages)> ComputeAsync(
            FeatureLayer lineLayer, FeatureLayer polygonLayer, double tolerance, string typeField, string oceanValue)
        {
            var messages = new List<string>();

            List<PolylineIntersectionCore.IntersectionCluster> clusters = null;
            SpatialReference sr = null;
            var polys = new List<(Polygon Geom, string TypeValue)>();
            var results = new List<PointResult>();

            await QueuedTask.Run(() =>
            {
                clusters = PolylineIntersectionCore.ComputeIntersectionClusters(
                    lineLayer, polygonLayer, tolerance, msg => messages.Add(msg), out sr);

                using var cursor = polygonLayer.Search(null);
                while (cursor.MoveNext())
                {
                    using var f = (Feature)cursor.Current;
                    if (f.GetShape() is not Polygon pg || pg.IsEmpty) continue;
                    if (pg.SpatialReference != null)
                        pg = (Polygon)GeometryEngine.Instance.Project(pg, sr);
                    var val = f[typeField];
                    polys.Add((pg, val?.ToString()?.Trim() ?? ""));
                }

                if (clusters.Count == 0 || polys.Count == 0)
                    return;

                foreach (var cluster in clusters)
                {
                    string nearestType = "";
                    double nearestDist = double.MaxValue;
                    foreach (var (pg, typeVal) in polys)
                    {
                        double d = GeometryEngine.Instance.Distance(pg, cluster.Point);
                        if (d < nearestDist)
                        {
                            nearestDist = d;
                            nearestType = typeVal;
                        }
                    }

                    int count = cluster.LineOids.Count;
                    string status;
                    if (count >= 2)
                        status = "OK";
                    else if (!string.IsNullOrEmpty(nearestType) && string.Equals(nearestType, oceanValue, StringComparison.OrdinalIgnoreCase))
                        status = "OCEAN";
                    else
                        status = "CHECK";

                    results.Add(new PointResult { Point = cluster.Point, LineCount = count, Status = status, PolyType = nearestType, NearDist = nearestDist });
                }
            });

            if (clusters.Count == 0)
                messages.Add("No intersection points found; output not created.");
            else if (polys.Count == 0)
                messages.Add("Polygon layer has no features; cannot determine nearest polygon type.");

            return (results, sr, messages);
        }

        public static async Task<(bool Success, List<string> Messages)> WriteOutputAsync(
            List<PointResult> results, SpatialReference sr, string outputPath)
        {
            var messages = new List<string>();

            if (results.Count == 0)
            {
                messages.Add("Nothing to write; output not created.");
                return (true, messages);
            }

            int okCount = results.Count(r => r.Status == "OK");
            int oceanCount = results.Count(r => r.Status == "OCEAN");
            int checkCount = results.Count(r => r.Status == "CHECK");

            var (workspace, name) = GdbOutputHelper.SplitOutputPath(outputPath);
            var (createOk, createMsg) = await GdbOutputHelper.CreateFeatureClassAsync(workspace, name, "POINT", sr);
            if (!createOk)
            {
                messages.Add(createMsg);
                return (false, messages);
            }

            foreach (var (fName, fType, len) in new (string, string, int)[]
            {
                ("LINE_COUNT", "LONG", 0),
                ("STATUS", "TEXT", 10),
                ("POLY_TYPE", "TEXT", 100),
                ("NEAR_DIST", "DOUBLE", 0)
            })
            {
                var (fieldOk, fieldMsg) = await GdbOutputHelper.AddFieldAsync(outputPath, fName, fType, len);
                if (!fieldOk)
                {
                    messages.Add(fieldMsg);
                    return (false, messages);
                }
            }

            await QueuedTask.Run(() =>
            {
                var (gdb, fc) = GdbOutputHelper.OpenFileGdbFeatureClass(workspace, name);
                using (gdb)
                using (fc)
                using (var insertCursor = fc.CreateInsertCursor())
                {
                    foreach (var r in results)
                    {
                        using var buffer = fc.CreateRowBuffer();
                        buffer["Shape"] = r.Point;
                        buffer["LINE_COUNT"] = r.LineCount;
                        buffer["STATUS"] = r.Status;
                        buffer["POLY_TYPE"] = r.PolyType;
                        buffer["NEAR_DIST"] = r.NearDist;
                        insertCursor.Insert(buffer);
                    }
                    insertCursor.Flush();
                }
            });

            messages.Add($"Validated {results.Count} point(s): OK={okCount}, OCEAN={oceanCount}, CHECK={checkCount}");
            messages.Add($"Created output at {outputPath}");

            return (true, messages);
        }

        public static async Task<(bool Success, int Count, int CheckCount, List<string> Messages)> RunAsync(
            FeatureLayer lineLayer, FeatureLayer polygonLayer, double tolerance,
            string typeField, string oceanValue, string outputPath)
        {
            var (results, sr, computeMessages) = await ComputeAsync(lineLayer, polygonLayer, tolerance, typeField, oceanValue);
            int checkCount = results.Count(r => r.Status == "CHECK");

            if (results.Count == 0)
                return (true, 0, 0, computeMessages);

            var (success, writeMessages) = await WriteOutputAsync(results, sr, outputPath);
            computeMessages.AddRange(writeMessages);
            return (success, results.Count, checkCount, computeMessages);
        }
    }
}
