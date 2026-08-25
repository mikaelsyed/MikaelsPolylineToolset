using ArcGIS.Core.Data;
using ArcGIS.Core.Data.Raster;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using MikaelsPolylineToolset.Common;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MikaelsPolylineToolset.RasterOverlap
{
    /// <summary>
    /// For each input line, samples a raster along its length at a fixed interval and
    /// calculates what percentage of sample points land on a valid (non-NoData) raster cell
    /// vs. off the raster entirely. Ported from TrainingToolGMIoverlap.pyt (Calculate Line
    /// Overlap with Raster). Reads pixel values directly via Raster.GetPixelValue rather than
    /// loading the whole raster into memory, so it isn't affected by the "clip first or it
    /// runs out of memory" caveat the original numpy-array-based script had.
    /// </summary>
    internal static class RasterOverlapService
    {
        private class LineResult
        {
            public Polyline Geometry;
            public int Total;
            public int On;
            public int Off;
            public double PctOn;
            public double PctOff;
        }

        public static async Task<(bool Success, int Count, List<string> Messages)> RunAsync(
            FeatureLayer lineLayer, RasterLayer rasterLayer, double interval, string outputPath)
        {
            var messages = new List<string>();
            var results = new List<LineResult>();
            SpatialReference lineSR = null;

            await QueuedTask.Run(() =>
            {
                var raster = rasterLayer.GetRaster();
                var rasterSR = raster.GetSpatialReference();
                var noDataValue = raster.GetNoDataValue();
                int width = raster.GetWidth();
                int height = raster.GetHeight();

                bool IsOnRaster(double x, double y)
                {
                    var (col, row) = raster.MapToPixel(x, y);
                    if (col < 0 || col >= width || row < 0 || row >= height) return false;
                    var val = raster.GetPixelValue(0, col, row);
                    if (val == null) return false;
                    if (noDataValue == null) return true;
                    try { return Math.Abs(Convert.ToDouble(val) - Convert.ToDouble(noDataValue)) > 1e-9; }
                    catch { return !val.Equals(noDataValue); }
                }

                List<MapPoint> SamplePoints(Polyline geom)
                {
                    var pts = new List<MapPoint>();
                    double length = geom.Length;
                    double pos = 0;
                    while (pos < length)
                    {
                        pts.Add(GeometryEngine.Instance.QueryPoint(geom, SegmentExtensionType.NoExtension, pos, AsRatioOrLength.AsLength));
                        pos += interval;
                    }
                    var endPt = GeometryEngine.Instance.QueryPoint(geom, SegmentExtensionType.NoExtension, length, AsRatioOrLength.AsLength);
                    if (pts.Count == 0 || GeometryEngine.Instance.Distance(pts[^1], endPt) > 1e-6)
                        pts.Add(endPt);
                    return pts;
                }

                using var cursor = lineLayer.Search(null);
                while (cursor.MoveNext())
                {
                    using var feature = (Feature)cursor.Current;
                    if (feature.GetShape() is not Polyline geom)
                        continue;
                    lineSR ??= geom.SpatialReference;

                    if (geom.IsEmpty || geom.Length == 0)
                    {
                        results.Add(new LineResult { Geometry = geom, Total = 0, On = 0, Off = 0, PctOn = 0, PctOff = 0 });
                        continue;
                    }

                    var sampleGeom = (rasterSR != null && geom.SpatialReference != null)
                        ? (Polyline)GeometryEngine.Instance.Project(geom, rasterSR)
                        : geom;

                    var points = SamplePoints(sampleGeom);
                    int total = points.Count;
                    int on = 0;
                    foreach (var pt in points)
                        if (IsOnRaster(pt.X, pt.Y)) on++;
                    int off = total - on;
                    double pctOn = total > 0 ? Math.Round(100.0 * on / total, 2) : 0;
                    double pctOff = total > 0 ? Math.Round(100.0 * off / total, 2) : 0;

                    results.Add(new LineResult { Geometry = geom, Total = total, On = on, Off = off, PctOn = pctOn, PctOff = pctOff });
                }
            });

            if (results.Count == 0)
            {
                messages.Add("No line features found; output not created.");
                return (true, 0, messages);
            }

            var (workspace, name) = GdbOutputHelper.SplitOutputPath(outputPath);
            var (createOk, createMsg) = await GdbOutputHelper.CreateFeatureClassAsync(workspace, name, "POLYLINE", lineSR);
            if (!createOk)
            {
                messages.Add(createMsg);
                return (false, 0, messages);
            }

            foreach (var (fName, fType, len) in new (string, string, int)[]
            {
                ("TOTAL_PTS", "LONG", 0),
                ("ON_RASTER", "LONG", 0),
                ("OFF_RASTER", "LONG", 0),
                ("PCT_ON", "DOUBLE", 0),
                ("PCT_OFF", "DOUBLE", 0)
            })
            {
                var (fieldOk, fieldMsg) = await GdbOutputHelper.AddFieldAsync(outputPath, fName, fType, len);
                if (!fieldOk)
                {
                    messages.Add(fieldMsg);
                    return (false, 0, messages);
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
                        buffer["Shape"] = r.Geometry;
                        buffer["TOTAL_PTS"] = r.Total;
                        buffer["ON_RASTER"] = r.On;
                        buffer["OFF_RASTER"] = r.Off;
                        buffer["PCT_ON"] = r.PctOn;
                        buffer["PCT_OFF"] = r.PctOff;
                        insertCursor.Insert(buffer);
                    }
                    insertCursor.Flush();
                }
            });

            messages.Add($"Sampled {results.Count} line(s) against the raster.");
            messages.Add($"Created output at {outputPath}");

            return (true, results.Count, messages);
        }
    }
}
