using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Microsoft.Web.WebView2.Core;

namespace SETBoard;

/// <summary>在应用内打开腾讯问卷，答卷数据直接提交至腾讯问卷。</summary>
public sealed class FeedbackWindow : Window
{
    public const string SurveyUrl = "https://wj.qq.com/s2/28775549/sjy1";

    private readonly SurveyWebView _browser;
    private readonly Border _statusOverlay;
    private readonly TextBlock _statusText;

    public FeedbackWindow()
    {
        Title = "使用问题反馈";
        Width = 900;
        Height = 760;
        MinWidth = 560;
        MinHeight = 500;
        CanResize = true;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#0C1B24"));
        FontFamily = new FontFamily("Microsoft YaHei UI, Microsoft YaHei, Segoe UI");

        _browser = new SurveyWebView(SurveyUrl, OnBrowserReady, OnBrowserError);
        _statusText = new TextBlock
        {
            Text = "正在加载问卷…",
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.Parse("#D5DFE6")),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };
        _statusOverlay = new Border
        {
            Background = Background,
            Padding = new Thickness(32),
            Child = _statusText,
        };

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        var externalButton = CreateExternalButton();
        Grid.SetColumn(externalButton, 1);
        var headerGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Children =
            {
                new TextBlock
                {
                    Text = "SETBoard 使用问题反馈",
                    FontSize = 16,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = new SolidColorBrush(Color.Parse("#F2F6F9")),
                    VerticalAlignment = VerticalAlignment.Center,
                },
                externalButton,
            },
        };
        var header = new Border
        {
            Padding = new Thickness(20, 12),
            BorderBrush = new SolidColorBrush(Color.Parse("#2C3A45")),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = headerGrid,
        };
        root.Children.Add(header);

        var browserHost = new Grid();
        browserHost.Children.Add(_browser);
        browserHost.Children.Add(_statusOverlay);
        Grid.SetRow(browserHost, 1);
        root.Children.Add(browserHost);
        Content = root;
    }

    private Button CreateExternalButton()
    {
        var button = new Button
        {
            Content = "在浏览器中打开",
            Height = 34,
            Padding = new Thickness(14, 0),
            CornerRadius = new CornerRadius(17),
            Background = new SolidColorBrush(Color.Parse("#22333E")),
            Foreground = new SolidColorBrush(Color.Parse("#E8EFF4")),
            BorderBrush = new SolidColorBrush(Color.Parse("#3A4A55")),
            BorderThickness = new Thickness(1),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        button.Click += (_, _) => OpenExternal();
        return button;
    }

    private void OnBrowserReady() => _statusOverlay.IsVisible = false;

    private void OnBrowserError(Exception exception)
    {
        _browser.IsVisible = false;
        _statusOverlay.IsVisible = true;
        _statusOverlay.Child = new StackPanel
        {
            Spacing = 16,
            MaxWidth = 440,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Text = "问卷暂时无法在应用内打开",
                    FontSize = 17,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = new SolidColorBrush(Color.Parse("#F2F6F9")),
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
                new TextBlock
                {
                    Text = $"请检查网络，并确认系统已安装 Microsoft Edge WebView2 运行时。\n\n{exception.Message}",
                    FontSize = 13,
                    Foreground = new SolidColorBrush(Color.Parse("#B9C8D2")),
                    TextWrapping = TextWrapping.Wrap,
                },
                new Button
                {
                    Content = "改用系统浏览器答卷",
                    Height = 40,
                    Padding = new Thickness(20, 0),
                    CornerRadius = new CornerRadius(20),
                    Background = new SolidColorBrush(Color.Parse("#D6EF72")),
                    Foreground = new SolidColorBrush(Color.Parse("#141A10")),
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
            },
        };
        if (_statusOverlay.Child is StackPanel panel && panel.Children[^1] is Button fallback)
            fallback.Click += (_, _) => OpenExternal();
    }

    private static void OpenExternal() => Process.Start(new ProcessStartInfo
    {
        FileName = SurveyUrl,
        UseShellExecute = true,
    });

    /// <summary>Avalonia 与 Win32 WebView2 之间的轻量宿主。</summary>
    private sealed class SurveyWebView : NativeControlHost
    {
        private const int WsChild = 0x40000000;
        private const int WsVisible = 0x10000000;
        private const int WsClipChildren = 0x02000000;

        private readonly string _url;
        private readonly Action _onReady;
        private readonly Action<Exception> _onError;
        private CoreWebView2Controller? _controller;
        private IntPtr _hwnd;
        private IntPtr _parentHwnd;
        private bool _initializing;

        public SurveyWebView(string url, Action onReady, Action<Exception> onError)
        {
            _url = url;
            _onReady = onReady;
            _onError = onError;
            LayoutUpdated += (_, _) => UpdateBounds();
        }

        protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
        {
            _parentHwnd = parent.Handle;
            _hwnd = CreateWindowEx(0, "Static", string.Empty, WsChild | WsVisible | WsClipChildren,
                0, 0, 100, 100, _parentHwnd, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero)
                throw new InvalidOperationException("无法创建问卷浏览器窗口。");

            _ = InitializeAsync();
            return new PlatformHandle(_hwnd, "HWND");
        }

        protected override void DestroyNativeControlCore(IPlatformHandle control)
        {
            _controller?.Close();
            _controller = null;
            if (_hwnd != IntPtr.Zero)
            {
                DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
            }
        }

        private async Task InitializeAsync()
        {
            if (_initializing) return;
            _initializing = true;
            try
            {
                var userDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SETBoard", "WebView2");
                Directory.CreateDirectory(userDataFolder);
                var environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
                _controller = await environment.CreateCoreWebView2ControllerAsync(_hwnd);
                _controller.IsVisible = true;
                UpdateBounds();
                _controller.CoreWebView2.NavigationCompleted += (_, args) =>
                {
                    if (args.IsSuccess) _onReady();
                    else _onError(new InvalidOperationException($"问卷页面加载失败（{args.WebErrorStatus}）。"));
                };
                _controller.CoreWebView2.Navigate(_url);
            }
            catch (Exception ex)
            {
                _onError(ex);
            }
        }

        private void UpdateBounds()
        {
            if (_hwnd == IntPtr.Zero || _parentHwnd == IntPtr.Zero) return;
            if (!GetClientRect(_parentHwnd, out var parentRect)) return;
            MoveWindow(_hwnd, 0, 0, parentRect.Right, parentRect.Bottom, true);
            if (_controller is not null && GetClientRect(_hwnd, out var rect))
                _controller.Bounds = rect;
        }

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName,
            int style, int x, int y, int width, int height, IntPtr parent, IntPtr menu,
            IntPtr instance, IntPtr param);

        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool MoveWindow(IntPtr hwnd, int x, int y, int width, int height, bool repaint);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hwnd, out System.Drawing.Rectangle rect);
    }
}
