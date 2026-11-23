using System.Threading.Tasks;
using CinecorePlayer2025.Utilities;

namespace CinecoreMac;

public sealed partial class MainWindow
{
    private bool _authorizingCredentials;
    private async Task AuthorizeSavedCredentialsAsync()
    {
        if (_authorizingCredentials) return;
        _authorizingCredentials = true;
        try
        {
            var authorization = Task.Run(Keychain.AuthorizePending);
            _settings.InvalidateVisual();
            _library.InvalidateVisual();
            await authorization;
            MovieMetadataService.ReloadUserTmdbApiKey();
            LibraryStore.RefreshMetadataRequests();
            if (!Keychain.AccessRequired) _library.RetrySavedAccess();
        }
        catch
        {
            // Le credenziali salvate e lo stato bloccato restano intatti. Nessun retry implicito.
        }
        finally
        {
            _authorizingCredentials = false;
            _settings.InvalidateVisual();
            _library.InvalidateVisual();
        }
    }
}
