using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace ClaudeUsageWidgetProvider;

/// <summary>
/// Free-floating, draggable, always-on-top card with one section per Claude/Codex account:
/// 5-hour and weekly limit bars plus tokens counted from the local CLI logs.
/// </summary>
public partial class FloatingWindow : Window
{
    private const string RunRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunRegistryValue = "ClaudeUsageWidget";

    private static readonly Brush MutedText = Frozen(new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0xA0)));
    private static readonly Brush MainText = Frozen(new SolidColorBrush(Color.FromRgb(0xEE, 0xEE, 0xF5)));
    private static readonly Brush ErrorText = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0x7A, 0x90)));
    private static readonly Brush TrackBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)));

    private readonly List<Section> _sections = [];
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private readonly DispatcherTimer _textTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private DateTimeOffset _lastTokenScan = DateTimeOffset.MinValue;
    private TokenTotals? _claudeTokens;
    private TokenTotals? _codexTokens;
    private bool _refreshing;

    internal FloatingWindow()
    {
        InitializeComponent();
        UpdatedText.Text = "cargando…";

        MouseLeftButtonDown += OnMouseLeftButtonDown;
        MouseRightButtonUp += (_, _) => ShowMenu();
        Loaded += (_, _) => RestorePosition();

        _refreshTimer.Tick += async (_, _) => await RefreshAsync();
        _textTimer.Tick += (_, _) => RefreshCountdowns();
        _textTimer.Start();
    }

    internal async Task SetAccountsAsync(List<ClaudeApiClient> clients)
    {
        ServicesPanel.Children.Clear();
        _sections.Clear();

        // Claude first, then Codex; tokens are shown on the first section of each service.
        var ordered = clients
            .Where(c => c.AccountService is ServiceType.Claude or ServiceType.Codex)
            .OrderBy(c => c.AccountService)
            .ToList();
        var seenServices = new HashSet<ServiceType>();
        foreach (var client in ordered)
        {
            var section = new Section(client, showTokens: seenServices.Add(client.AccountService));
            if (_sections.Count > 0)
                ServicesPanel.Children.Add(new Rectangle
                {
                    Height = 1, Margin = new Thickness(0, 10, 0, 10),
                    Fill = new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF))
                });
            ServicesPanel.Children.Add(section.Root);
            _sections.Add(section);
        }

        await RefreshAsync(force: true);
        _refreshTimer.Start();
    }

    private async Task RefreshAsync(bool force = false)
    {
        if (_refreshing) return;
        _refreshing = true;
        StatusDot.Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0xC8, 0x57));
        try
        {
            if (force || DateTimeOffset.Now - _lastTokenScan > TimeSpan.FromMinutes(2))
            {
                var (claude, codex) = await Task.Run(() =>
                {
                    TokenTotals? a = null, b = null;
                    try { a = LocalTokenCounter.CountClaude(); } catch { }
                    try { b = LocalTokenCounter.CountCodex(); } catch { }
                    return (a, b);
                });
                _claudeTokens = claude;
                _codexTokens = codex;
                _lastTokenScan = DateTimeOffset.Now;
            }

            bool anyError = false;
            foreach (var section in _sections)
            {
                var usage = await section.Client.GetUsageAsync(force);
                if (usage != null) section.Update(usage);
                else section.ShowError(section.Client.LastError);
                anyError |= usage == null;

                section.SetTokens(section.Client.AccountService == ServiceType.Claude ? _claudeTokens : _codexTokens);
                if (usage != null)
                {
                    UsageHistoryStore.Instance.Append(section.Client.AccountKey, usage);
                    NotificationService.Instance.CheckAndNotify(section.Client.AccountKey, usage);
                }
            }

            UpdatedText.Text = $"act. {DateTime.Now:HH:mm}";
            StatusDot.Fill = new SolidColorBrush(anyError ? Color.FromRgb(0xFF, 0x5E, 0x7E) : Color.FromRgb(0x3D, 0xDC, 0x97));
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void RefreshCountdowns()
    {
        foreach (var s in _sections) s.RefreshCountdowns();
    }

    // Moving the card

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            _ = RefreshAsync(force: true);
            return;
        }
        try { DragMove(); } catch (InvalidOperationException) { return; }
        SavePosition();
    }

    private void RestorePosition()
    {
        var s = SettingsStore.Instance;
        var area = SystemParameters.WorkArea;
        if (!double.IsNaN(s.FloatLeft) && !double.IsNaN(s.FloatTop) && IsOnSomeScreen(s.FloatLeft, s.FloatTop))
        {
            Left = s.FloatLeft;
            Top = s.FloatTop;
        }
        else
        {
            // Default: top-right corner of the primary work area.
            Left = area.Right - ActualWidth - 10;
            Top = area.Top + 10;
        }
    }

    private static bool IsOnSomeScreen(double left, double top) =>
        left >= SystemParameters.VirtualScreenLeft - 50 &&
        top >= SystemParameters.VirtualScreenTop - 50 &&
        left <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 60 &&
        top <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 60;

    private void SavePosition()
    {
        var s = SettingsStore.Instance;
        s.FloatLeft = Left;
        s.FloatTop = Top;
        s.Save();
    }

    // Context menu

    private void ShowMenu()
    {
        var menu = new ContextMenu();

        var refresh = new MenuItem { Header = "Actualizar ahora" };
        refresh.Click += (_, _) => _ = RefreshAsync(force: true);
        menu.Items.Add(refresh);

        var startup = new MenuItem { Header = "Iniciar con Windows", IsCheckable = true, IsChecked = IsStartupEnabled() };
        startup.Click += (_, _) => SetStartup(startup.IsChecked);
        menu.Items.Add(startup);

        var notify = new MenuItem
        {
            Header = "Avisarme al llegar al 90 %",
            IsCheckable = true,
            IsChecked = SettingsStore.Instance.NotificationsEnabled
        };
        notify.Click += (_, _) =>
        {
            SettingsStore.Instance.NotificationsEnabled = notify.IsChecked;
            SettingsStore.Instance.Save();
        };
        menu.Items.Add(notify);

        var reset = new MenuItem { Header = "Volver a la esquina" };
        reset.Click += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - ActualWidth - 10;
            Top = area.Top + 10;
            SavePosition();
        };
        menu.Items.Add(reset);

        menu.Items.Add(new Separator());

        var docked = new MenuItem { Header = "Usar modo barra de tareas (original)" };
        docked.Click += (_, _) =>
        {
            SettingsStore.Instance.FloatingMode = false;
            SettingsStore.Instance.Save();
            Restart();
        };
        menu.Items.Add(docked);

        var quit = new MenuItem { Header = "Salir" };
        quit.Click += (_, _) => Application.Current.Shutdown();
        menu.Items.Add(quit);

        menu.PlacementTarget = this;
        menu.IsOpen = true;
    }

    internal static void Restart()
    {
        if (Environment.ProcessPath is { } exe)
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        Application.Current.Shutdown();
    }

    private static bool IsStartupEnabled()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunRegistryKey);
        return key?.GetValue(RunRegistryValue) != null;
    }

    private static void SetStartup(bool enabled)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunRegistryKey, writable: true);
        if (key == null) return;
        if (enabled && Environment.ProcessPath is { } exe)
            key.SetValue(RunRegistryValue, $"\"{exe}\"");
        else
            key.DeleteValue(RunRegistryValue, throwOnMissingValue: false);
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    /// <summary>One account: header with logo, name and token totals, then one bar per limit window.</summary>
    private sealed class Section
    {
        public ClaudeApiClient Client { get; }
        public StackPanel Root { get; } = new();

        private readonly bool _showTokens;
        private readonly TextBlock _tokensText;
        private readonly TextBlock _errorText;
        private readonly StackPanel _barsPanel = new();
        private readonly Dictionary<string, Bar> _bars = [];
        private readonly (Color Light, Color Dark) _accent;

        public Section(ClaudeApiClient client, bool showTokens)
        {
            Client = client;
            _showTokens = showTokens;
            bool isClaude = client.AccountService == ServiceType.Claude;
            _accent = isClaude
                ? (Color.FromRgb(0xF4, 0xA2, 0x7E), Color.FromRgb(0xD9, 0x77, 0x57))
                : (Color.FromRgb(0x4F, 0xE3, 0xA8), Color.FromRgb(0x10, 0xA3, 0x7F));

            var header = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var icon = new Image
            {
                Width = 18, Height = 18, Margin = new Thickness(0, 0, 7, 0),
                Source = new BitmapImage(new Uri(isClaude
                    ? "pack://application:,,,/Assets/claude-logo.png"
                    : "pack://application:,,,/Assets/codex-logo.png"))
            };
            RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
            header.Children.Add(icon);

            var name = new TextBlock
            {
                Text = isClaude ? "Claude" : "Codex",
                FontSize = 13.5, FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(_accent.Light),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(name, 1);
            header.Children.Add(name);

            _tokensText = new TextBlock
            {
                FontSize = 10, Foreground = MutedText,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Tokens contados en los registros locales de " +
                          (isClaude ? "Claude Code" : "Codex CLI") +
                          ". El uso en la web no aparece aquí, pero sí en las barras."
            };
            Grid.SetColumn(_tokensText, 2);
            header.Children.Add(_tokensText);

            _errorText = new TextBlock
            {
                FontSize = 10, Foreground = ErrorText, TextWrapping = TextWrapping.Wrap,
                Visibility = Visibility.Collapsed
            };

            Root.Children.Add(header);
            Root.Children.Add(_barsPanel);
            Root.Children.Add(_errorText);

            // Placeholders until the first response arrives.
            GetBar("x-5h");
            GetBar("x-7d");
        }

        public void SetTokens(TokenTotals? totals)
        {
            if (!_showTokens || totals == null)
            {
                _tokensText.Text = "";
                return;
            }
            _tokensText.Text = $"hoy {LocalTokenCounter.Format(totals.Today)} · 7d {LocalTokenCounter.Format(totals.Last7Days)}";
        }

        public void Update(UsageData data)
        {
            _errorText.Visibility = Visibility.Collapsed;
            var limits = data.Limits
                .Where(l => l.Label.StartsWith("unified-") || l.Label.StartsWith("session-"))
                .Where(l => !SettingsStore.Instance.IsLimitHidden(l.Label))
                .ToList();
            if (limits.Count == 0) limits = data.Limits.Take(2).ToList();

            foreach (var key in _bars.Keys.Except(limits.Select(l => l.Label)).ToList())
            {
                _barsPanel.Children.Remove(_bars[key].Root);
                _bars.Remove(key);
            }
            foreach (var limit in limits)
                GetBar(limit.Label).Set(limit);
        }

        public void ShowError(string? error)
        {
            _errorText.Text = "Sin datos: " + (error ?? "error desconocido");
            _errorText.ToolTip = error;
            _errorText.Visibility = Visibility.Visible;
        }

        public void RefreshCountdowns()
        {
            foreach (var bar in _bars.Values) bar.RefreshCountdown();
        }

        private Bar GetBar(string label)
        {
            if (_bars.TryGetValue(label, out var bar)) return bar;
            // A real limit replaces the placeholders.
            if (!label.StartsWith("x-"))
                foreach (var key in _bars.Keys.Where(k => k.StartsWith("x-")).ToList())
                {
                    _barsPanel.Children.Remove(_bars[key].Root);
                    _bars.Remove(key);
                }
            bar = new Bar(label, _accent);
            _bars[label] = bar;
            _barsPanel.Children.Add(bar.Root);
            return bar;
        }
    }

    private sealed class Bar
    {
        public Grid Root { get; } = new() { Margin = new Thickness(0, 2, 0, 6) };

        private readonly TextBlock _value;
        private readonly Grid _track;
        private readonly Border _fill;
        private readonly DropShadowEffect _glow;
        private readonly (Color Light, Color Dark) _accent;
        private RateLimitEntry? _limit;
        private double _fraction;

        public Bar(string label, (Color Light, Color Dark) accent)
        {
            _accent = accent;
            Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var title = new TextBlock { Text = DisplayName(label), FontSize = 10.5, Foreground = MutedText };
            _value = new TextBlock
            {
                Text = "…", FontSize = 10.5, Foreground = MainText,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            Root.Children.Add(title);
            Root.Children.Add(_value);

            _track = new Grid { Height = 8, Margin = new Thickness(0, 3, 0, 0) };
            _track.Children.Add(new Border { CornerRadius = new CornerRadius(4), Background = TrackBrush });
            _glow = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 0, Opacity = 0.7, Color = accent.Dark };
            _fill = new Border
            {
                CornerRadius = new CornerRadius(4),
                HorizontalAlignment = HorizontalAlignment.Left,
                Width = 0,
                Effect = _glow
            };
            _track.Children.Add(_fill);
            _track.SizeChanged += (_, _) => Animate(false);
            Grid.SetRow(_track, 1);
            Root.Children.Add(_track);
            Paint(0);
        }

        public void Set(RateLimitEntry limit)
        {
            _limit = limit;
            _fraction = Math.Clamp(limit.Utilization / 100.0, 0, 1);
            Paint(limit.Utilization);
            RefreshCountdown();
            Animate(true);
        }

        public void RefreshCountdown()
        {
            if (_limit == null) return;
            var remaining = _limit.ResetsAt - DateTimeOffset.UtcNow;
            string reset = remaining <= TimeSpan.Zero ? "reinicio ya"
                : remaining.TotalDays >= 1 ? $"{(int)remaining.TotalDays}d {remaining.Hours}h"
                : remaining.TotalHours >= 1 ? $"{(int)remaining.TotalHours}h {remaining.Minutes:00}m"
                : $"{remaining.Minutes}m";
            _value.Text = $"{_limit.Utilization:0} %  ·  ↻ {reset}";
            Root.ToolTip = $"Usado {_limit.Utilization:0.#} %. Se reinicia el {_limit.ResetsAt.ToLocalTime():ddd d MMM, HH:mm}.";
        }

        private void Animate(bool smooth)
        {
            double target = Math.Max(_fraction > 0 ? 8 : 0, _track.ActualWidth * _fraction);
            if (!smooth)
            {
                _fill.BeginAnimation(FrameworkElement.WidthProperty, null);
                _fill.Width = target;
                return;
            }
            var anim = new DoubleAnimation(target, TimeSpan.FromMilliseconds(700))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            _fill.BeginAnimation(FrameworkElement.WidthProperty, anim);
        }

        private void Paint(double utilization)
        {
            var (light, dark) = utilization >= 90
                ? (Color.FromRgb(0xFF, 0x7A, 0x90), Color.FromRgb(0xE0, 0x24, 0x5E))
                : utilization >= 75
                    ? (Color.FromRgb(0xFF, 0xD1, 0x66), Color.FromRgb(0xFF, 0x8C, 0x42))
                    : _accent;
            _fill.Background = new LinearGradientBrush(dark, light, 0);
            _glow.Color = dark;
        }

        private static string DisplayName(string label)
        {
            if (label.StartsWith("x-")) label = label[2..];
            var window = label.Contains('-') ? label[(label.LastIndexOf('-') + 1)..] : label;
            return window switch
            {
                "5h" => "Sesión (5 h)",
                "7d" => "Semana",
                "30d" or "31d" => "Mes",
                _ => window
            };
        }
    }
}
