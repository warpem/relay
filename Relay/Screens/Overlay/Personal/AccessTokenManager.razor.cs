using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;
using Refund.DataModel;
using Refund.Services;
using Refund.Services.Core.Session;

namespace Relay.Screens.Overlay.Personal;

public partial class AccessTokenManager : ComponentBase
{
    [Inject] private PersonalAccessTokenService Pats { get; set; } = default!;
    [Inject] private RelaySession Session { get; set; } = default!;
    [Inject] private IToastService ToastService { get; set; } = default!;
    [Inject] private IJSRuntime JsRuntime { get; set; } = default!;

    private IReadOnlyList<PersonalAccessToken> _tokens = [];
    private bool _showCreate;
    private string _newName = "";
    private string? _createdRawToken; // shown once after creation

    protected override void OnInitialized() => Refresh();

    private void Refresh() => _tokens = Pats.ListForUser(Session.User.Id);

    private void OpenCreate()
    {
        _newName = "";
        _createdRawToken = null;
        _showCreate = true;
    }

    private async Task CreateToken()
    {
        if (string.IsNullOrWhiteSpace(_newName))
        {
            ToastService.ShowError("Please enter a name for the token.");
            return;
        }
        try
        {
            _createdRawToken = await Pats.Generate(Session.User.Id, _newName.Trim());
            Refresh();
        }
        catch (Exception exc)
        {
            ToastService.ShowError("Couldn't create token: " + exc.Message);
        }
    }

    private async Task CopyToken()
    {
        if (_createdRawToken == null) return;
        try
        {
            await JsRuntime.InvokeVoidAsync("navigator.clipboard.writeText", _createdRawToken);
            ToastService.ShowSuccess("Token copied to clipboard");
        }
        catch (Exception exc)
        {
            ToastService.ShowError("Couldn't copy token: " + exc.Message);
        }
    }

    private void CloseCreate()
    {
        _showCreate = false;
        _createdRawToken = null;
    }

    private async Task RevokeToken(int tokenId)
    {
        try
        {
            await Pats.Revoke(Session.User.Id, tokenId);
            Refresh();
        }
        catch (Exception exc)
        {
            ToastService.ShowError("Couldn't revoke token: " + exc.Message);
        }
    }

    private static string FormatLastUsed(DateTime? dt) =>
        dt is null ? "Never" : dt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    private static string FormatExpiry(DateTime? dt) =>
        dt is null ? "—" : dt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    private static string FormatCreated(DateTime dt) =>
        dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
}
