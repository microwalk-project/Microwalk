using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace Visualizer
{
    internal class FunctionNode : UIElement
    {
        /// <summary>
        /// The underlying visual that is rendered.
        /// </summary>
        private Visual _visual = null;

        /// <summary>
        /// The formatted text to be rendered.
        /// </summary>
        private FormattedText _formattedFunctionName;

        /// <summary>
        /// Returns the function name being rendered.
        /// </summary>
        public string FunctionName => _formattedFunctionName.Text;

        /// <summary>
        /// Returns the top left position of this element.
        /// </summary>
        public Point Position { get; private set; }

        /// <summary>
        /// Returns the center X coordinate of this element.
        /// </summary>
        public double CenterXPosition { get; private set; }

        /// <summary>
        /// Returns the horizontal size of this element.
        /// </summary>
        public double Width { get; private set; }

        /// <summary>
        /// Creates a new function node with the given text and position.
        /// </summary>
        /// <param name="functionName">The function name to be displayed.</param>
        /// <param name="position">The position of this node.</param>
        public FunctionNode(string functionName, Point position)
            : base()
        {
            // Format text
            _formattedFunctionName = new FormattedText(functionName, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(new FontFamily("Tahoma"), FontStyles.Normal, FontWeights.Light, FontStretches.Normal), 10, Brushes.Black);

            // Set size values
            Position = position;
            Width = 100;// _formattedFunctionName.Width + 10;
            CenterXPosition = position.X + Width / 2;

            // Disable hit testing
            IsHitTestVisible = false;
        }

        /// <summary>
        /// Generates the underlying renderable visual. This method must be called before assigning this node to a parent element.
        /// </summary>
        /// <param name="verticalLength">The desired length of the line below this node.</param>
        public void GenerateVisual(double verticalLength)
        {
            // Start drawing
            DrawingVisual drawing = new DrawingVisual();
            DrawingContext drawingContext = drawing.RenderOpen();

            // Draw header
            /*drawingContext.DrawRectangle(Brushes.White, new Pen(Brushes.Black, 2), new Rect(0, 0, Width, 20));
            drawingContext.DrawText(_formattedFunctionName, new Point(5, 5));*/

            // Draw vertical line
            drawingContext.DrawRectangle(Brushes.DarkGray, new Pen(Brushes.DarkGray, 2), new Rect(Width / 2 - 2, 20, 4, verticalLength));

            // Finish drawing
            drawingContext.Close();

            // Save drawing
            _visual = drawing;
        }

        #region Internal methods for rendering
        protected override int VisualChildrenCount => _visual != null ? 1 : 0;
        protected override Visual GetVisualChild(int index) => _visual;
        #endregion
    }
}
