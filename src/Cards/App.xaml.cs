using Microsoft.Extensions.DependencyInjection;

namespace Cards;

/// <summary>
/// The phone app itself. Not "App": the shared Cards.App project's namespace is visible
/// here too (through Cards.Rendering), and Cards.App named both.
/// </summary>
public partial class CardsApp : Application
{
	public CardsApp()
	{
		InitializeComponent();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		return new Window(new AppShell());
	}
}