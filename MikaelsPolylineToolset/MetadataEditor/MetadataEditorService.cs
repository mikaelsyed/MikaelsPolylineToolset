using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace MikaelsPolylineToolset.MetadataEditor
{
    /// <summary>Editable fields shown in the Metadata Editor window, matching Pro's own Item Description panel.</summary>
    internal class ShapefileMetadata
    {
        public string Title { get; set; } = "";
        public string Tags { get; set; } = "";
        public string Summary { get; set; } = "";
        public string Description { get; set; } = "";
        public string Credits { get; set; } = "";
        public string Constraints { get; set; } = "";
    }

    /// <summary>
    /// Reads and writes feature class metadata -- a shapefile's .shp.xml sidecar, or a File/
    /// Mobile geodatabase feature class's metadata record -- through the Pro SDK's IMetadata
    /// interface (ItemFactory.Instance.Create + IMetadata.GetXml/SetXml handle both the same
    /// way), parsing/rewriting just the handful of ArcGIS Metadata (ISO 19139) elements shown
    /// in Pro's Item Description panel. Everything else already present in the XML -- spatial
    /// reference info, sync-generated data properties, etc. -- is left as-is.
    /// </summary>
    internal static class MetadataEditorService
    {
        public static async Task<(bool Success, bool CanEdit, ShapefileMetadata Metadata, string Message)> LoadAsync(string shapefilePath)
        {
            return await QueuedTask.Run(() =>
            {
                Item item;
                try
                {
                    item = ItemFactory.Instance.Create(shapefilePath);
                }
                catch (Exception ex)
                {
                    return (false, false, (ShapefileMetadata)null, $"Could not open '{shapefilePath}': {ex.Message}");
                }

                if (item is not IMetadata metaItem)
                    return (false, false, (ShapefileMetadata)null, "This item does not support metadata.");

                bool canEdit = metaItem.CanEdit();
                string xml = metaItem.GetXml();
                var metadata = ParseXml(xml);

                string message = canEdit
                    ? "Metadata loaded."
                    : "Metadata loaded, but this item is read-only (in use elsewhere, or the file/folder is write-protected) -- editing is disabled.";
                return (true, canEdit, metadata, message);
            });
        }

        public static async Task<(bool Success, string Message)> SaveAsync(string shapefilePath, ShapefileMetadata metadata)
        {
            return await QueuedTask.Run(() =>
            {
                Item item;
                try
                {
                    item = ItemFactory.Instance.Create(shapefilePath);
                }
                catch (Exception ex)
                {
                    return (false, $"Could not open '{shapefilePath}': {ex.Message}");
                }

                if (item is not IMetadata metaItem)
                    return (false, "This item does not support metadata.");

                if (!metaItem.CanEdit())
                    return (false, "Metadata for this item cannot be edited (it may be read-only, or open/locked elsewhere).");

                string newXml = BuildXml(metaItem.GetXml(), metadata);
                metaItem.SetXml(newXml);

                try { metaItem.Synchronize(); } catch { /* best effort -- core fields are already saved */ }

                return (true, $"Metadata saved to {shapefilePath}");
            });
        }

        private static ShapefileMetadata ParseXml(string xml)
        {
            var metadata = new ShapefileMetadata();
            if (string.IsNullOrWhiteSpace(xml)) return metadata;

            XDocument doc;
            try { doc = XDocument.Parse(xml); }
            catch { return metadata; }

            var dataIdInfo = doc.Root?.Element("dataIdInfo");
            if (dataIdInfo == null) return metadata;

            metadata.Title = dataIdInfo.Element("idCitation")?.Element("resTitle")?.Value.Trim() ?? "";
            metadata.Summary = dataIdInfo.Element("idPurp")?.Value.Trim() ?? "";
            metadata.Description = dataIdInfo.Element("idAbs")?.Value.Trim() ?? "";
            metadata.Credits = dataIdInfo.Element("idCredit")?.Value.Trim() ?? "";

            var keywords = dataIdInfo.Element("searchKeys")?.Elements("keyword")
                .Select(k => k.Value.Trim())
                .Where(k => k.Length > 0);
            metadata.Tags = keywords != null ? string.Join(", ", keywords) : "";

            var useLimits = dataIdInfo.Element("resConst")?.Element("Consts")?.Elements("useLimit")
                .Select(u => u.Value.Trim())
                .Where(u => u.Length > 0);
            metadata.Constraints = useLimits != null ? string.Join(Environment.NewLine, useLimits) : "";

            return metadata;
        }

        private static string BuildXml(string existingXml, ShapefileMetadata metadata)
        {
            XDocument doc = null;
            if (!string.IsNullOrWhiteSpace(existingXml))
            {
                try { doc = XDocument.Parse(existingXml); }
                catch { doc = null; }
            }
            if (doc?.Root == null || doc.Root.Name.LocalName != "metadata")
                doc = NewMetadataDoc();

            var dataIdInfo = GetOrCreate(doc.Root, "dataIdInfo");
            var idCitation = GetOrCreate(dataIdInfo, "idCitation");
            SetElementValue(idCitation, "resTitle", metadata.Title);
            SetElementValue(dataIdInfo, "idPurp", metadata.Summary);
            SetElementValue(dataIdInfo, "idAbs", metadata.Description);
            SetElementValue(dataIdInfo, "idCredit", metadata.Credits);

            dataIdInfo.Element("searchKeys")?.Remove();
            var tags = SplitList(metadata.Tags);
            if (tags.Count > 0)
            {
                var searchKeys = new XElement("searchKeys");
                foreach (var tag in tags)
                    searchKeys.Add(new XElement("keyword", tag));
                dataIdInfo.Add(searchKeys);
            }

            dataIdInfo.Element("resConst")?.Remove();
            var limits = SplitLines(metadata.Constraints);
            if (limits.Count > 0)
            {
                var consts = new XElement("Consts");
                foreach (var limit in limits)
                    consts.Add(new XElement("useLimit", limit));
                dataIdInfo.Add(new XElement("resConst", consts));
            }

            return doc.Declaration != null
                ? doc.Declaration + Environment.NewLine + doc
                : doc.ToString();
        }

        private static XDocument NewMetadataDoc()
        {
            return new XDocument(
                new XDeclaration("1.0", "utf-8", null),
                new XElement("metadata",
                    new XAttribute(XNamespace.Xml + "lang", "en"),
                    new XElement("Esri",
                        new XElement("ArcGISFormat", "1.0"),
                        new XElement("SyncOnce", "FALSE"))));
        }

        private static XElement GetOrCreate(XElement parent, string name)
        {
            var el = parent.Element(name);
            if (el == null)
            {
                el = new XElement(name);
                parent.Add(el);
            }
            return el;
        }

        private static void SetElementValue(XElement parent, string name, string value)
        {
            value = value?.Trim() ?? "";
            var el = parent.Element(name);
            if (value.Length == 0)
            {
                el?.Remove();
                return;
            }
            if (el == null)
            {
                el = new XElement(name);
                parent.Add(el);
            }
            el.Value = value;
        }

        private static List<string> SplitList(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return new List<string>();
            return text.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim())
                .Where(t => t.Length > 0)
                .ToList();
        }

        private static List<string> SplitLines(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return new List<string>();
            return text.Replace("\r\n", "\n").Split('\n')
                .Select(t => t.Trim())
                .Where(t => t.Length > 0)
                .ToList();
        }
    }
}
