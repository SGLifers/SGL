using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using SGL.Core;
namespace SGL.Desktop;

public sealed class InstalledMapImage : Image
{
    public static readonly DependencyProperty MapProperty = DependencyProperty.Register(nameof(Map), typeof(SavedMap), typeof(InstalledMapImage), new PropertyMetadata(null, Changed));
    static readonly DependencyPropertyKey HasPreviewKey = DependencyProperty.RegisterReadOnly(nameof(HasPreview), typeof(bool), typeof(InstalledMapImage), new PropertyMetadata(false));
    public static readonly DependencyProperty HasPreviewProperty = HasPreviewKey.DependencyProperty;
    static readonly DependencyPropertyKey IsLoadingKey = DependencyProperty.RegisterReadOnly(nameof(IsLoading), typeof(bool), typeof(InstalledMapImage), new PropertyMetadata(false));
    public static readonly DependencyProperty IsLoadingProperty = IsLoadingKey.DependencyProperty;
    public SavedMap? Map { get => (SavedMap?)GetValue(MapProperty); set => SetValue(MapProperty, value); }
    public bool HasPreview => (bool)GetValue(HasPreviewProperty);
    public bool IsLoading => (bool)GetValue(IsLoadingProperty);
    long generation;
    public InstalledMapImage()
    {
        Loaded += async (_, _) => await LoadPreview();
        Unloaded += (_, _) => Clear();
    }
    void Clear()
    {
        generation++; Source = null; SetValue(HasPreviewKey, false); SetValue(IsLoadingKey, false);
    }
    static void Changed(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var image = (InstalledMapImage)sender;
        image.Clear();
        if (image.IsLoaded) _ = image.LoadPreview();
    }
    async Task LoadPreview()
    {
        long current = ++generation;
        var map = Map;
        if (map == null) return;
        SetValue(IsLoadingKey, true);
        var image = await InstalledImageCache.Get(map);
        if (current != generation || !ReferenceEquals(Map, map)) return;
        Source = image;
        SetValue(HasPreviewKey, image != null); SetValue(IsLoadingKey, false);
    }
}

static class InstalledImageCache
{
    static readonly object gate = new();
    static readonly Dictionary<string, BitmapSource> images = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, Task<BitmapSource?>> pending = new(StringComparer.OrdinalIgnoreCase);
    static readonly SemaphoreSlim readers = new(4);
    public static Task<BitmapSource?> Get(SavedMap map)
    {
        string key = map.ArchivePath + ":" + map.ArchiveHash;
        lock (gate)
        {
            if (images.TryGetValue(key, out var image)) return Task.FromResult<BitmapSource?>(image);
            if (pending.TryGetValue(key, out var task)) return task;
            var request = Read(key, map.ArchivePath);
            pending[key] = request;
            return request;
        }
    }
    static async Task<BitmapSource?> Read(string key, string path)
    {
        await Task.Yield();
        await readers.WaitAsync();
        try
        {
            var image = await Task.Run(() => Maps.InstalledPreview(path) is byte[] bytes ? SharedMapImage.Decode(bytes) : null);
            if (image != null) lock (gate)
            {
                if (images.Count >= 16) images.Remove(images.Keys.First());
                images[key] = image;
            }
            return image;
        }
        catch (Exception error) when (error is not OutOfMemoryException) { return null; }
        finally { readers.Release(); lock (gate) pending.Remove(key); }
    }
}
