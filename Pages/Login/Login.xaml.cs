using Microsoft.Maui.Controls;
namespace JRoute.Pages.Login

{
	public partial class Login : ContentPage
	{
		public Login()
		{
			InitializeComponent();
			this.SizeChanged += OnPageSizeChanged;
			ConductorButton.Clicked += OnConductorClicked;
			CommuterButton.Clicked += OnCommuterClicked;
		}

		private void OnPageSizeChanged(object? sender, EventArgs e)
		{
			if (this.Width > 0)
			{
				// Header image: 60% width
				if (DavaoImage != null)
					DavaoImage.WidthRequest = this.Width * 0.6;

				// Buttons: 70% width
				if (ConductorButton != null)
					ConductorButton.WidthRequest = this.Width * 0.7;
				if (CommuterButton != null)
					CommuterButton.WidthRequest = this.Width * 0.7;

				// Footer labels: 80% width
				if (FooterTitle != null)
					FooterTitle.WidthRequest = this.Width * 0.8;
				if (FooterSubtitle != null)
					FooterSubtitle.WidthRequest = this.Width * 0.4;
			}
		}

		private async void OnConductorClicked(object? sender, EventArgs e)
		{
			await Navigation.PushAsync(new JRoute.Pages.Conductor.Conductor());
		}

		private async void OnCommuterClicked(object? sender, EventArgs e)
		{
			await Navigation.PushAsync(new JRoute.Pages.Commuter.Commuter());
		}
	}
}