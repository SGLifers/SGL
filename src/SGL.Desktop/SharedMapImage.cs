using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using SGL.Core;

namespace SGL.Desktop;

public sealed class SharedMapImage : Image
{
    public static readonly DependencyProperty PreviewDescriptorProperty = DependencyProperty.Register(nameof(PreviewDescriptor), typeof(JsonObject), typeof(SharedMapImage), new PropertyMetadata(null, Changed));
    static readonly DependencyPropertyKey HasPreviewPropertyKey = DependencyProperty.RegisterReadOnly(nameof(HasPreview), typeof(bool), typeof(SharedMapImage), new PropertyMetadata(false));
    public static readonly DependencyProperty HasPreviewProperty = HasPreviewPropertyKey.DependencyProperty;
    static readonly DependencyPropertyKey IsLoadingPropertyKey = DependencyProperty.RegisterReadOnly(nameof(IsLoading), typeof(bool), typeof(SharedMapImage), new PropertyMetadata(false));
    public static readonly DependencyProperty IsLoadingProperty = IsLoadingPropertyKey.DependencyProperty;
    public JsonObject? PreviewDescriptor { get => (JsonObject?)GetValue(PreviewDescriptorProperty); set => SetValue(PreviewDescriptorProperty, value); }
    public bool HasPreview => (bool)GetValue(HasPreviewProperty);
    public bool IsLoading => (bool)GetValue(IsLoadingProperty);
    long generation;
    public SharedMapImage()
    {
        Loaded += async (_, _) => await LoadImage();
        Unloaded += (_, _) => { generation++; Source = null; SetValue(HasPreviewPropertyKey, false); SetValue(IsLoadingPropertyKey, false); };
    }
    static void Changed(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var image = (SharedMapImage)sender;
        image.generation++; image.Source = null; image.SetValue(HasPreviewPropertyKey, false); image.SetValue(IsLoadingPropertyKey, false);
        if (image.IsLoaded) _ = image.LoadImage();
    }
    async Task LoadImage()
    {
        long current = ++generation;
        var descriptor = PreviewDescriptor;
        if (descriptor == null) return;
        SetValue(IsLoadingPropertyKey, true);
        var bitmap = await SharedImageCache.Get(descriptor);
        if (current != generation || !ReferenceEquals(descriptor, PreviewDescriptor)) return;
        Source = bitmap;
        SetValue(IsLoadingPropertyKey, false);
        SetValue(HasPreviewPropertyKey, bitmap != null);
    }
    public static BitmapSource Decode(byte[] bytes)
    {
        using var metadata = new MemoryStream(bytes, false);
        var frame = BitmapDecoder.Create(metadata, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
        int width = frame.PixelWidth, height = frame.PixelHeight;
        if (width < 1 || height < 1 || width > 16384 || height > 16384 || (long)width * height > 40_000_000)
            throw new InvalidDataException("Shared preview dimensions exceed their limits.");
        double scale = Math.Min(1, 1000d / Math.Max(width, height));
        using var stream = new MemoryStream(bytes, false);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = Math.Max(1, (int)(width * scale));
        image.DecodePixelHeight = Math.Max(1, (int)(height * scale));
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }
}

static class SharedImageCache
{
    static readonly object gate = new();
    static readonly Dictionary<string, BitmapSource> images = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, Task<BitmapSource?>> pending = new(StringComparer.OrdinalIgnoreCase);
    static readonly SemaphoreSlim downloads = new(4);
    public static Task<BitmapSource?> Get(JsonObject descriptor)
    {
        if (descriptor["sha256"] is not JsonValue value || !value.TryGetValue<string>(out var key) || key.Length != 64 || !key.All(Uri.IsHexDigit))
            return Task.FromResult<BitmapSource?>(null);
        lock (gate)
        {
            if (images.TryGetValue(key, out var image)) return Task.FromResult<BitmapSource?>(image);
            if (pending.TryGetValue(key, out var request)) return request;
            var task = Fetch(key, Configuration.Clone(descriptor));
            pending[key] = task;
            return task;
        }
    }
    static async Task<BitmapSource?> Fetch(string key, JsonObject descriptor)
    {
        // Yield before touching the dictionaries so Get can register this task.
        await Task.Yield();
        await downloads.WaitAsync();
        try
        {
            var bytes = await LibraryClient.FetchPreview(descriptor);
            var image = bytes == null ? null : SharedMapImage.Decode(bytes);
            if (image != null) lock (gate)
            {
                if (images.Count >= 24) images.Remove(images.Keys.First());
                images[key] = image;
            }
            return image;
        }
        catch (Exception error) when (error is not OutOfMemoryException) { return null; }
        finally { downloads.Release(); lock (gate) pending.Remove(key); }
    }
}
