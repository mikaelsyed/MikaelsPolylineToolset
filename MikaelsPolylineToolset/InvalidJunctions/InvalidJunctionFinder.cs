using ArcGIS.Core.Data;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Mapping;
using System;
using System.Collections.Generic;

namespace MikaelsPolylineToolset.InvalidJunctions
{
    /// <summary>
    /// Finds invalid flow junctions in a directed line layer: nodes where two or more lines
    /// START (the flow splits), which is invalid in a directed line network. Lines are treated
    /// as DIRECTED -- first vertex = START, last vertex = END. Ported from SplitFlowFinder.pyt
    /// (SelectInvalidJunctions). Must be called from within QueuedTask.Run.
    /// </summary>
    internal static class InvalidJunctionFinder
    {
        private class NodeInfo
        {
            public HashSet<long> Starts = new HashSet<long>();
            public HashSet<long> Ends = new HashSet<long>();
        }

        public static List<long> FindInvalidJunctionOids(
            FeatureLayer lineLayer,
            double tolerance,
            bool requireIncoming,
            bool startsOnly,
            Action<string> log)
        {
            double? snap = tolerance > 0 ? tolerance : (double?)null;

            (long, long) NodeKey(double x, double y)
            {
                if (snap.HasValue)
                    return ((long)Math.Round(x / snap.Value), (long)Math.Round(y / snap.Value));
                return ((long)Math.Round(x * 1e8), (long)Math.Round(y * 1e8));
            }

            var nodes = new Dictionary<(long, long), NodeInfo>();
            int nLines = 0;

            using (var cursor = lineLayer.Search(null))
            {
                while (cursor.MoveNext())
                {
                    using var feature = (Feature)cursor.Current;
                    long oid = feature.GetObjectID();
                    if (feature.GetShape() is not Polyline geom || geom.IsEmpty)
                    {
                        log($"OID {oid} has null/empty geometry; skipped.");
                        continue;
                    }
                    nLines++;
                    var sp = geom.Points[0];
                    var ep = geom.Points[geom.PointCount - 1];
                    var sk = NodeKey(sp.X, sp.Y);
                    var ek = NodeKey(ep.X, ep.Y);

                    if (!nodes.TryGetValue(sk, out var sInfo))
                        nodes[sk] = sInfo = new NodeInfo();
                    sInfo.Starts.Add(oid);

                    if (!nodes.TryGetValue(ek, out var eInfo))
                        nodes[ek] = eInfo = new NodeInfo();
                    eInfo.Ends.Add(oid);
                }
            }

            var selected = new HashSet<long>();
            int badNodes = 0;
            foreach (var info in nodes.Values)
            {
                if (info.Starts.Count >= 2 && (!requireIncoming || info.Ends.Count >= 1))
                {
                    badNodes++;
                    if (startsOnly)
                        selected.UnionWith(info.Starts);
                    else
                    {
                        selected.UnionWith(info.Starts);
                        selected.UnionWith(info.Ends);
                    }
                }
            }

            var result = new List<long>(selected);
            result.Sort();
            log($"Found {badNodes} invalid junction(s); selecting {result.Count} line(s) out of {nLines}.");
            return result;
        }
    }
}
