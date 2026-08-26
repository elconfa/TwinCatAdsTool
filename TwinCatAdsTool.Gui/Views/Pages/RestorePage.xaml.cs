using System.Windows.Controls;

namespace TwinCatAdsTool.Gui.Views.Pages
{
    /// <summary>
    /// Navigation host for the restore view. The data context is supplied by
    /// <see cref="NavigationPageProvider"/>: a page navigated to inside a frame does not inherit
    /// the one of the window.
    /// </summary>
    public partial class RestorePage : Page
    {
        public RestorePage()
        {
            InitializeComponent();
        }
    }
}
