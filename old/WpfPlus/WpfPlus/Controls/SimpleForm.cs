using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace WpfPlus.Controls
{
    /// <summary>
    /// Grid that automatically splits the childs into rows to make creating forms easier.
    /// </summary>
    public class SimpleForm : SpacedGrid
    {
        protected override Size MeasureOverride(Size constraint)
        {
            UpdateRowDefinitions();
            return base.MeasureOverride(constraint);
        }

        private void UpdateRowDefinitions()
        {
            int lastColumn = -1;
            int currentRow = -1;

            foreach (UIElement child in InternalChildren)
            {
                int childColumn = GetColumn(child);
                if (childColumn <= lastColumn || lastColumn == -1)
                {
                    currentRow++;

                    if (RowDefinitions.Count < currentRow + 1)
                        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                }

                lastColumn = childColumn;
                SetRow(child, currentRow);

                FrameworkElement control = child as FrameworkElement;
                if (control != null)
                    control.VerticalAlignment = VerticalAlignment.Center;
            }
        }
    }
}