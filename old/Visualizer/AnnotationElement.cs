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
    /// <summary>
    /// Represents a non-scaling annotation element.
    /// </summary>
    class AnnotationElement : UIElement
    {
        /// <summary>
        /// The underlying visual that is rendered.
        /// </summary>
        private Visual _visual = null;

        /// <summary>
        /// Returns the size of this element.
        /// </summary>
        public Size Size { get; private set; }

        /// <summary>
        /// Returns the top left coordinate of this element.
        /// </summary>
        public Point TopLeft { get; private set; }

        /// <summary>
        /// Creates a new annotation element with the given content.
        /// </summary>
        /// <param name="content">The text to be displayed.</param>
        public AnnotationElement(string content)
            : base()
        {
            // Disable hit testing
            IsHitTestVisible = false;

            // Start drawing
            DrawingVisual drawing = new DrawingVisual();
            DrawingContext drawingContext = drawing.RenderOpen();

            // Draw text vertically
            drawingContext.PushTransform(new RotateTransform(90));
            FormattedText txt = new FormattedText(content, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(new FontFamily("Tahoma"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 20, Brushes.Gray);
            drawingContext.DrawText(txt, new Point(0, 0));
            drawingContext.Pop();

            // Finish drawing
            drawingContext.Close();

            // Save visual
            _visual = drawing;

            // Calculate size
            Size = drawing.ContentBounds.Size;
            TopLeft = drawing.ContentBounds.TopLeft;
        }

        #region Internal methods for rendering
        protected override int VisualChildrenCount => _visual != null ? 1 : 0;
        protected override Visual GetVisualChild(int index) => _visual;
        #endregion
    }
}
