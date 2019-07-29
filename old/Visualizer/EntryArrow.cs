using LeakageDetector;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace Visualizer
{
    internal class EntryArrow : UIElement
    {
        /// <summary>
        /// The length of one side of an arrow tip.
        /// </summary>
        public const double ArrowTipSideLength = 14;

        /// <summary>
        /// The underlying visual that is normally rendered.
        /// </summary>
        private Visual _visual = null;

        /// <summary>
        /// The underlying visual that is rendered when the element is selected;
        /// </summary>
        private Visual _visualSelected = null;

        /// <summary>
        /// Returns the vertical position of this arrow.
        /// </summary>
        public double PositionY { get; private set; }

        /// <summary>
        /// Returns the source function node of this arrow.
        /// </summary>
        public FunctionNode From { get; private set; }

        /// <summary>
        /// Returns the destination function node of this arrow.
        /// </summary>
        public FunctionNode To { get; private set; }

        /// <summary>
        /// Returns the associated trace file <see cref="BranchEntry"/>.
        /// </summary>
        public BranchEntry TraceFileEntry { get; private set; }

        /// <summary>
        /// Returns the index of this entry. This value corresponds to the mismatch index output by the comparison functions.
        /// </summary>
        public int TraceFileEntryIndex { get; private set; }

        /// <summary>
        /// Returns the ID of the associated trace file ({1, 2}). Used for determining the coloring of this entry.
        /// </summary>
        public int TraceFileId { get; private set; }

        /// <summary>
        /// Returns or sets whether this arrow is selected.
        /// </summary>
        public bool Selected { get; set; }

        /// <summary>
        /// Creates a new entry arrow from the given source function node to the given destination function node.
        /// </summary>
        /// <param name="from">The source function node of the arrow.</param>
        /// <param name="to">The destination function node of the arrow.</param>
        /// <param name="positionY">The vertical position of the arrow.</param>
        /// <param name="traceFileEntry">The associated trace file entry.</param>
        /// <param name="traceFileEntryIndex">The index of this entry. This value should correspond to the mismatch index output by the comparison functions.</param>
        /// <param name="traceFileId">The ID of the associated trace file ({1, 2}). Used for determining the coloring of this entry.</param>
        public EntryArrow(FunctionNode from, FunctionNode to, double positionY, BranchEntry traceFileEntry, int traceFileEntryIndex, int traceFileId)
            : base()
        {
            // Save parameters
            PositionY = positionY;
            From = from;
            To = to;
            TraceFileEntry = traceFileEntry;
            TraceFileEntryIndex = traceFileEntryIndex;
            TraceFileId = traceFileId;

            // Disable hit testing
            IsHitTestVisible = false;

            // Create visuals
            _visual = CreateArrowVisual(false);
            _visualSelected = CreateArrowVisual(true);
        }

        /// <summary>
        /// Creates an arrow drawing.
        /// </summary>
        /// <param name="selected">Sets whether the arrow should be highlighted.</param>
        /// <returns></returns>
        private DrawingVisual CreateArrowVisual(bool selected)
        {
            // Start drawing
            DrawingVisual drawing = new DrawingVisual();
            DrawingContext drawingContext = drawing.RenderOpen();

            // Derive style by branch type
            Brush brush = Brushes.Gray;
            Pen linePen = new Pen(Brushes.Gray, 2);
            if(TraceFileEntry.BranchType == BranchTypes.Call)
            {
                // Solid blue line
                if(TraceFileId == 1)
                {
                    brush = Brushes.Blue;
                    linePen = new Pen(Brushes.Blue, 2);
                }
                else if(TraceFileId == 2)
                {
                    brush = Brushes.Firebrick;
                    linePen = new Pen(Brushes.Firebrick, 2);
                }
            }
            else if(TraceFileEntry.BranchType == BranchTypes.Ret)
            {
                // Dashed blue line
                if(TraceFileId == 1)
                {
                    brush = Brushes.Blue;
                    linePen = new Pen(Brushes.Blue, 2)
                    {
                        DashStyle = DashStyles.Dash
                    };
                }
                else if(TraceFileId == 2)
                {
                    brush = Brushes.Firebrick;
                    linePen = new Pen(Brushes.Firebrick, 2)
                    {
                        DashStyle = DashStyles.Dash
                    };
                }
            }
            else // Jump
            {
                // Solid red line
                if(TraceFileId == 1)
                {
                    brush = Brushes.RoyalBlue;
                    linePen = new Pen(Brushes.RoyalBlue, 2);
                }
                else if(TraceFileId == 2)
                {
                    brush = Brushes.Red;
                    linePen = new Pen(Brushes.Red, 2);
                }
            }

            // Highlight?
            if(selected)
                linePen.Thickness = 4;

            // Draw arrows for non-function internal branches, else boxes
            if(From != To)
            {
                // Prepare arrow head
                Point arrowTipPosition = new Point(To.CenterXPosition - From.CenterXPosition, ArrowTipSideLength / 2);
                Point p1 = new Point(arrowTipPosition.X > 0 ? arrowTipPosition.X - ArrowTipSideLength : arrowTipPosition.X + ArrowTipSideLength, arrowTipPosition.Y - ArrowTipSideLength / 2);
                Point p2 = new Point(arrowTipPosition.X > 0 ? arrowTipPosition.X - ArrowTipSideLength : arrowTipPosition.X + ArrowTipSideLength, arrowTipPosition.Y + ArrowTipSideLength / 2);
                Point p3 = new Point(arrowTipPosition.X, arrowTipPosition.Y);
                StreamGeometry arrowTip = new StreamGeometry();
                using(StreamGeometryContext geometryContext = arrowTip.Open())
                {
                    geometryContext.BeginFigure(p1, true, true);
                    geometryContext.PolyLineTo(new PointCollection { p2, p3 }, true, true);
                }
                arrowTip.Freeze();

                // Draw arrow (line is slightly shorter that needed, to prevent it from distorting the arrow tip)
                double lineLength = To.CenterXPosition - From.CenterXPosition + (arrowTipPosition.X > 0 ? -ArrowTipSideLength / 2 : ArrowTipSideLength / 2);
                drawingContext.DrawLine(linePen, new Point(0, ArrowTipSideLength / 2), new Point(lineLength, ArrowTipSideLength / 2));
                drawingContext.DrawGeometry(brush, new Pen(brush, 1), arrowTip);
            }
            else
            {
                // Draw filled rectangle
                drawingContext.DrawRectangle(brush, linePen, new Rect(-ArrowTipSideLength, 0, 2 * ArrowTipSideLength, ArrowTipSideLength));
            }

            // Finish drawing
            drawingContext.Close();
            return drawing;
        }

        #region Internal methods for rendering
        protected override int VisualChildrenCount => _visual != null ? 1 : 0;
        protected override Visual GetVisualChild(int index) => Selected ? _visualSelected : _visual;
        #endregion
    }
}
