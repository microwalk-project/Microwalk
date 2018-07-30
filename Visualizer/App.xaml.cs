using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace Visualizer
{
    /// <summary>
    /// Main class.
    /// </summary>
    public partial class App : Application
    {
        private void Application_Exit(object sender, ExitEventArgs e)
        {
            // Save settings
            Visualizer.Properties.Settings.Default.Save();
        }
    }
}
