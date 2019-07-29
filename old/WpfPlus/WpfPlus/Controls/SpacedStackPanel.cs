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
    /// StackPanel that adds margins to its child elements to simulate row or column spacing.
    /// </summary>
    public class SpacedStackPanel : StackPanel
    {
        private const int DefaultHorizontalSpacing = 6;
        private const int DefaultVerticalSpacing = 6;

        /// <summary>
        /// Amount of Pixels between each column. Only relevant when <see cref="Orientation"/> is Horizontal.
        /// </summary>
        public static readonly DependencyProperty HorizontalSpacingProperty = DependencyProperty.Register(nameof(HorizontalSpacing), typeof(int), typeof(SpacedGrid),
            new FrameworkPropertyMetadata(DefaultHorizontalSpacing, FrameworkPropertyMetadataOptions.AffectsArrange | FrameworkPropertyMetadataOptions.AffectsMeasure));

        /// <summary>
        /// Amount of pixels between each row. Only relevant when <see cref="Orientation"/> is Vertical.
        /// </summary>
        public static readonly DependencyProperty VerticalSpacingProperty = DependencyProperty.Register(nameof(VerticalSpacing), typeof(int), typeof(SpacedGrid),
            new FrameworkPropertyMetadata(DefaultVerticalSpacing, FrameworkPropertyMetadataOptions.AffectsArrange | FrameworkPropertyMetadataOptions.AffectsMeasure));

        /// <summary>
        /// Amount of Pixels between each column. Only relevant when Orientation is Horizontal.
        /// </summary>
        public int HorizontalSpacing
        {
            get { return (int) GetValue(HorizontalSpacingProperty); }
            set
            {
                SetValue(HorizontalSpacingProperty, value);
                UpdateChildMargins();
            }
        }

        /// <summary>
        /// Amount of pixels between each row. Only relevant when Orientation is Vertical.
        /// </summary>
        public int VerticalSpacing
        {
            get { return (int) GetValue(VerticalSpacingProperty); }
            set
            {
                SetValue(VerticalSpacingProperty, value);
                UpdateChildMargins();
            }
        }

        public SpacedStackPanel()
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
            for (int i = 0; i < InternalChildren.Count; i++)
            {
                UIElement child = InternalChildren[i];
                if (!(child is FrameworkElement))
                    continue;

                FrameworkElement element = (FrameworkElement) child;

                double marginLeft = 0;
                double marginTop = 0;
                double marginRight = 0;
                double marginBotom = 0;

                if (Orientation == Orientation.Horizontal)
                {
                    marginLeft = i > 0 ? 0.5 : 0;
                    marginRight = i < InternalChildren.Count - 1 ? 0.5 : 0;
                }
                else
                {
                    marginTop = i > 0 ? 0.5 : 0;
                    marginBotom = i < InternalChildren.Count - 1 ? 0.5 : 0;
                }

                element.Margin = new Thickness(marginLeft * HorizontalSpacing, marginTop * VerticalSpacing, marginRight * HorizontalSpacing, marginBotom * VerticalSpacing);
            }
        }
    }
}