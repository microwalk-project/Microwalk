using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace Visualizer
{
    /// <summary>
    /// Draws a colored area, representing e.g. a diff section.
    /// </summary>
    internal class DiffArea : UIElement
    {
        /// <summary>
        /// The underlying visual that is rendered.
        /// </summary>
        private Visual _visual = null;

        /// <summary>
        /// Returns the description string.
        /// </summary>
        public string Description { get; }

        /// <summary>
        /// Returns the Y position.
        /// </summary>
        public double PositionY { get; }

        /// <summary>
        /// Returns the height;
        /// </summary>
        public double Height { get; }

        /// <summary>
        /// Creates a new colored area.
        /// </summary>
        /// <param name="width">The X length of the area (it starts at X = 0).</param>
        /// <param name="height">The Y length of the area.</param>
        /// <param name="brush">The color of the area.</param>
        /// <param name="description">A string containing information about this diff area (displayed when hovering).</param>
        /// <param name="positionY">The Y position of this diff area (only used for hovering).</param>
        public DiffArea(double width, double height, Brush brush, string description, double positionY)
        {
            // Save parameters
            Description = description;
            PositionY = positionY;
            Height = height;

            // Prevent this control from receiving mouse events
            IsHitTestVisible = false;

            // Start drawing
            DrawingVisual drawing = new DrawingVisual();
            DrawingContext drawingContext = drawing.RenderOpen();

            // Draw colored area
            drawingContext.DrawRectangle(brush, new Pen(brush, 1), new Rect(0, 0, width, height));

            // Finish drawing
            drawingContext.Close();
            _visual = drawing;
        }

        #region Internal methods for rendering
        protected override int VisualChildrenCount => _visual != null ? 1 : 0;
        protected override Visual GetVisualChild(int index) => _visual;
        #endregion
    }
}
