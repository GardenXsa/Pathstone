using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MyGame.Desktop.Views;

/// <summary>
/// Code-behind for the onboarding (first-run welcome wizard) view
/// (issue #73). All state + navigation is data-bound to
/// <see cref="MyGame.Desktop.ViewModels.OnboardingViewModel"/>; this
/// class is just the XAML loader required by Avalonia's
/// <c>InitializeComponent</c> mechanism.
/// </summary>
public partial class OnboardingView : UserControl
{
    public OnboardingView()
    {
        InitializeComponent();
    }
}
