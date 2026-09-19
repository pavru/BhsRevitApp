using System.Windows.Controls;

namespace BHS.MEP.Cabling.Ui;

/// <summary>The inspector pane's content element. All it knows is its view model.</summary>
public partial class InspectorView : UserControl
{
    public InspectorView(InspectorViewModel model)
    {
        InitializeComponent();
        DataContext = model ?? throw new ArgumentNullException(nameof(model));
    }
}
