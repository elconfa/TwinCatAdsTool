using System.Windows.Controls;

namespace TwinCatAdsTool.Gui.Views.Pages
{
    /// <summary>
    /// Navigation host for the help view. Unlike the other pages it needs no view model: it holds
    /// no state and talks to no plc.
    /// </summary>
    public partial class HelpPage : Page
    {
        public HelpPage()
        {
            InitializeComponent();
        }
    }
}
