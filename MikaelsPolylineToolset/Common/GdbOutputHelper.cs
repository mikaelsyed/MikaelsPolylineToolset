using ArcGIS.Core.Data;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Core.Geoprocessing;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace MikaelsPolylineToolset.Common
{
    /// <summary>
    /// Helpers for creating a brand-new output feature class (not copying an existing
    /// selection) and writing computed rows into it -- used by tools that generate derived
    /// points rather than just selecting existing features.
    /// </summary>
    internal static class GdbOutputHelper
    {
        public static (string Workspace, string Name) SplitOutputPath(string fullPath)
        {
            string workspace = Path.GetDirectoryName(fullPath);
            string name = Path.GetFileName(fullPath);
            return (workspace, name);
        }

        public static async Task<(bool Success, string Message)> CreateFeatureClassAsync(
            string workspace, string name, string geometryType, SpatialReference sr)
        {
            string srParam = sr != null && sr.Wkid > 0 ? sr.Wkid.ToString() : (sr?.Wkt ?? "");
            var createParams = Geoprocessing.MakeValueArray(workspace, name, geometryType, null, "DISABLED", "DISABLED", srParam);
            var result = await Geoprocessing.ExecuteToolAsync("management.CreateFeatureclass", createParams,
                null, null, null, GPExecuteToolFlags.None);
            if (result.IsFailed)
                return (false, "CreateFeatureclass failed: " + string.Join("; ", result.ErrorMessages.Select(m => m.Text)));
            return (true, "");
        }

        public static async Task<(bool Success, string Message)> AddFieldAsync(
            string fcPath, string fieldName, string fieldType, int length = 0)
        {
            var addParams = Geoprocessing.MakeValueArray(fcPath, fieldName, fieldType, null, null,
                length > 0 ? (object)length : null);
            var result = await Geoprocessing.ExecuteToolAsync("management.AddField", addParams,
                null, null, null, GPExecuteToolFlags.None);
            if (result.IsFailed)
                return (false, $"AddField {fieldName} failed: " + string.Join("; ", result.ErrorMessages.Select(m => m.Text)));
            return (true, "");
        }

        /// <summary>Opens a just-created feature class for writing. Must be called from within QueuedTask.Run.</summary>
        public static (Geodatabase Geodatabase, FeatureClass FeatureClass) OpenFileGdbFeatureClass(string workspace, string name)
        {
            if (!workspace.EndsWith(".gdb", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Output must be a feature class inside a File Geodatabase (.gdb).");

            var gdb = new Geodatabase(new FileGeodatabaseConnectionPath(new Uri(workspace)));
            var fc = gdb.OpenDataset<FeatureClass>(name);
            return (gdb, fc);
        }
    }
}
