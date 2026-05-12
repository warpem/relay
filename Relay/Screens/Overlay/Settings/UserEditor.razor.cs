using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.Services;
using Refund.Services.Core.DataManager;
using Refund.Services.Core.Session;
using Warp.Tools;

namespace Relay.Screens.Overlay.Settings;

public partial class UserEditor : ComponentBase, IDisposable
{
    private readonly IEnumerable<string> _roles = Enum.GetNames(typeof(UserRole));
    private readonly Dictionary<string, string> _errorMessages = new();
    private ReadOnlyUser _errorsForUser = null;
    private Dictionary<(ReadOnlyUser user, string fieldName), string> _draftValues = new();

    [Inject]
    private DataManager DataManager { get; set; }

    [Inject]
    private RelaySession Session { set; get; }

    [Inject]
    private IToastService ToastService { get; set; }

    [Inject]
    private SecurityTokenService TokenService { get; set; }

    [Inject]
    private IJSRuntime JsRuntime { get; set; }

    private readonly List<GroupEventSubscription> _subscriptions = new();

    protected override void OnInitialized()
    {
        foreach (var sub in _subscriptions)
            sub.Unsubscribe();

        _subscriptions.Clear();

        _subscriptions.Add(DataManager.UserUpdated.Add(GroupName.User(null), DataHasBeenUpdatedAsync));
        _subscriptions.Add(DataManager.UserDeleted.Add(GroupName.User(null), DataHasBeenUpdatedAsync));
        _subscriptions.Add(DataManager.UserCreated.Add(GroupName.User(null), DataHasBeenUpdatedAsync));
    }

    private async Task DataHasBeenUpdatedAsync(GroupEventArgs<ReadOnlyUser> args)
    {
        // Clear any draft values for this user when backend data changes
        _draftValues.Clear();

        await InvokeAsync(StateHasChanged);
    }

    #region Token handling

    private async Task GenerateToken()
    {
        try
        {
            var token = await TokenService.GenerateToken(Session.User);
            await JsRuntime.InvokeVoidAsync("navigator.clipboard.writeText", token.Token);
            ToastService.ShowSuccess("Registration token copied to clipboard");
        }
        catch (Exception ex)
        {
            ToastService.ShowError($"Failed to generate token: {ex.Message}");
        }
    }

    private async Task InvalidateTokens()
    {
        try
        {
            await TokenService.InvalidateAllTokens();
            ToastService.ShowWarning("All registration tokens have been invalidated");
        }
        catch (Exception ex)
        {
            ToastService.ShowError($"Failed to invalidate tokens: {ex.Message}");
        }
    }

    #endregion

    #region Draft Value Management

    private string GetFieldValue(ReadOnlyUser user, string fieldName)
    {
        if (_draftValues.TryGetValue((user, fieldName), out var draftValue))
            return draftValue;

        return fieldName switch
        {
            nameof(ReadOnlyUser.Username) => user.Username, nameof(ReadOnlyUser.Name) => user.Name, nameof(ReadOnlyUser.Email) => user.Email, _ => null
        };
    }

    private void SetDraftValue(ReadOnlyUser user, string fieldName, string value)
    {
        _draftValues[(user, fieldName)] = value;
    }

    private void ClearDraftValue(ReadOnlyUser user, string fieldName)
    {
        _draftValues.Remove((user, fieldName));
    }

    private void ClearAllDraftValues(ReadOnlyUser user)
    {
        var userDraftKeys = _draftValues.Keys.Where(k => k.user == user).ToList();

        foreach (var key in userDraftKeys)
            _draftValues.Remove(key);
    }

    #endregion

    #region Error Handling

    private void AddError(ReadOnlyUser user, string fieldName, string message)
    {
        _errorsForUser = user;
        _errorMessages[fieldName] = message;
    }

    private void ClearErrors()
    {
        _errorMessages.Clear();
        _errorsForUser = null;
    }

    private bool HasErrors(ReadOnlyUser user, string fieldName)
        => _errorsForUser == user && _errorMessages.ContainsKey(fieldName);

    #endregion

    #region Field Change Handlers

    private async Task OnUsernameChanged(string newValue, ReadOnlyUser user)
    {
        newValue = newValue?.Trim();

        try
        {
            SetDraftValue(user, nameof(ReadOnlyUser.Username), newValue);

            if (string.IsNullOrWhiteSpace(newValue))
            {
                AddError(user, nameof(ReadOnlyUser.Username), "Username is required.");
                await InvokeAsync(StateHasChanged);

                return;
            }

            if (newValue.Contains(" "))
            {
                AddError(user, nameof(ReadOnlyUser.Username), "Username cannot contain spaces.");
                await InvokeAsync(StateHasChanged);

                return;
            }

            if (DataManager.Users.Any(u => u != user && u.Username.Equals(newValue, StringComparison.OrdinalIgnoreCase)))
            {
                AddError(user, nameof(ReadOnlyUser.Username), "This username already exists.");
                await InvokeAsync(StateHasChanged);

                return;
            }

            await DataManager.UpdateUser(user, dbUser => dbUser.Username = newValue);

            ClearDraftValue(user, nameof(ReadOnlyUser.Username));
            _errorMessages.Remove(nameof(ReadOnlyUser.Username));
        }
        catch (Exception ex)
        {
            ToastService.ShowError($"Couldn't update username: {ex.Message}");
        }
    }

    private async Task OnNameChanged(string newValue, ReadOnlyUser user)
    {
        newValue = newValue?.Trim();

        try
        {
            SetDraftValue(user, nameof(ReadOnlyUser.Name), newValue);

            if (string.IsNullOrWhiteSpace(newValue))
            {
                AddError(user, nameof(ReadOnlyUser.Name), "Name is required.");
                await InvokeAsync(StateHasChanged);

                return;
            }

            await DataManager.UpdateUser(user, dbUser => dbUser.Name = newValue);

            ClearDraftValue(user, nameof(ReadOnlyUser.Name));
            _errorMessages.Remove(nameof(ReadOnlyUser.Name));
        }
        catch (Exception ex)
        {
            ToastService.ShowError($"Couldn't update name: {ex.Message}");
        }
    }

    private async Task OnEmailChanged(string newValue, ReadOnlyUser user)
    {
        newValue = newValue?.Trim();

        try
        {
            SetDraftValue(user, nameof(ReadOnlyUser.Email), newValue);

            if (string.IsNullOrWhiteSpace(newValue))
            {
                AddError(user, nameof(ReadOnlyUser.Email), "Email is required.");
                await InvokeAsync(StateHasChanged);

                return;
            }

            if (DataManager.Users.Any(u => u != user && u.Email.Equals(newValue, StringComparison.OrdinalIgnoreCase)))
            {
                AddError(user, nameof(ReadOnlyUser.Email), "This email already exists.");
                await InvokeAsync(StateHasChanged);

                return;
            }

            await DataManager.UpdateUser(user, dbUser => dbUser.Email = newValue);

            ClearDraftValue(user, nameof(ReadOnlyUser.Email));
            _errorMessages.Remove(nameof(ReadOnlyUser.Email));
        }
        catch (Exception ex)
        {
            ToastService.ShowError($"Couldn't update email: {ex.Message}");
        }
    }

    private async Task OnRoleChanged(string newValue, ReadOnlyUser user)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(newValue))
            {
                AddError(user, nameof(ReadOnlyUser.Role), "Role is required.");
                await InvokeAsync(StateHasChanged);

                return;
            }

            var parsedRole = Enum.Parse<UserRole>(newValue);
            await DataManager.UpdateUser(user, dbUser => dbUser.Role = parsedRole);

            _errorMessages.Remove(nameof(ReadOnlyUser.Role));
        }
        catch (Exception ex)
        {
            ToastService.ShowError($"Couldn't update role: {ex.Message}");
        }
    }

    #endregion
    
    #region Reset password

    private async Task ResetPassword(ReadOnlyUser user)
    {
        try
        {
            // Generate random 8-char password
            const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            var password = new string(Helper.ArrayOfFunction(s => chars[Random.Shared.Next(chars.Length)], 8));

            // Update user's password hash
            await DataManager.UpdateUser(user, originalUser =>
            {
                originalUser.PasswordHash = User.HashPassword(password);
            });

            // Copy to clipboard
            await JsRuntime.InvokeVoidAsync("navigator.clipboard.writeText", password);

            // Notify user
            ToastService.ShowSuccess($"Password reset for {user.Username}. New password copied to clipboard.");
        }
        catch (Exception ex)
        {
            ToastService.ShowError($"Failed to reset password: {ex.Message}");
        }
    }
    
    #endregion

    #region Delete Handling

    private async Task DeleteUser(ReadOnlyUser user)
    {
        if (user != null)
        {
            try
            {
                await DataManager.DeleteUser(user);
                ClearAllDraftValues(user);
                ClearErrors();
            }
            catch (Exception ex)
            {
                ToastService.ShowError($"Couldn't delete user: {ex.Message}");
            }
        }
    }

    #endregion

    public void Dispose()
    {
        foreach (var sub in _subscriptions)
            sub.Unsubscribe();

        _subscriptions.Clear();
        _draftValues.Clear();
    }
}