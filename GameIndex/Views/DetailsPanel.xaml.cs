using GameIndex.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GameIndex.Views;

public sealed partial class DetailsPanel : UserControl
{
    public MainViewModel Vm { get; private set; } = null!;
    public DetailsPanel() { }   // InitializeComponent runs in Initialize, once Vm exists (x:Bind)

    public void Initialize(MainViewModel vm) { Vm = vm; InitializeComponent(); }
    public void SelectTab(int index) { if (index >= 0 && index < Tabs.Items.Count) Tabs.SelectedIndex = index; }
    private void Close_Click(object sender, RoutedEventArgs e) => Vm.CloseDetails();
}
