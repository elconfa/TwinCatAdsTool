using System.Windows.Controls;

namespace TwinCatAdsTool.Gui.Views.Pages
{
    /// <summary>
    /// Navigation host for the compare view. The data context is supplied by
    /// <see cref="NavigationPageProvider"/>: a page navigated to inside a frame does not inherit
    /// the one of the window.
    /// </summary>
    public partial class ComparePage : Page
    {
        public ComparePage()
        {
            InitializeComponent();
        }
    }
}
