using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NtfyDesktop.Models;
using Windows.Storage;

namespace NtfyDesktop.Services;

/// <summary>
/// Loads and saves subscriptions, message history, and app settings as JSON
/// files in the packaged app's <see cref="ApplicationData.LocalFolder"/>.
/// Writes are serialized through a lock so concurrent stream callbacks can't
/// corrupt a file.
/// </summary>
public sealed class PersistenceService
{
    private const string SubscriptionsFile = "subscriptions.json";
    private const string MessagesFile = "messages.json";
    private const string SettingsFile = "settings.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _folder;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public PersistenceService()
    {
        // ApplicationData requires package identity; the app always runs packaged
        // via `winapp run`, so this is available.
        _folder = ApplicationData.Current.LocalFolder.Path;
    }

    private string PathFor(string fileName) => Path.Combine(_folder, fileName);

    // --- Subscriptions ---

    public List<Subscription> LoadSubscriptions() =>
        ReadList<Subscription>(SubscriptionsFile);

    public Task SaveSubscriptionsAsync(IEnumerable<Subscription> subscriptions) =>
        WriteAsync(SubscriptionsFile, new List<Subscription>(subscriptions));

    // --- Messages ---

    public List<NtfyMessage> LoadMessages() =>
        ReadList<NtfyMessage>(MessagesFile);

    public Task SaveMessagesAsync(IEnumerable<NtfyMessage> messages) =>
        WriteAsync(MessagesFile, new List<NtfyMessage>(messages));

    // --- Settings ---

    public AppSettings LoadSettings()
    {
        try
        {
            var path = PathFor(SettingsFile);
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, Options);
                if (settings is not null)
                {
                    return settings;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"LoadSettings failed: {ex.Message}");
        }
        return new AppSettings();
    }

    public Task SaveSettingsAsync(AppSettings settings) =>
        WriteAsync(SettingsFile, settings);

    // --- Helpers ---

    private List<T> ReadList<T>(string fileName)
    {
        try
        {
            var path = PathFor(fileName);
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var list = JsonSerializer.Deserialize<List<T>>(json, Options);
                if (list is not null)
                {
                    return list;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Read {fileName} failed: {ex.Message}");
        }
        return new List<T>();
    }

    private async Task WriteAsync<T>(string fileName, T value)
    {
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var json = JsonSerializer.Serialize(value, Options);
            var path = PathFor(fileName);
            var tmp = path + ".tmp";
            await File.WriteAllTextAsync(tmp, json).ConfigureAwait(false);
            // Atomic-ish replace so a crash mid-write can't truncate the real file.
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Write {fileName} failed: {ex.Message}");
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
