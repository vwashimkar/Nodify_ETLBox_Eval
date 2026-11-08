using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Xunit;

namespace Nodify_ETLBox.Tests;

public enum WpfTheme
{
    Light,
    Dark
}

/// <summary>Tests that load real WPF windows. One at a time, never next to other tests, one STA thread for all.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WpfCollection : ICollectionFixture<WpfHost>
{
    public const string Name = "WPF windows";
}

/// <summary>
/// Test only. One STA thread with a running dispatcher and one WPF <see cref="Application"/> (created
/// once per test run; a second one is never created). Loads the theme dictionaries the app loads at
/// start, lays out windows at a given size, renders them to PNG and collects binding messages.
/// Disposed at the end of the collection: the Application is shut down and the thread ends.
/// </summary>
public sealed class WpfHost : IDisposable
{
    public static readonly TimeSpan DefaultLimit = TimeSpan.FromSeconds(60);

    private readonly Thread _thread;
    private readonly Dispatcher _dispatcher;

    public WpfHost()
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        _thread = new Thread(() =>
        {
            try
            {
                if (Application.Current is not null)
                {
                    throw new InvalidOperationException("A WPF Application already exists in this test run; the WPF tests need to own it.");
                }

                _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                ready.SetResult(Dispatcher.CurrentDispatcher);
            }
            catch (Exception ex)
            {
                ready.SetException(ex);
                return;
            }

            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "WPF test host"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _dispatcher = ready.Task.GetAwaiter().GetResult();
    }

    /// <summary>Runs <paramref name="action"/> on the STA thread (with its synchronization context) and fails after the limit.</summary>
    public async Task RunAsync(Func<Task> action, TimeSpan? limit = null)
    {
        var task = _dispatcher.InvokeAsync(action).Task.Unwrap();
        var finished = await Task.WhenAny(task, Task.Delay(limit ?? DefaultLimit));
        if (finished != task)
        {
            throw new TimeoutException($"The WPF test did not finish within {(limit ?? DefaultLimit).TotalSeconds:0} s.");
        }

        await task;
    }

    public Task RunAsync(Action action, TimeSpan? limit = null)
        => RunAsync(() => { action(); return Task.CompletedTask; }, limit);

    /// <summary>Puts the app's theme dictionary (Themes/Light.xaml or Themes/Dark.xaml, as App.xaml and the theme box do) on the Application. Call on the STA thread.</summary>
    public static void SetTheme(WpfTheme theme)
    {
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        dictionaries.Clear();
        dictionaries.Add(new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Nodify_ETLBox;component/Themes/{theme}.xaml", UriKind.Absolute)
        });
    }

    /// <summary>
    /// Sets the size, then Measure, Arrange and UpdateLayout. A window that is not shown does not lay
    /// out at all (everything stays 0 x 0), so a window is first shown far off the screen, not
    /// activated and without a taskbar button. Its title bar and border are real, so the content is a
    /// little smaller than <paramref name="width"/> x <paramref name="height"/>, as on screen.
    /// </summary>
    public static void Layout(FrameworkElement element, double width, double height)
    {
        if (element is Window window && !window.IsVisible)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -20000;
            window.Top = -20000;
            window.ShowInTaskbar = false;
            window.ShowActivated = false;
            window.Show();
        }

        element.Width = width;
        element.Height = height;
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
    }

    /// <summary>Lets queued dispatcher work (bindings, layout) run. Call on the STA thread.</summary>
    public static async Task Settle()
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    /// <summary>Renders <paramref name="element"/> at its current layout size and <paramref name="dpi"/> to a PNG file.</summary>
    public static void RenderPng(FrameworkElement element, double dpi, string path)
    {
        // A window's own size includes the title bar and border, which are not drawn: render its client area.
        if (element is Window && VisualTreeHelper.GetChildrenCount(element) > 0 && VisualTreeHelper.GetChild(element, 0) is FrameworkElement client)
        {
            element = client;
        }

        var scale = dpi / 96.0;
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(element.ActualWidth * scale),
            (int)Math.Ceiling(element.ActualHeight * scale),
            dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(element);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>The number of distinct pixel colours in a PNG file (to see that it is not blank).</summary>
    public static int DistinctColours(string path)
    {
        using var stream = File.OpenRead(path);
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        var colours = new HashSet<int>();
        for (var i = 0; i < pixels.Length; i += 4)
        {
            colours.Add(BitConverter.ToInt32(pixels, i));
        }

        return colours.Count;
    }

    /// <summary>All elements of type <typeparamref name="T"/> below <paramref name="root"/> in the visual tree.</summary>
    public static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var below in Descendants<T>(child))
            {
                yield return below;
            }
        }
    }

    /// <summary>The visible TextBlock texts below <paramref name="root"/>, in tree order.</summary>
    public static List<string> VisibleTexts(DependencyObject root)
        => Descendants<System.Windows.Controls.TextBlock>(root)
            .Where(t => t.IsVisible || IsVisibleInLayout(t))
            .Select(t => t.Text)
            .Where(t => !string.IsNullOrEmpty(t))
            .ToList();

    /// <summary>True when the element and all its parents are Visible (works for a window that is not shown, where IsVisible is false).</summary>
    public static bool IsVisibleInLayout(DependencyObject element)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement ui && ui.Visibility != Visibility.Visible)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The folder where reports go (the repo's reports folder).</summary>
    public static string ReportsFolder()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "Nodify_ETLBox_Eval.slnx")))
        {
            folder = folder.Parent;
        }

        return Path.Combine(folder?.FullName ?? AppContext.BaseDirectory, "reports");
    }

    public void Dispose()
    {
        if (!_dispatcher.HasShutdownStarted)
        {
            _dispatcher.Invoke(() => Application.Current?.Shutdown());
            if (!_dispatcher.HasShutdownStarted)
            {
                _dispatcher.InvokeShutdown();
            }
        }

        _thread.Join(TimeSpan.FromSeconds(10));
    }
}

/// <summary>
/// Collects WPF binding messages (Warning and worse) while it is alive. Tracing is switched on with
/// <see cref="PresentationTraceSources.Refresh"/>, without it WPF only traces under a debugger.
/// </summary>
public sealed class BindingTrace : TraceListener
{
    private readonly ConcurrentQueue<string> _messages = new();
    private readonly SourceLevels _oldLevel;

    private BindingTrace()
    {
        PresentationTraceSources.Refresh();
        var source = PresentationTraceSources.DataBindingSource;
        _oldLevel = source.Switch.Level;
        source.Switch.Level = SourceLevels.Warning;
        source.Listeners.Add(this);
    }

    public static BindingTrace Start() => new();

    public IReadOnlyList<string> Messages => _messages.ToArray();

    public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? message)
        => _messages.Enqueue($"{eventType}: {message}");

    public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? format, params object?[]? args)
        => _messages.Enqueue($"{eventType}: {(args is null ? format : string.Format(format ?? string.Empty, args))}");

    public override void Write(string? message)
    {
    }

    public override void WriteLine(string? message)
    {
        if (!string.IsNullOrEmpty(message))
        {
            _messages.Enqueue(message);
        }
    }

    protected override void Dispose(bool disposing)
    {
        var source = PresentationTraceSources.DataBindingSource;
        source.Listeners.Remove(this);
        source.Switch.Level = _oldLevel;
        base.Dispose(disposing);
    }
}
