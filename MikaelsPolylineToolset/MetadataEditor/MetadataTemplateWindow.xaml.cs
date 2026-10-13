using ArcGIS.Desktop.Core;
using MikaelsPolylineToolset.Common;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace MikaelsPolylineToolset.MetadataEditor
{
    public partial class MetadataTemplateWindow : ArcGIS.Desktop.Framework.Controls.ProWindow
    {
        /// <summary>Populated after ShowDialog() returns true.</summary>
        internal ShapefileMetadata Result { get; private set; }

        /// <summary>The feature class being edited; used to pick the matching layer out of a loaded QC log.</summary>
        internal string TargetPath { get; set; }

        private readonly Dictionary<string, (TextBox Err, TextBox Fp)> _polylineRows;
        private readonly Dictionary<string, (TextBox Err, TextBox Fp, CheckBox Use)> _reviewerRows = new Dictionary<string, (TextBox, TextBox, CheckBox)>();
        private QcRunLog _polylineLog, _reviewerLog;
        private string _polylineFile, _reviewerFile;
        private bool _populating;

        public MetadataTemplateWindow()
        {
            InitializeComponent();

            ProcessCombo.ItemsSource = MetadataTemplateService.ProcessOptions;
            DlmTextBox.Text = DateTime.Now.ToString("M/d/yyyy");

            // Polyline QC batch step key -> the template's existing rows.
            _polylineRows = new Dictionary<string, (TextBox, TextBox)>
            {
                ["IsolatedLines"] = (IsolatedLinesErrorsBox, IsolatedLinesFpBox),
                ["CrossingLines"] = (CrossingPolylinesErrorsBox, CrossingPolylinesFpBox),
                ["OverlappingLines"] = (OverlapPolylinesErrorsBox, OverlapPolylinesFpBox),
                ["ConvergingEnds"] = (ConvergingEndsErrorsBox, ConvergingEndsFpBox),
                ["NValence1"] = (NValence3ErrorsBox, NValence3FpBox),
                ["NValence2"] = (NValence4ErrorsBox, NValence4FpBox),
                ["VertexCount"] = (CulvertVertexErrorsBox, CulvertVertexFpBox),
                ["UnconnectedEnds"] = (UnconnectedEndsErrorsBox, UnconnectedEndsFpBox),
                ["InflowOutflow"] = (ValidateInOutErrorsBox, ValidateInOutFpBox),
            };

            BuildReviewerGrid();
            Loaded += (s, e) => FindDefaultLogs(silent: true);
        }

        // ------------------------------------------------------------------
        // Reviewer QC rows
        // ------------------------------------------------------------------

        private void BuildReviewerGrid()
        {
            ReviewerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            ReviewerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
            ReviewerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            ReviewerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });
            for (int i = 0; i <= QcLogTemplateMapper.ReviewerChecks.Length; i++)
                ReviewerGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            void Header(int col, string text)
            {
                var tb = new TextBlock { Text = text, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 4) };
                Grid.SetRow(tb, 0); Grid.SetColumn(tb, col);
                ReviewerGrid.Children.Add(tb);
            }
            Header(1, "Errors"); Header(2, "False Pos."); Header(3, "Use");

            int row = 1;
            foreach (var (key, label) in QcLogTemplateMapper.ReviewerChecks)
            {
                var name = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3, 8, 3) };
                var err = new TextBox { Text = "0", HorizontalContentAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 3, 0, 3) };
                var fp = new TextBox { Text = "0", HorizontalContentAlignment = HorizontalAlignment.Center, Margin = new Thickness(4, 3, 0, 3) };
                var use = new CheckBox { IsChecked = true, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetRow(name, row); Grid.SetColumn(name, 0);
                Grid.SetRow(err, row); Grid.SetColumn(err, 1);
                Grid.SetRow(fp, row); Grid.SetColumn(fp, 2);
                Grid.SetRow(use, row); Grid.SetColumn(use, 3);
                ReviewerGrid.Children.Add(name);
                ReviewerGrid.Children.Add(err);
                ReviewerGrid.Children.Add(fp);
                ReviewerGrid.Children.Add(use);
                _reviewerRows[key] = (err, fp, use);
                row++;
            }
        }

        // ------------------------------------------------------------------
        // QC output logs
        // ------------------------------------------------------------------

        private static string LogFolder()
        {
            string folder = Project.Current?.HomeFolderPath;
            return !string.IsNullOrEmpty(folder) && Directory.Exists(folder) ? folder : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }

        private void LoadLogsButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select the QC output log file(s)",
                Filter = "QC output log (*.txt)|*.txt|All files (*.*)|*.*",
                Multiselect = true,
                InitialDirectory = LogFolder()
            };
            if (dialog.ShowDialog(this) == true)
                LoadFiles(dialog.FileNames);
        }

        private void FindLogsButton_Click(object sender, RoutedEventArgs e) => FindDefaultLogs(silent: false);

        private void FindDefaultLogs(bool silent)
        {
            string folder = LogFolder();
            var found = new[] { QcRunLog.PolylineDefaultFileName, QcRunLog.ReviewerDefaultFileName }
                .Select(n => Path.Combine(folder, n)).Where(File.Exists).ToList();
            if (found.Count == 0)
            {
                if (!silent)
                    StatusTextBlock.Text = $"No {QcRunLog.PolylineDefaultFileName} or {QcRunLog.ReviewerDefaultFileName} found in {folder}. Use 'Load log file(s)...' to pick them.";
                return;
            }
            LoadFiles(found);
        }

        private void LoadFiles(IEnumerable<string> files)
        {
            StatusTextBlock.Text = "";
            var problems = new List<string>();
            foreach (var file in files)
            {
                try
                {
                    var log = QcRunLog.Read(file);
                    if (log.ToolbarId == QcRunLog.PolylineToolbarId) SetLog(log, file, isPolyline: true);
                    else if (log.ToolbarId == QcRunLog.ReviewerToolbarId) SetLog(log, file, isPolyline: false);
                    else problems.Add($"{Path.GetFileName(file)}: not a Polyline QC or Reviewer QC log.");
                }
                catch (Exception ex)
                {
                    problems.Add($"{Path.GetFileName(file)}: {ex.Message}");
                }
            }
            if (problems.Count > 0) StatusTextBlock.Text = string.Join(" ", problems);
        }

        private void SetLog(QcRunLog log, string file, bool isPolyline)
        {
            var combo = isPolyline ? PolylineLogLayerCombo : ReviewerLogLayerCombo;
            if (isPolyline) { _polylineLog = log; _polylineFile = file; }
            else { _reviewerLog = log; _reviewerFile = file; }

            var choices = QcLogTemplateMapper.LayerChoices(log);
            _populating = true;
            combo.ItemsSource = choices;
            combo.SelectedItem = QcLogTemplateMapper.PickDefault(choices, TargetPath);
            _populating = false;

            (isPolyline ? PolylineLogPanel : ReviewerLogPanel).Visibility = Visibility.Visible;
            ApplyLog(isPolyline);
        }

        private void PolylineLogLayerCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_populating) ApplyLog(isPolyline: true);
        }

        private void ReviewerLogLayerCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_populating) ApplyLog(isPolyline: false);
        }

        /// <summary>Fills the template rows from the loaded log for the chosen layer and describes what was and wasn't filled.</summary>
        private void ApplyLog(bool isPolyline)
        {
            var log = isPolyline ? _polylineLog : _reviewerLog;
            var file = isPolyline ? _polylineFile : _reviewerFile;
            var choice = (isPolyline ? PolylineLogLayerCombo : ReviewerLogLayerCombo).SelectedItem as QcLogTemplateMapper.LayerChoice;
            var text = isPolyline ? PolylineLogText : ReviewerLogText;
            if (log == null) return;

            if (choice == null)
            {
                text.Text = $"{Path.GetFileName(file)} - {QcLogTemplateMapper.DescribeRun(log)}. The log has no polyline or polygon layer results to use.";
                return;
            }

            var entries = QcLogTemplateMapper.Extract(log, choice.Name);
            int filled = 0, rows = 0;
            var notFilled = new List<string>();
            var cautions = new List<string>();

            if (isPolyline)
            {
                foreach (var (key, boxes) in _polylineRows)
                {
                    rows++;
                    if (!entries.TryGetValue(key, out var entry)) continue;
                    if (entry.Filled)
                    {
                        boxes.Err.Text = entry.Errors.ToString();
                        boxes.Fp.Text = entry.FalsePositives.ToString();
                        filled++;
                        if (!string.IsNullOrEmpty(entry.Note)) cautions.Add($"{entry.Label}: {entry.Note}");
                    }
                    else notFilled.Add($"{entry.Label} ({entry.Note})");
                }

                // The two valence rows are labelled with the valence the run used.
                NValence3Label.Text = "N-Valence Junctions (" + (entries.TryGetValue("NValence1", out var v1) && v1.Filled ? QcLogTemplateMapper.ValenceOf(log, "NValence1") ?? 3 : 3) + ")";
                NValence4Label.Text = "N-Valence Junctions (" + (entries.TryGetValue("NValence2", out var v2) && v2.Filled ? QcLogTemplateMapper.ValenceOf(log, "NValence2") ?? 4 : 4) + ")";

                var extra = entries.Where(kv => !_polylineRows.ContainsKey(kv.Key) && kv.Value.Filled).Select(kv => $"{kv.Value.Label} ({kv.Value.Errors:N0})").ToList();
                if (extra.Count > 0) cautions.Add("In the log but not part of this template: " + string.Join(", ", extra));
            }
            else
            {
                foreach (var (key, boxes) in _reviewerRows)
                {
                    rows++;
                    if (!entries.TryGetValue(key, out var entry)) { boxes.Use.IsChecked = false; continue; }
                    if (entry.Filled)
                    {
                        boxes.Err.Text = entry.Errors.ToString();
                        boxes.Fp.Text = entry.FalsePositives.ToString();
                        boxes.Use.IsChecked = true;
                        filled++;
                        if (!string.IsNullOrEmpty(entry.Note)) cautions.Add($"{entry.Label}: {entry.Note}");
                    }
                    else
                    {
                        boxes.Err.Text = "0";
                        boxes.Fp.Text = "0";
                        boxes.Use.IsChecked = false;
                        notFilled.Add($"{entry.Label} ({entry.Note})");
                    }
                }
            }

            var lines = new List<string>
            {
                $"{Path.GetFileName(file)} - {QcLogTemplateMapper.DescribeRun(log)}.",
                $"Filled {filled} of {rows} checks from layer '{choice.Name}'."
            };
            if (notFilled.Count > 0) lines.Add("Not filled: " + string.Join("; ", notFilled) + ".");
            lines.AddRange(cautions);
            text.Text = string.Join(Environment.NewLine, lines);
        }

        // ------------------------------------------------------------------
        // OK / Cancel
        // ------------------------------------------------------------------

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            StatusTextBlock.Text = "";

            string subarea = SubareaTextBox.Text?.Trim();
            string process = (ProcessCombo.Text ?? "").Trim();
            string revision = RevisionTextBox.Text?.Trim();
            string name = NameTextBox.Text?.Trim();
            string dlm = DlmTextBox.Text?.Trim();

            if (string.IsNullOrEmpty(subarea) || string.IsNullOrEmpty(process) ||
                string.IsNullOrEmpty(revision) || string.IsNullOrEmpty(name) || string.IsNullOrEmpty(dlm))
            {
                StatusTextBlock.Text = "Subarea, Process, Revision, Name, and Date Last Modified are required.";
                return;
            }

            if (!TryReadCount(IsolatedLinesErrorsBox, "Isolated Lines Errors", out int isolatedLinesErrors) ||
                !TryReadCount(IsolatedLinesFpBox, "Isolated Lines False Positives", out int isolatedLinesFp) ||
                !TryReadCount(CrossingPolylinesErrorsBox, "Crossing Flow Errors", out int crossingFlowErrors) ||
                !TryReadCount(CrossingPolylinesFpBox, "Crossing Flow False Positives", out int crossingFlowFp) ||
                !TryReadCount(OverlapPolylinesErrorsBox, "Overlap Flow Errors", out int overlapFlowErrors) ||
                !TryReadCount(OverlapPolylinesFpBox, "Overlap Flow False Positives", out int overlapFlowFp) ||
                !TryReadCount(ConvergingEndsErrorsBox, "Converging Ends Errors", out int convergingEndsErrors) ||
                !TryReadCount(ConvergingEndsFpBox, "Converging Ends False Positives", out int convergingEndsFp) ||
                !TryReadCount(NValence3ErrorsBox, "N-Valence Junctions (3) Errors", out int nValence3Errors) ||
                !TryReadCount(NValence3FpBox, "N-Valence Junctions (3) False Positives", out int nValence3Fp) ||
                !TryReadCount(NValence4ErrorsBox, "N-Valence Junctions (4) Errors", out int nValence4Errors) ||
                !TryReadCount(NValence4FpBox, "N-Valence Junctions (4) False Positives", out int nValence4Fp) ||
                !TryReadCount(CulvertVertexErrorsBox, "Vertex Count Check Errors", out int culvertErrors) ||
                !TryReadCount(CulvertVertexFpBox, "Vertex Count Check False Positives", out int culvertFp) ||
                !TryReadCount(UnconnectedEndsErrorsBox, "Unconnected Ends Errors", out int unconnectedErrors) ||
                !TryReadCount(UnconnectedEndsFpBox, "Unconnected Ends False Positives", out int unconnectedFp) ||
                !TryReadCount(ValidateInOutErrorsBox, "Validate Inflow/Outflow Errors", out int validateErrors) ||
                !TryReadCount(ValidateInOutFpBox, "Validate Inflow/Outflow False Positives", out int validateFp))
            {
                return;
            }

            var input = new MetadataTemplateInput
            {
                Subarea = subarea,
                Process = process,
                Revision = revision,
                Name = name,
                DateLastModified = dlm,
                Notes = NotesTextBox.Text?.Trim() ?? "",
                IsolatedLinesErrors = isolatedLinesErrors,
                IsolatedLinesFalsePositives = isolatedLinesFp,
                CrossingPolylinesErrors = crossingFlowErrors,
                CrossingPolylinesFalsePositives = crossingFlowFp,
                OverlapPolylinesErrors = overlapFlowErrors,
                OverlapPolylinesFalsePositives = overlapFlowFp,
                ConvergingEndsErrors = convergingEndsErrors,
                ConvergingEndsFalsePositives = convergingEndsFp,
                NValence3Errors = nValence3Errors,
                NValence3FalsePositives = nValence3Fp,
                NValence4Errors = nValence4Errors,
                NValence4FalsePositives = nValence4Fp,
                CulvertVertexCountErrors = culvertErrors,
                CulvertVertexCountFalsePositives = culvertFp,
                UnconnectedEndsErrors = unconnectedErrors,
                UnconnectedEndsFalsePositives = unconnectedFp,
                ValidateInflowOutflowErrors = validateErrors,
                ValidateInflowOutflowFalsePositives = validateFp,
                NValence3Label = NValence3Label.Text,
                NValence4Label = NValence4Label.Text
            };

            foreach (var (key, label) in QcLogTemplateMapper.ReviewerChecks)
            {
                var row = _reviewerRows[key];
                if (row.Use.IsChecked != true) continue;
                if (!TryReadCount(row.Err, label + " Errors", out int errors) ||
                    !TryReadCount(row.Fp, label + " False Positives", out int fp))
                    return;
                input.ReviewerLines.Add(new ReviewerQcLine { Label = label, Errors = errors, FalsePositives = fp });
            }

            if (_polylineLog != null) input.QcRuns.Add(QcLogTemplateMapper.DescribeRun(_polylineLog));
            if (_reviewerLog != null) input.QcRuns.Add(QcLogTemplateMapper.DescribeRun(_reviewerLog));

            Result = MetadataTemplateService.Apply(input);
            DialogResult = true;
            Close();
        }

        private bool TryReadCount(TextBox box, string label, out int value)
        {
            string text = box.Text?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                value = 0;
                return true;
            }

            if (!int.TryParse(text, out value) || value < 0)
            {
                StatusTextBlock.Text = $"{label} must be a whole number of 0 or more.";
                box.Focus();
                box.SelectAll();
                return false;
            }
            return true;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
