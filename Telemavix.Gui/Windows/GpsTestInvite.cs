using Avalonia.Controls;

namespace Telemavix.Gui;

internal static class GpsTestInvite
{
	public static async Task ShowAsync(Window owner)
	{
		if (await ConfirmDialog.AskAsync(owner, Strings.GpsTestInvite_Title, Strings.GpsTestInvite_Message, Strings.GpsTestInvite_Open, DialogKind.Info))
			AppUpdateFlow.OpenInBrowser(Strings.GpsTestInvite_Url);
	}
}
