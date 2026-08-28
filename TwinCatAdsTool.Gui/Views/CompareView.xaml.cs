using System.Windows.Controls;

namespace TwinCatAdsTool.Gui.Views
{
    /// <summary>
    /// Interaction logic for CompareView.xaml
    /// </summary>
    public partial class CompareView : UserControl
    {
        public CompareView()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Brings the picked row into view. Moving between differences is done from the view model,
        /// which knows which row comes next but has no way of scrolling to it - and a grid of a
        /// hundred thousand rows is virtualised, so the row that was picked usually does not exist
        /// as a control until it has been scrolled to.
        /// </summary>
        private void RowSelected(object sender, SelectionChangedEventArgs e)
        {
            if (DifferenceGrid.SelectedItem != null)
            {
                DifferenceGrid.ScrollIntoView(DifferenceGrid.SelectedItem);
            }
        }
    }
}
