using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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

namespace WpfPlus.Controls
{
    /// <summary>
    /// Grid that adds margins to its child elements to simulate row and column spacing.
    /// </summary>
    public class SpacedGrid : Grid
    {
        private const int DefaultColumnSpacing = 6;
        private const int DefaultRowSpacing = 6;

        /// <summary>
        /// Amount of Pixels between each column.
        /// </summary>
        public static readonly DependencyProperty ColumnSpacingProperty = DependencyProperty.Register(nameof(ColumnSpacing), typeof(int), typeof(SpacedGrid),
            new FrameworkPropertyMetadata(DefaultColumnSpacing, FrameworkPropertyMetadataOptions.AffectsArrange | FrameworkPropertyMetadataOptions.AffectsMeasure));

        /// <summary>
        /// Amount of pixels between each row.
        /// </summary>
        public static readonly DependencyProperty RowSpacingProperty = DependencyProperty.Register(nameof(RowSpacing), typeof(int), typeof(SpacedGrid),
            new FrameworkPropertyMetadata(DefaultRowSpacing, FrameworkPropertyMetadataOptions.AffectsArrange | FrameworkPropertyMetadataOptions.AffectsMeasure));

        /// <summary>
        /// Amount of Pixels between each column.
        /// </summary>
        public int ColumnSpacing
        {
            get { return (int)GetValue(ColumnSpacingProperty); }
            set
            {
                SetValue(ColumnSpacingProperty, value);
                UpdateChildMargins();
            }
        }

        /// <summary>
        /// Amount of pixels between each row.
        /// </summary>
        public int RowSpacing
        {
            get { return (int)GetValue(RowSpacingProperty); }
            set
            {
                SetValue(RowSpacingProperty, value);
                UpdateChildMargins();
            }
        }

        public SpacedGrid()
        {
            SnapsToDevicePixels = true;
        }

        protected override Size MeasureOverride(Size constraint)
        {
            UpdateChildMargins();

            return base.MeasureOverride(constraint);
        }

        private void UpdateChildMargins()
        {
            int columnCount = 0;
            int rowCount = 0;

            foreach (UIElement child in InternalChildren)
            {
                int endColumn = GetColumn(child) + GetColumnSpan(child);
                int endRow = GetRow(child) + GetRowSpan(child);

                columnCount = endColumn > columnCount ? endColumn : columnCount;
                rowCount = endRow > rowCount ? endRow : rowCount;
            }

            foreach (UIElement child in InternalChildren)
            {
                if (!(child is FrameworkElement))
                    continue;

                FrameworkElement element = (FrameworkElement)child;
                int elementColumn = GetColumn(element);
                int elementRow = GetRow(element);

                double marginLeft = elementColumn == 0 ? 0 : 0.5;
                double marginTop = elementRow == 0 ? 0 : 0.5;
                double marginRight = elementColumn + GetColumnSpan(element) >= columnCount ? 0 : 0.5;
                double marginBotom = elementRow + GetRowSpan(element) >= rowCount ? 0 : 0.5;

                element.Margin = new Thickness(marginLeft * ColumnSpacing, marginTop * RowSpacing, marginRight * ColumnSpacing, marginBotom * RowSpacing);
            }
        }
    }
}