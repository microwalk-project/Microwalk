using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Windows.Threading;
using LeakageDetector;
using Microsoft.Win32;

namespace Visualizer
{
    /// <summary>
    /// Main form.
    /// </summary>
    public partial class MainWindow : Window
    {
        #region Variables

        /// <summary>
        /// The first currently rendered trace file.
        /// </summary>
        private TraceFile _traceFile1 = null;

        /// <summary>
        /// The second currently rendered trace file.
        /// </summary>
        private TraceFile _traceFile2 = null;

        /// <summary>
        /// The list of image file names as used by the loaded trace files.
        /// </summary>
        private List<string> _imageFileNames = new List<string>();

        /// <summary>
        /// Contains symbol information of image files.
        /// </summary>
        private Dictionary<string, MapFile> _mapFiles = new Dictionary<string, MapFile>();

        /// <summary>
        /// The nodes of the functions.
        /// </summary>
        private List<FunctionNode> _functionNodes;

        /// <summary>
        /// The name annotations of the functions.
        /// </summary>
        private List<AnnotationElement> _functionNodeNameAnnotations;

        /// <summary>
        /// The drawn trace entry arrows. The entries are sorted ascending by their Y coordinates (as they were read from the trace file).
        /// </summary>
        private List<EntryArrow> _entryArrows;

        /// <summary>
        /// The current zoom level.
        /// </summary>
        private double _zoom = 1;

        /// <summary>
        /// The cursor position where the left mouse button has last been pressed.
        /// </summary>
        private Point _mouseClickLocation = new Point(0, 0);

        /// <summary>
        /// The currently hovered entry arrow.
        /// </summary>
        private EntryArrow _hoveredEntryArrow = null;

        /// <summary>
        /// The colored areas visualizing diff sections.
        /// </summary>
        private List<DiffArea> _diffAreas;

        #endregion

        #region Functions

        /// <summary>
        /// Constructor.
        /// </summary>
        public MainWindow()
        {
            // Load controls
            InitializeComponent();
        }

        /// <summary>
        /// Manages the render list, such that only visible objects are actually drawn.
        /// </summary>
        public void UpdateRenderedElements()
        {
            // Do nothing if there is nothing to be rendered
            if(_entryArrows == null)
                return;

            // Filtering is done vertically, only show elements with meaningful Y coordinates
            double yMin = _renderPanelContainer.VerticalOffset / _zoom;
            double yMax = yMin + _renderPanelContainer.ActualHeight / _zoom;

            // Find all visible children
            IEnumerable<EntryArrow> visibleEntries = _entryArrows.Where(ea => yMin <= ea.PositionY && ea.PositionY <= yMax);

            // Delete old arrow entries
            // The elements not being deleted reside in the lower indices of the list
            int nonDeleteCount = _functionNodes.Count + _functionNodeNameAnnotations.Count + _diffAreas.Count;
            _renderPanel.Children.RemoveRange(nonDeleteCount, _renderPanel.Children.Count - nonDeleteCount);

            // Add visible entries
            foreach(var entry in visibleEntries)
                _renderPanel.Children.Add(entry);

            // Update vertical positions of function name annotations
            foreach(var annotation in _functionNodeNameAnnotations)
            {
                // Do not scale annotation, keep it on top of the render area
                annotation.RenderTransform = (Transform)_renderPanel.LayoutTransform.Inverse;
                Canvas.SetTop(annotation, yMin + 20 / _zoom);
            }
        }

        #endregion

        #region Event handlers

        private void _loadTraceFileButton_Click(object sender, RoutedEventArgs e)
        {
            // Check input first
            string mapFileName = Properties.Settings.Default.MapFileName;
            string trace1FileName = Properties.Settings.Default.Trace1FileName;
            string trace2FileName = Properties.Settings.Default.Trace2FileName;
            if(string.IsNullOrWhiteSpace(trace2FileName))
                trace2FileName = trace1FileName;
            if(!File.Exists(mapFileName) || !File.Exists(trace1FileName) || !File.Exists(trace2FileName))
            {
                MessageBox.Show("File(s) not found.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Reset lists possibly containing data from previous loads
            _imageFileNames.Clear();
            _mapFiles.Clear();

            // Delete all drawable elements
            _functionNodes = new List<FunctionNode>();
            _functionNodeNameAnnotations = new List<AnnotationElement>();
            _entryArrows = new List<EntryArrow>();
            _diffAreas = new List<DiffArea>();
            _renderPanel.Children.Clear();

            // Local function to create function nodes (called in several places)
            double nextNodeX = 20;
            FunctionNode CreateFunctionNode(string functionName)
            {
                // Create new node
                FunctionNode node = new FunctionNode(functionName, new Point(nextNodeX, 20));
                nextNodeX += node.Width;
                _functionNodes.Add(node);
                return node;
            };

            // Regex for removing extensions from image names
            Regex imageNameRegex = new Regex("\\.(dll|exe)(\\..*)?", RegexOptions.Compiled | RegexOptions.IgnoreCase);

            // Load map files
            string mapFileImageName = System.IO.Path.GetFileNameWithoutExtension(mapFileName).ToLower();
            _mapFiles.Add(imageNameRegex.Replace(mapFileImageName, ""), new MapFile(mapFileName));

            // Load trace files
            _traceFile1 = new TraceFile(trace1FileName, _imageFileNames, 1);
            _traceFile1.CacheEntries();
            _traceFile2 = new TraceFile(trace2FileName, _imageFileNames, 1);
            _traceFile2.CacheEntries();

            // Local function to retrieve function metadata for a given address
            Dictionary<int, string> imageIdMapFileNameMapping = new Dictionary<int, string>();
            Dictionary<int, Dictionary<ulong, FunctionNode>> nodes = new Dictionary<int, Dictionary<ulong, FunctionNode>>();
            FunctionNode GetFunctionNodeForBranchEntryOperand(int imageId, string imageName, ulong instructionAddress)
            {
                // Try to retrieve name of function
                string cleanedImageName = imageNameRegex.Replace(imageName, "");
                if(!imageIdMapFileNameMapping.ContainsKey(imageId))
                {
                    // Check whether map file exists
                    if(_mapFiles.ContainsKey(cleanedImageName.ToLower()))
                        imageIdMapFileNameMapping.Add(imageId, cleanedImageName.ToLower());
                    else
                        imageIdMapFileNameMapping.Add(imageId, null);
                    nodes.Add(imageId, new Dictionary<ulong, FunctionNode>());
                    nodes[imageId].Add(0, CreateFunctionNode(cleanedImageName));
                }
                string sourceMapFileName = imageIdMapFileNameMapping[imageId];
                ulong sourceInstructionAddress = instructionAddress - 0x1000; // TODO hardcoded -> correct?
                var (sourceFunctionAddress, sourceFunctionName) = (sourceMapFileName == null ? (0, "Unknown") : _mapFiles[sourceMapFileName].GetSymbolNameByAddress(sourceInstructionAddress));

                // Retrieve source function node
                if(!nodes[imageId].ContainsKey(sourceFunctionAddress))
                    nodes[imageId].Add(sourceFunctionAddress, CreateFunctionNode(cleanedImageName + " ! " + sourceFunctionName));
                return nodes[imageId][sourceFunctionAddress];
            };

            double CreateEntryArrow(IEnumerator<TraceEntry> traceFileEnumerator, double y, int index, int traceFileId)
            {
                // Retrieve entry and check its type
                traceFileEnumerator.MoveNext();
                TraceEntry entry = traceFileEnumerator.Current;
                if(entry.EntryType != TraceEntryTypes.Branch)
                    return 0;

                // Draw if taken
                BranchEntry branchEntry = (BranchEntry)entry;
                if(branchEntry.Taken)
                {
                    // Get function nodes of first entry
                    FunctionNode sourceFunctionNode1 = GetFunctionNodeForBranchEntryOperand(branchEntry.SourceImageId, branchEntry.SourceImageName, branchEntry.SourceInstructionAddress);
                    FunctionNode destinationFunctionNode1 = GetFunctionNodeForBranchEntryOperand(branchEntry.DestinationImageId, branchEntry.DestinationImageName, branchEntry.DestinationInstructionAddress);

                    // Add line
                    _entryArrows.Add(new EntryArrow(sourceFunctionNode1, destinationFunctionNode1, y, branchEntry, index, traceFileId));
                    return EntryArrow.ArrowTipSideLength + 4;
                }
                return 0;
            };

            // Compare trace files and create diff
            TraceFileDiff diff = new TraceFileDiff(_traceFile1, _traceFile2);
            double nextEntryY = 60;
            var traceFile1Enumerator = _traceFile1.Entries.GetEnumerator();
            var traceFile2Enumerator = _traceFile2.Entries.GetEnumerator();
            List<Tuple<double, double, Brush, string>> requestedDiffAreas = new List<Tuple<double, double, Brush, string>>();
            foreach(var diffEntry in diff.RunDiff())
            {
                // Save Y offset of first entry
                double baseY = nextEntryY;

                // Draw branch entries for trace
                int diffEntryCount1 = diffEntry.EndLine1 - diffEntry.StartLine1;
                int diffEntryCount2 = diffEntry.EndLine2 - diffEntry.StartLine2;
                int commonDiffEntryCount = Math.Min(diffEntryCount1, diffEntryCount2);
                for(int i = 0; i < commonDiffEntryCount; ++i)
                {
                    // Draw main entry
                    nextEntryY += CreateEntryArrow(traceFile1Enumerator, nextEntryY, diffEntry.StartLine1 + i, 1);

                    // If there are differences, draw corresponding other entry; move the enumerator anyway
                    if(!diffEntry.Equal)
                        nextEntryY += CreateEntryArrow(traceFile2Enumerator, nextEntryY, diffEntry.StartLine2 + i, 2);
                    else
                        traceFile2Enumerator.MoveNext();
                }

                // Draw remaining entries from longer sequence
                if(diffEntryCount1 > commonDiffEntryCount)
                    for(int i = commonDiffEntryCount; i < diffEntryCount1; ++i)
                        nextEntryY += CreateEntryArrow(traceFile1Enumerator, nextEntryY, diffEntry.StartLine1 + i, 1);
                else if(diffEntryCount2 > commonDiffEntryCount)
                    for(int i = commonDiffEntryCount; i < diffEntryCount2; ++i)
                        nextEntryY += CreateEntryArrow(traceFile2Enumerator, nextEntryY, diffEntry.StartLine2 + i, 2);

                // Differences? => Schedule 
                if(!diffEntry.Equal)
                {
                    // Make sure this diff area is visible even if it does not contain branch entries
                    requestedDiffAreas.Add(new Tuple<double, double, Brush, string>(baseY - 2, nextEntryY + 2, Brushes.LightPink, $"DIFF   A: {diffEntry.StartLine1}-{diffEntry.EndLine1 - 1} vs. B: {diffEntry.StartLine2}-{diffEntry.EndLine2 - 1}"));
                    nextEntryY += 6;
                }
            }

            // Show message if files match
            if(!requestedDiffAreas.Any())
                MessageBox.Show("The compared files match.", "Trace diff", MessageBoxButton.OK, MessageBoxImage.Information);

            // Set draw panel dimensions
            _renderPanel.Width = nextNodeX;
            _renderPanel.Height = nextEntryY;

            // Draw diff areas in the background
            FunctionNode lastFunctionNode = _functionNodes.LastOrDefault();
            if(lastFunctionNode != null)
                foreach(var reqDiffArea in requestedDiffAreas)
                {
                    // Create diff area
                    DiffArea diffArea = new DiffArea(lastFunctionNode.Position.X + lastFunctionNode.Width, reqDiffArea.Item2 - reqDiffArea.Item1, reqDiffArea.Item3, reqDiffArea.Item4, reqDiffArea.Item1);
                    Canvas.SetLeft(diffArea, 0);
                    Canvas.SetTop(diffArea, reqDiffArea.Item1);
                    _diffAreas.Add(diffArea);
                    _renderPanel.Children.Add(diffArea);
                }

            // Draw function nodes above the diff areas
            foreach(var node in _functionNodes)
            {
                // Insert function node
                node.GenerateVisual(nextEntryY);
                _renderPanel.Children.Add(node);
                Canvas.SetLeft(node, node.Position.X);
                Canvas.SetTop(node, node.Position.Y);

                // Create annotation node to display function name independent of scrolling
                // The vertical position will be updated in another place
                AnnotationElement annotationElement = new AnnotationElement(node.FunctionName);
                Canvas.SetLeft(annotationElement, node.CenterXPosition - annotationElement.TopLeft.X + 2);
                _functionNodeNameAnnotations.Add(annotationElement);
                _renderPanel.Children.Add(annotationElement);
            }

            // Finally draw the entry arrows in the foreground
            foreach(var entry in _entryArrows)
            {
                // Prepare coordinates
                Canvas.SetLeft(entry, entry.From.CenterXPosition);
                Canvas.SetTop(entry, entry.PositionY);
            }

            // Update render data
            _renderPanel.LayoutTransform = new ScaleTransform(_zoom, _zoom);
            UpdateRenderedElements();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // Set initial render matrix
            _renderPanel.LayoutTransform = new ScaleTransform(_zoom, _zoom);
        }

        private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // Update render data
            _renderPanel.LayoutTransform = new ScaleTransform(_zoom, _zoom);
            UpdateRenderedElements();
        }

        private void _renderPanel_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            // Zoom if Ctrl key is down
            if(Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl))
            {
                // Do not pass this event to the scroll viewer
                e.Handled = true;

                // Remember old mouse position
                Point mousePos = _renderPanel.LayoutTransform.Transform(e.GetPosition(_renderPanel));

                // Change zoom value
                double zoomChangeFactor = (e.Delta > 0 ? 1.1 : (1.0 / 1.1));
                _zoom *= zoomChangeFactor;
                _renderPanel.LayoutTransform = new ScaleTransform(_zoom, _zoom);

                // Scroll to zoomed position
                Point targetPoint = new Point(mousePos.X * zoomChangeFactor, mousePos.Y * zoomChangeFactor);
                _renderPanelContainer.ScrollToHorizontalOffset(_renderPanelContainer.HorizontalOffset + targetPoint.X - mousePos.X);
                _renderPanelContainer.ScrollToVerticalOffset(_renderPanelContainer.VerticalOffset + targetPoint.Y - mousePos.Y);

                // Update render data
                UpdateRenderedElements();
            }
        }

        private void _renderPanel_MouseDown(object sender, MouseButtonEventArgs e)
        {
            // Capture mouse such that it does not leave the control
            _mouseClickLocation = e.GetPosition(_renderPanelContainer);
            _renderPanel.CaptureMouse();
            this.Cursor = Cursors.ScrollAll;
        }

        private void _renderPanel_MouseUp(object sender, MouseButtonEventArgs e)
        {
            // Release mouse
            _renderPanel.ReleaseMouseCapture();
            this.Cursor = Cursors.Arrow;
        }

        private void _renderPanel_MouseMove(object sender, MouseEventArgs e)
        {
            // Update rendering offset if left mouse button is pressed
            if(e.LeftButton == MouseButtonState.Pressed)
            {
                // Calculate new scroll offset
                Point mousePosition = e.GetPosition(_renderPanelContainer);
                _renderPanelContainer.ScrollToHorizontalOffset(_renderPanelContainer.HorizontalOffset - (mousePosition.X - _mouseClickLocation.X));
                _renderPanelContainer.ScrollToVerticalOffset(_renderPanelContainer.VerticalOffset - (mousePosition.Y - _mouseClickLocation.Y));
                _mouseClickLocation = mousePosition;

                // Update render data
                UpdateRenderedElements();
            }
            else if(_entryArrows != null)
            {
                // Get mouse position in render panel coordinate system
                Point mousePosition = e.GetPosition(_renderPanel);

                // Run through entry arrow list and test whether one was hit
                double minimumYValueForMatch = mousePosition.Y - EntryArrow.ArrowTipSideLength; // Lower bound for Y filtering; offset should correspond to the vertical size of one arrow
                EntryArrow newlyHoveredElement = null;
                foreach(EntryArrow entryArrow in _entryArrows)
                {
                    // Check Y coordinate for fast filtering
                    if(entryArrow.PositionY < minimumYValueForMatch)
                        continue;

                    // The list is sorted, so we can stop if the desired Y value is exceeded
                    if(entryArrow.PositionY > mousePosition.Y)
                        break;

                    // The entry is a candidate, now check X coordinate
                    double left;
                    double right;
                    if(entryArrow.From == entryArrow.To)
                    {
                        // ==
                        left = entryArrow.From.CenterXPosition - EntryArrow.ArrowTipSideLength;
                        right = entryArrow.From.CenterXPosition + EntryArrow.ArrowTipSideLength;
                    }
                    else if(entryArrow.From.CenterXPosition < entryArrow.To.CenterXPosition)
                    {
                        // -->
                        left = entryArrow.From.CenterXPosition;
                        right = entryArrow.To.CenterXPosition;
                    }
                    else
                    {
                        // <--
                        left = entryArrow.To.CenterXPosition;
                        right = entryArrow.From.CenterXPosition;
                    }
                    if(mousePosition.X < left || right < mousePosition.X)
                        continue;

                    // Hit!
                    newlyHoveredElement = entryArrow;
                    break;
                }

                // Deselect currently hovered element
                if(_hoveredEntryArrow != null && _hoveredEntryArrow != newlyHoveredElement)
                {
                    // Deselect currently hovered element
                    _hoveredEntryArrow.Selected = false;
                    _renderPanel.Children.Remove(_hoveredEntryArrow);
                    _renderPanel.Children.Add(_hoveredEntryArrow);
                    _hoveredEntryArrow = null;

                    _debugLabel.Content = "";
                }

                // Show annotation
                if(newlyHoveredElement != _hoveredEntryArrow)
                {
                    // Select element
                    _hoveredEntryArrow = newlyHoveredElement;
                    _hoveredEntryArrow.Selected = true;
                    _renderPanel.Children.Remove(_hoveredEntryArrow);
                    _renderPanel.Children.Add(_hoveredEntryArrow);

                    _debugLabel.Content = $"[{newlyHoveredElement.TraceFileEntryIndex}] {newlyHoveredElement.TraceFileEntry.ToString()}";
                }

                // Try to find diff area
                DiffArea hoveredDiffArea = null;
                foreach(DiffArea diffArea in _diffAreas)
                {
                    // Check Y coordinate
                    if(diffArea.PositionY <= mousePosition.Y && mousePosition.Y <= diffArea.PositionY + diffArea.Height)
                    {
                        // Hit
                        hoveredDiffArea = diffArea;
                        break;
                    }
                }

                // Show hover text if a diff area was found
                if(hoveredDiffArea == null)
                    _resultLabel.Content = "";
                else
                    _resultLabel.Content = hoveredDiffArea.Description;
            }
        }

        private void _renderPanelContainer_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            // Update render data
            UpdateRenderedElements();
        }

        private void _openTrace1FileNameDialog_Click(object sender, RoutedEventArgs e)
        {
            // Show dialog
            string preSelectedFileName = Properties.Settings.Default.Trace1FileName;
            if(!File.Exists(preSelectedFileName))
                preSelectedFileName = "";
            OpenFileDialog dialog = new OpenFileDialog()
            {
                CheckFileExists = true,
                Filter = "Preprocessed trace files (*.trace.processed)|*.trace.processed",
                Title = "Open trace file...",
                FileName = preSelectedFileName
            };
            if(dialog.ShowDialog() ?? false)
                Properties.Settings.Default.Trace1FileName = dialog.FileName;
        }

        private void _openTrace2FileNameDialog_Click(object sender, RoutedEventArgs e)
        {
            // Show dialog
            string preSelectedFileName = Properties.Settings.Default.Trace2FileName;
            if(!File.Exists(preSelectedFileName))
                preSelectedFileName = "";
            OpenFileDialog dialog = new OpenFileDialog()
            {
                CheckFileExists = true,
                Filter = "Preprocessed trace files (*.trace.processed)|*.trace.processed",
                Title = "Open trace file...",
                FileName = preSelectedFileName
            };
            if(dialog.ShowDialog() ?? false)
                Properties.Settings.Default.Trace2FileName = dialog.FileName;
        }

        private void _openMapFileNameDialog_Click(object sender, RoutedEventArgs e)
        {
            // Show dialog
            string preSelectedFileName = Properties.Settings.Default.MapFileName;
            if(!File.Exists(preSelectedFileName))
                preSelectedFileName = "";
            OpenFileDialog dialog = new OpenFileDialog()
            {
                CheckFileExists = true,
                Filter = "Map files (*.map)|*.map",
                Title = "Open map file...",
                FileName = preSelectedFileName
            };
            if(dialog.ShowDialog() ?? false)
                Properties.Settings.Default.MapFileName = dialog.FileName;
        }

        private void _scrollToFunctionTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            // ENTER pressed?
            if(e.Key == Key.Enter && !string.IsNullOrWhiteSpace(_scrollToFunctionTextBox.Text))
            {
                // Find function with given name
                FunctionNode functionNode = _functionNodes.FirstOrDefault(fn => fn.FunctionName.Contains(_scrollToFunctionTextBox.Text));
                if(functionNode != null)
                {
                    // Scroll horizontally
                    _renderPanelContainer.ScrollToHorizontalOffset(_renderPanel.LayoutTransform.Transform(new Point(functionNode.CenterXPosition, 0)).X);
                }
            }
        }

        #endregion
    }
}
