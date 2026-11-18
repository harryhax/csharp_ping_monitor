using System.Net.NetworkInformation;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace NetworkDiagnostics.App.Services;

/// <summary>Shows a save dialog and returns a writable stream, or null if the user cancelled.</summary>
public interface IFileSaveService
{
    Task<SaveTarget?> PickSaveTargetAsync(string title, string suggestedFileName, string fileTypeName, string extension);
}

public sealed record SaveTarget(string DisplayPath, Func<Task<Stream>> OpenWriteAsync);

public sealed class AvaloniaFileSaveService(TopLevel topLevel) : IFileSaveService
{
    public async Task<SaveTarget?> PickSaveTargetAsync(string title, string suggestedFileName, string fileTypeName, string extension)
    {
        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedFileName,
            DefaultExtension = extension,
            ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType(fileTypeName) { Patterns = ["*." + extension] }],
        });

        if (file is null)
        {
            return null;
        }

        return new SaveTarget(file.TryGetLocalPath() ?? file.Name, file.OpenWriteAsync);
    }
}

/// <summary>Marshals work to the UI thread. Abstracted so view models can be tested without Avalonia.</summary>
public interface IUiDispatcher
{
    void Post(Action action);
}

public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    public void Post(Action action) => Dispatcher.UIThread.Post(action);
}

/// <summary>Raised when the operating system reports an address or availability change.</summary>
public interface INetworkChangeNotifier
{
    event EventHandler? NetworkChanged;
}

public sealed class SystemNetworkChangeNotifier : INetworkChangeNotifier, IDisposable
{
    public SystemNetworkChangeNotifier()
    {
        try
        {
            NetworkChange.NetworkAddressChanged += OnChanged;
            NetworkChange.NetworkAvailabilityChanged += OnAvailability;
        }
        catch (Exception)
        {
            // Not available on every platform; manual Refresh still works.
        }
    }

    public event EventHandler? NetworkChanged;

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnAvailability;
    }

    private void OnChanged(object? sender, EventArgs e) => NetworkChanged?.Invoke(this, EventArgs.Empty);

    private void OnAvailability(object? sender, NetworkAvailabilityEventArgs e) => NetworkChanged?.Invoke(this, EventArgs.Empty);
}
