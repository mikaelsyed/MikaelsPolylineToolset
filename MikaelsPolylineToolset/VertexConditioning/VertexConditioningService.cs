using ArcGIS.Core.Data;
using ArcGIS.Core.Data.Raster;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Editing;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using MikaelsPolylineToolset.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MikaelsPolylineToolset.VertexConditioning
{
    /// <summary>
    /// Reconditions flowline vertices against a spacing spec (min/max distance between
    /// consecutive vertices) and a DEM-derived monotonic-elevation rule (walking start to
    /// end/downstream, every vertex's DEM elevation must be &lt;= the previous vertex's).
    ///
    /// Spacing violations are fixed by resampling along the line's own existing path only --
    /// too-close vertices are dropped, too-long segments are split with points interpolated
    /// on that same straight segment -- so the path shape is unchanged except where a
    /// violation actually forces it. Elevation violations are fixed by relocating the
    /// offending vertex to the nearest DEM cell that satisfies the rule, searched within a
    /// bounded radius, so no single vertex can drift further than that radius.
    ///
    /// The line's true start and end vertices are never added, removed, or moved by either
    /// pass, since other flowlines and junctions may be snapped to them; a violation at an
    /// endpoint is flagged for manual review instead of being auto-fixed.
    ///
    /// Must be called from within QueuedTask.Run.
    /// </summary>
    internal static class VertexConditioningService
    {
        public class FeatureResult
        {
            public long Oid;
            public Polyline OriginalGeometry;
            public Polyline NewGeometry;
            public int OriginalVertexCount;
            public int NewVertexCount;
            public int VerticesAdded;
            public int VerticesRemoved;
            public int VerticesMovedForElevation;
            public double MaxVertexShift;
            public bool Changed;
            public bool Skipped;
            public string SkipReason;
            public List<string> Flags { get; } = new List<string>();
        }

        public static async Task<(List<FeatureResult> Results, List<string> Messages)> ComputeAsync(
            FeatureLayer lineLayer, RasterLayer rasterLayer,
            double minSpacing, double maxSpacing, double searchRadius, bool selectedOnly, QcProgress progress = null)
        {
            var messages = new List<string>();
            var results = new List<FeatureResult>();

            await QueuedTask.Run(() =>
            {
                var raster = rasterLayer.GetRaster();
                var rasterSR = raster.GetSpatialReference();
                var noDataValue = raster.GetNoDataValue();
                int width = raster.GetWidth();
                int height = raster.GetHeight();
                var (cellX, cellY) = raster.GetMeanCellSize();
                int radiusCols = Math.Max(1, (int)Math.Ceiling(searchRadius / Math.Max(cellX, 1e-9)));
                int radiusRows = Math.Max(1, (int)Math.Ceiling(searchRadius / Math.Max(cellY, 1e-9)));
                int ringLimit = Math.Max(radiusCols, radiusRows);

                bool IsNoData(object val)
                {
                    if (val == null) return true;
                    if (noDataValue == null) return false;
                    try { return Math.Abs(Convert.ToDouble(val) - Convert.ToDouble(noDataValue)) < 1e-9; }
                    catch { return val.Equals(noDataValue); }
                }

                // Samples the DEM directly under a point already expressed in the raster's own
                // spatial reference; returns null if off-raster or NoData.
                double? SampleAtRasterPoint(double x, double y)
                {
                    var (col, row) = raster.MapToPixel(x, y);
                    if (col < 0 || col >= width || row < 0 || row >= height) return null;
                    var val = raster.GetPixelValue(0, col, row);
                    if (IsNoData(val)) return null;
                    return Convert.ToDouble(val);
                }

                // Searches outward in pixel rings from (x,y) [raster SR] for the closest DEM cell
                // whose value is <= referenceElevation, within searchRadius map units. Falls back
                // to the lowest-elevation cell found in radius if nothing satisfies the rule.
                (double X, double Y, double Elevation, bool Satisfied)? FindBestCell(double x, double y, double referenceElevation)
                {
                    var (baseCol, baseRow) = raster.MapToPixel(x, y);
                    (double X, double Y, double Elevation)? best = null;
                    double bestGoodDist = double.MaxValue;
                    (double X, double Y, double Elevation)? fallback = null;
                    double bestFallbackElev = double.MaxValue, bestFallbackDist = double.MaxValue;

                    for (int ring = 0; ring <= ringLimit; ring++)
                    {
                        for (int dc = -ring; dc <= ring; dc++)
                        {
                            for (int dr = -ring; dr <= ring; dr++)
                            {
                                if (Math.Max(Math.Abs(dc), Math.Abs(dr)) != ring) continue;
                                int col = baseCol + dc, row = baseRow + dr;
                                if (col < 0 || col >= width || row < 0 || row >= height) continue;
                                var val = raster.GetPixelValue(0, col, row);
                                if (IsNoData(val)) continue;
                                double elev = Convert.ToDouble(val);
                                var (mx, my) = raster.PixelToMap(col, row);
                                double dist = Math.Sqrt((mx - x) * (mx - x) + (my - y) * (my - y));
                                if (dist > searchRadius) continue;

                                if (elev <= referenceElevation)
                                {
                                    if (dist < bestGoodDist) { bestGoodDist = dist; best = (mx, my, elev); }
                                }
                                else if (elev < bestFallbackElev || (elev == bestFallbackElev && dist < bestFallbackDist))
                                {
                                    bestFallbackElev = elev; bestFallbackDist = dist; fallback = (mx, my, elev);
                                }
                            }
                        }
                        if (best != null) break; // nearest ring with a satisfying cell wins
                    }

                    if (best != null) return (best.Value.X, best.Value.Y, best.Value.Elevation, true);
                    if (fallback != null) return (fallback.Value.X, fallback.Value.Y, fallback.Value.Elevation, false);
                    return null;
                }

                MapPoint ToRasterSR(MapPoint p) =>
                    rasterSR == null ? p : GeometryEngine.Instance.Project(p, rasterSR) as MapPoint;

                MapPoint FromRasterSR(double x, double y, SpatialReference lineSR)
                {
                    var pt = MapPointBuilderEx.CreateMapPoint(x, y, rasterSR);
                    return lineSR == null ? pt : GeometryEngine.Instance.Project(pt, lineSR) as MapPoint;
                }

                QueryFilter filter = null;
                if (selectedOnly)
                {
                    var selOids = lineLayer.GetSelection().GetObjectIDs();
                    filter = new QueryFilter { ObjectIDs = selOids };
                }

                progress?.Begin("Reconditioning polylines", selectedOnly ? lineLayer.GetSelection().GetCount() : QcRunUi.CountNow(lineLayer), 0, 1);
                using var cursor = lineLayer.Search(filter);
                int processed = 0;
                while (cursor.MoveNext())
                {
                    using var feature = (Feature)cursor.Current;
                    var oid = feature.GetObjectID();
                    progress?.Tick(oid);
                    if (feature.GetShape() is not Polyline original || original.IsEmpty)
                        continue;
                    processed++;

                    var result = new FeatureResult { Oid = oid, OriginalGeometry = original, OriginalVertexCount = original.PointCount };

                    if (original.PartCount > 1)
                    {
                        result.Skipped = true;
                        result.SkipReason = "multipart geometry not supported";
                        results.Add(result);
                        continue;
                    }

                    var lineSR = original.SpatialReference;
                    var pts = original.Points.ToList();
                    if (pts.Count < 2)
                    {
                        result.Skipped = true;
                        result.SkipReason = "fewer than 2 vertices";
                        results.Add(result);
                        continue;
                    }

                    // ---- Spacing pass: decimate too-close vertices, then densify too-long
                    // segments. The true start/end vertex is always preserved exactly.
                    var decimated = new List<MapPoint> { pts[0] };
                    for (int i = 1; i < pts.Count - 1; i++)
                    {
                        if (GeometryEngine.Instance.Distance(decimated[^1], pts[i]) >= minSpacing)
                            decimated.Add(pts[i]);
                    }
                    decimated.Add(pts[^1]);
                    int removed = pts.Count - decimated.Count;

                    if (decimated.Count >= 2)
                    {
                        double lastSeg = GeometryEngine.Instance.Distance(decimated[^2], decimated[^1]);
                        if (lastSeg < minSpacing)
                            result.Flags.Add($"Final segment into the end vertex is {lastSeg:0.##}m, shorter than the {minSpacing:0.##}m minimum; end vertex was kept in place to preserve connectivity.");
                    }

                    var densified = new List<MapPoint> { decimated[0] };
                    int added = 0;
                    for (int i = 1; i < decimated.Count; i++)
                    {
                        var p0 = decimated[i - 1];
                        var p1 = decimated[i];
                        double segLen = GeometryEngine.Instance.Distance(p0, p1);
                        if (segLen > maxSpacing)
                        {
                            int n = (int)Math.Ceiling(segLen / maxSpacing);
                            for (int k = 1; k < n; k++)
                            {
                                double t = (double)k / n;
                                densified.Add(MapPointBuilderEx.CreateMapPoint(
                                    p0.X + (p1.X - p0.X) * t, p0.Y + (p1.Y - p0.Y) * t, lineSR));
                                added++;
                            }
                        }
                        densified.Add(p1);
                    }

                    // ---- Elevation pass: walk start-to-end, relocating any interior vertex
                    // whose DEM elevation exceeds the previous vertex's, within the search radius.
                    var finalPts = new List<MapPoint>(densified);
                    double? referenceElev = null;
                    int movedForElevation = 0;
                    double maxShift = 0;

                    for (int i = 0; i < finalPts.Count; i++)
                    {
                        var p = finalPts[i];
                        var pInRasterSR = ToRasterSR(p);
                        double? elev = pInRasterSR == null ? null : SampleAtRasterPoint(pInRasterSR.X, pInRasterSR.Y);

                        if (elev == null)
                        {
                            result.Flags.Add($"Vertex {i} has no DEM coverage (off-raster or NoData); elevation check skipped for this vertex.");
                            continue; // reference elevation carries forward unchanged
                        }

                        if (i == 0)
                        {
                            referenceElev = elev;
                            continue;
                        }

                        if (referenceElev == null || elev <= referenceElev)
                        {
                            referenceElev = elev;
                            continue;
                        }

                        // Violation: elev > referenceElev
                        bool isEndVertex = i == finalPts.Count - 1;
                        if (isEndVertex)
                        {
                            result.Flags.Add($"End vertex elevation ({elev:0.##}) exceeds the previous vertex's ({referenceElev:0.##}); left in place to preserve connectivity.");
                            referenceElev = elev;
                            continue;
                        }

                        var fix = FindBestCell(pInRasterSR.X, pInRasterSR.Y, referenceElev.Value);
                        if (fix == null)
                        {
                            result.Flags.Add($"Vertex {i}: elevation ({elev:0.##}) exceeds previous ({referenceElev:0.##}); no usable DEM cell found within {searchRadius:0.##}m search radius, left unmoved.");
                            referenceElev = elev;
                            continue;
                        }

                        var newPt = FromRasterSR(fix.Value.X, fix.Value.Y, lineSR);
                        double shift = GeometryEngine.Instance.Distance(p, newPt);
                        maxShift = Math.Max(maxShift, shift);
                        finalPts[i] = newPt;
                        movedForElevation++;
                        referenceElev = fix.Value.Elevation;

                        if (!fix.Value.Satisfied)
                            result.Flags.Add($"Vertex {i}: best-effort move (elevation {fix.Value.Elevation:0.##}) within {searchRadius:0.##}m still exceeds previous; flagged for review.");
                    }

                    result.NewGeometry = PolylineBuilderEx.CreatePolyline(finalPts, lineSR);
                    result.NewVertexCount = finalPts.Count;
                    result.VerticesAdded = added;
                    result.VerticesRemoved = removed;
                    result.VerticesMovedForElevation = movedForElevation;
                    result.MaxVertexShift = maxShift;
                    result.Changed = added > 0 || removed > 0 || movedForElevation > 0;
                    results.Add(result);
                }

                messages.Add(selectedOnly
                    ? $"Processed {processed} selected polyline(s)."
                    : $"Processed {processed} polyline(s).");
            });

            int skipped = results.Count(r => r.Skipped);
            int changed = results.Count(r => r.Changed && !r.Skipped);
            int totalAdded = results.Sum(r => r.VerticesAdded);
            int totalRemoved = results.Sum(r => r.VerticesRemoved);
            int totalMoved = results.Sum(r => r.VerticesMovedForElevation);
            int flaggedFeatures = results.Count(r => r.Flags.Count > 0);

            if (skipped > 0)
                messages.Add($"Skipped {skipped} polyline(s): " + string.Join("; ",
                    results.Where(r => r.Skipped).Select(r => $"OID {r.Oid} ({r.SkipReason})")));
            messages.Add(changed > 0
                ? $"{changed} polyline(s) need changes: {totalAdded} vertex(es) added, {totalRemoved} removed, {totalMoved} moved to fix elevation."
                : "No polylines need changes.");
            if (flaggedFeatures > 0)
            {
                messages.Add($"{flaggedFeatures} polyline(s) flagged for review:");
                foreach (var r in results.Where(r => r.Flags.Count > 0))
                {
                    messages.Add($"  OID {r.Oid}:");
                    foreach (var f in r.Flags) messages.Add($"    - {f}");
                }
            }

            return (results, messages);
        }

        public static async Task<(bool Success, int Count, string Message)> ApplyAsync(
            FeatureLayer lineLayer, List<FeatureResult> results)
        {
            var toApply = results.Where(r => r.Changed && !r.Skipped).ToList();
            if (toApply.Count == 0)
                return (true, 0, "Nothing to apply.");

            return await QueuedTask.Run(() =>
            {
                var op = new EditOperation { Name = "Recondition Polyline Vertices" };
                foreach (var r in toApply)
                    op.Modify(lineLayer, r.Oid, r.NewGeometry);

                bool ok = op.Execute();
                return ok
                    ? (true, toApply.Count, $"Applied edits to {toApply.Count} polyline(s).")
                    : (false, 0, "Edit operation failed: " + (op.ErrorMessage ?? "unknown error"));
            });
        }
    }
}
