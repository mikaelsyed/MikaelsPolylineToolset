using ArcGIS.Core.Data;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Mapping;
using MikaelsPolylineToolset.Common;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MikaelsPolylineToolset.VertexCountCheck
{
    /// <summary>
    /// Reads every flowline where a chosen attribute field equals a chosen value, and flags
    /// any of them that do NOT have exactly the expected vertex count (2 by default, i.e. a
    /// straight, unbent segment). Ported from FlowlineVertexChecker.pyt (CheckVertexCount).
    /// Must be called from within QueuedTask.Run.
    /// </summary>
    internal static class VertexCountFinder
    {
        public static List<long> FindVertexCountFlags(
            FeatureLayer lineLayer,
            string fieldName,
            string fieldValue,
            int expected,
            Action<string> log,
            QcProgress progress = null)
        {
            var matched = new List<(long Oid, int VertexCount)>();

            progress?.Begin("Reading polylines and counting vertices", QcRunUi.CountNow(lineLayer), 0, 1);
            using (var cursor = lineLayer.Search(null))
            {
                while (cursor.MoveNext())
                {
                    using var feature = (Feature)cursor.Current;
                    progress?.Tick(feature.GetObjectID());
                    var val = feature[fieldName];
                    var display = val?.ToString()?.Trim();
                    if (display != fieldValue) continue;

                    var geom = feature.GetShape() as Polyline;
                    int vcount = geom?.PointCount ?? 0;
                    matched.Add((feature.GetObjectID(), vcount));
                }
            }

            var flagged = matched.Where(m => m.VertexCount != expected)
                .Select(m => m.Oid).OrderBy(o => o).ToList();

            log($"Matched {matched.Count} polyline(s) with {fieldName} = {fieldValue}.");
            log(flagged.Count > 0
                ? $"{flagged.Count} of {matched.Count} flagged (vertex count != {expected})."
                : $"All matched polylines have exactly {expected} vertices. Nothing flagged.");

            return flagged;
        }
    }
}
