using Shuka.Android.Services;

namespace Shuka.Android.Pages;

/// <summary>
/// A self-contained card view for a single DownloadItem.
/// Subscribes to PropertyChanged and updates itself live.
/// Includes a tap-to-expand log panel for debugging.
/// </summary>
public class DownloadCard : ContentView
{
    public event Action<DownloadItem>? OptionsRequested;
    public event Action<DownloadItem>? ShareRequested;
    public event Action<DownloadItem>? OpenRequested;
    public event Action<DownloadItem>? RetryRequested;
    public event Action<DownloadItem>? DismissRequested;

    private readonly DownloadItem _item;

    // Live-updated controls
    private readonly Label  _titleLabel;
    private readonly Label  _authorLabel;
    private readonly Label  _statusTextLabel;
    private readonly Border _statusDot;
    private readonly Label  _statusIconLabel;
    private readonly Border _progressFill;
    private readonly Label  _pctLabel;
    private readonly Border _optionsBtn;
    private readonly View   _progressSection;
    private readonly View   _actionRow;
    private readonly View   _retryRow;
    private readonly View   _logSection;
    private readonly Label  _logLabel;
    private readonly Label  _logToggleIcon;
    private bool            _logExpanded = false;

    // Track card width for progress fill calculation
    private double _cardWidth = 0;

    public DownloadCard(DownloadItem item)
    {
        _item = item;

        _statusIconLabel = new Label
        {
            FontFamily        = "MaterialSymbols",
            FontSize          = 18,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions   = LayoutOptions.Center
        };

        _statusDot = new Border
        {
            StrokeThickness = 0,
            StrokeShape     = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 10 },
            WidthRequest    = 36,
            HeightRequest   = 36,
            VerticalOptions = LayoutOptions.Start,
            Margin          = new Thickness(0, 0, 12, 0),
            Content         = _statusIconLabel
        };

        _titleLabel = new Label
        {
            FontSize       = 14,
            FontAttributes = FontAttributes.Bold,
            LineBreakMode  = LineBreakMode.TailTruncation,
            MaxLines       = 1
        };
        _titleLabel.SetDynamicResource(Label.TextColorProperty, "TextPrimary");

        _authorLabel = new Label
        {
            FontSize      = 12,
            LineBreakMode = LineBreakMode.TailTruncation,
            MaxLines      = 1
        };
        _authorLabel.SetDynamicResource(Label.TextColorProperty, "TextMuted");

        _statusTextLabel = new Label
        {
            FontSize      = 11,
            LineBreakMode = LineBreakMode.TailTruncation,
            MaxLines      = 1
        };
        _statusTextLabel.SetDynamicResource(Label.TextColorProperty, "TextMuted");

        var textStack = new VerticalStackLayout
        {
            Spacing         = 3,
            VerticalOptions = LayoutOptions.Center,
            Children        = { _titleLabel, _authorLabel, _statusTextLabel }
        };

        // ── Log toggle button ─────────────────────────────────────────────────
        _logToggleIcon = new Label
        {
            Text            = "\uE313", // expand_more
            FontFamily      = "MaterialSymbols",
            FontSize        = 20,
            VerticalOptions = LayoutOptions.Center,
            Margin          = new Thickness(4, 0, 0, 0),
        };
        _logToggleIcon.SetDynamicResource(Label.TextColorProperty, "TextMuted");

        var logToggleBtn = new Border
        {
            StrokeThickness = 0,
            BackgroundColor = Colors.Transparent,
            Padding         = new Thickness(6, 4),
            VerticalOptions = LayoutOptions.Center,
            Content         = _logToggleIcon,
        };
        logToggleBtn.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(ToggleLog)
        });

        // ── Options button ─────────────────────────────────────────────────────
        var optionsLabel = new Label
        {
            Text            = "\uE5D4", // more_vert
            FontFamily      = "MaterialSymbols",
            FontSize        = 22,
            VerticalOptions = LayoutOptions.Center
        };
        optionsLabel.SetDynamicResource(Label.TextColorProperty, "TextSecondary");

        _optionsBtn = new Border
        {
            StrokeThickness = 0,
            BackgroundColor = Colors.Transparent,
            Padding         = new Thickness(6, 6),
            VerticalOptions = LayoutOptions.Center,
            Content         = optionsLabel
        };
        _optionsBtn.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(() => OptionsRequested?.Invoke(_item))
        });

        // ── Header row ────────────────────────────────────────────────────────
        var headerGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new ColumnDefinition { Width = GridLength.Auto },  // status dot
                new ColumnDefinition { Width = GridLength.Star },  // text
                new ColumnDefinition { Width = GridLength.Auto },  // log toggle
                new ColumnDefinition { Width = GridLength.Auto },  // cancel
            },
            Padding = new Thickness(18, 16, 18, 14)
        };
        headerGrid.Add(_statusDot,   0, 0);
        headerGrid.Add(textStack,    1, 0);
        headerGrid.Add(logToggleBtn, 2, 0);
        headerGrid.Add(_optionsBtn,  3, 0);

        // ── Progress bar ──────────────────────────────────────────────────────
        var progressTrack = new Border
        {
            StrokeThickness   = 0,
            StrokeShape       = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 3 },
            HeightRequest     = 6,
            HorizontalOptions = LayoutOptions.Fill
        };
        progressTrack.SetDynamicResource(Border.BackgroundColorProperty, "ProgressTrack");

        _progressFill = new Border
        {
            StrokeThickness   = 0,
            StrokeShape       = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 3 },
            HeightRequest     = 6,
            HorizontalOptions = LayoutOptions.Start,
            WidthRequest      = 0
        };
        _progressFill.SetDynamicResource(Border.BackgroundColorProperty, "AccentLight");

        var trackContainer = new Grid
        {
            HeightRequest   = 6,
            VerticalOptions = LayoutOptions.Center,
            Children        = { progressTrack, _progressFill }
        };

        _pctLabel = new Label
        {
            FontSize        = 12,
            FontAttributes  = FontAttributes.Bold,
            VerticalOptions = LayoutOptions.Center,
            Margin          = new Thickness(10, 0, 0, 0)
        };
        _pctLabel.SetDynamicResource(Label.TextColorProperty, "AccentLight");

        var progressRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new ColumnDefinition { Width = GridLength.Star },
                new ColumnDefinition { Width = GridLength.Auto }
            },
            ColumnSpacing   = 0,
            VerticalOptions = LayoutOptions.Center
        };
        progressRow.Add(trackContainer, 0, 0);
        progressRow.Add(_pctLabel,      1, 0);

        _progressSection = new VerticalStackLayout
        {
            Padding  = new Thickness(18, 0, 18, 16),
            Spacing  = 8,
            Children = { progressRow }
        };

        // ── Action row (done) ─────────────────────────────────────────────────
        var shareBtn = MakeActionBtn("\uE6B8", "Share", "Accent",   false, () => ShareRequested?.Invoke(_item));
        var openBtn  = MakeActionBtn("\uE2C7", "Open",  "BgInput",  true,  () => OpenRequested?.Invoke(_item));

        var actionGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new ColumnDefinition { Width = GridLength.Star },
                new ColumnDefinition { Width = new GridLength(12) },
                new ColumnDefinition { Width = GridLength.Star }
            },
            Padding   = new Thickness(18, 0, 18, 16),
            IsVisible = false
        };
        actionGrid.Add(shareBtn, 0, 0);
        actionGrid.Add(openBtn,  2, 0);
        _actionRow = actionGrid;

        // ── Retry row (failed/cancelled) ──────────────────────────────────────
        var retryBtn   = MakeActionBtn("\uE5D5", "Retry",   "Accent",  false, () => RetryRequested?.Invoke(_item));
        var dismissBtn = MakeActionBtn("\uE5CD", "Dismiss", "BgInput", true,  () => DismissRequested?.Invoke(_item));

        var retryGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new ColumnDefinition { Width = GridLength.Star },
                new ColumnDefinition { Width = new GridLength(12) },
                new ColumnDefinition { Width = GridLength.Star }
            },
            Padding   = new Thickness(18, 0, 18, 16),
            IsVisible = false
        };
        retryGrid.Add(retryBtn,   0, 0);
        retryGrid.Add(dismissBtn, 2, 0);
        _retryRow = retryGrid;

        // ── Log section ───────────────────────────────────────────────────────
        var logDivider = new BoxView
        {
            HeightRequest     = 1,
            HorizontalOptions = LayoutOptions.Fill,
            Margin            = new Thickness(18, 0)
        };
        logDivider.SetDynamicResource(BoxView.ColorProperty, "Divider");

        _logLabel = new Label
        {
            FontSize      = 10,
            FontFamily    = "Monospace",
            LineBreakMode = LineBreakMode.WordWrap,
            Margin        = new Thickness(18, 10, 18, 14),
        };
        _logLabel.SetDynamicResource(Label.TextColorProperty, "TextMuted");

        _logSection = new VerticalStackLayout
        {
            IsVisible = false,
            Children  = { logDivider, _logLabel }
        };

        // ── Card assembly ─────────────────────────────────────────────────────
        var cardInner = new VerticalStackLayout
        {
            Children = { headerGrid, _progressSection, _actionRow, _retryRow, _logSection }
        };

        var card = new Border
        {
            StrokeThickness = 1,
            StrokeShape     = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 20 },
            Padding         = new Thickness(0),
            Content         = cardInner
        };
        card.SetDynamicResource(Border.BackgroundColorProperty, "BgCard");
        card.SetDynamicResource(Border.StrokeProperty, "Stroke");

        card.SizeChanged += (s, e) =>
        {
            _cardWidth = card.Width - 36;
            UpdateProgressFill();
        };

        Content = card;

        _item.PropertyChanged += OnItemPropertyChanged;
        Refresh();
    }

    // ── Log toggle ────────────────────────────────────────────────────────────

    private async void ToggleLog()
    {
        _logExpanded = !_logExpanded;

        // Rotate the chevron icon
        await _logToggleIcon.RotateToAsync(_logExpanded ? 180 : 0, 200, Easing.CubicOut);

        if (_logExpanded)
        {
            // Update log text before showing
            _logLabel.Text    = string.IsNullOrWhiteSpace(_item.LogText)
                ? "(no log yet)"
                : _item.LogText.TrimEnd();
            _logSection.Opacity      = 0;
            _logSection.IsVisible    = true;
            await _logSection.FadeToAsync(1.0, 180, Easing.CubicOut);
        }
        else
        {
            await _logSection.FadeToAsync(0, 150, Easing.CubicIn);
            _logSection.IsVisible = false;
        }
    }

    // ── Property change ───────────────────────────────────────────────────────

    private void OnItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            Refresh();
            // Keep log text live while expanded
            if (_logExpanded && e.PropertyName == nameof(DownloadItem.LogText))
                _logLabel.Text = string.IsNullOrWhiteSpace(_item.LogText)
                    ? "(no log yet)"
                    : _item.LogText.TrimEnd();
        });
    }

    private void Refresh()
    {
        _titleLabel.Text       = _item.Title;
        _authorLabel.Text      = _item.Author;
        _authorLabel.IsVisible = !string.IsNullOrEmpty(_item.Author);
        _statusTextLabel.Text  = _item.StatusText;
        _pctLabel.Text         = _item.ProgressPct;

        _statusDot.BackgroundColor = _item.StatusColor.WithAlpha(0.15f);
        _statusIconLabel.Text      = _item.StatusIcon;
        _statusIconLabel.TextColor = _item.StatusColor;

        UpdateProgressFill();

        bool running = _item.IsRunning;
        bool done    = _item.IsDone;

        _optionsBtn.IsVisible      = running;
        _progressSection.IsVisible = running;
        _actionRow.IsVisible       = done;
        _retryRow.IsVisible        = _item.IsFailed || _item.IsCancelled;

        if (done)
            ((Border)Content).SetDynamicResource(Border.StrokeProperty, "Success");
        else if (_item.IsFailed)
            ((Border)Content).SetDynamicResource(Border.StrokeProperty, "Danger");
        else if (_item.IsCancelled)
            ((Border)Content).SetDynamicResource(Border.StrokeProperty, "Warning");
        else
            ((Border)Content).SetDynamicResource(Border.StrokeProperty, "Stroke");
    }

    private void UpdateProgressFill()
    {
        if (_cardWidth <= 0) return;
        _progressFill.WidthRequest = Math.Max(0, _cardWidth * _item.Progress);
    }

    // ── Helper ────────────────────────────────────────────────────────────────

    private static Border MakeActionBtn(string icon, string text, string bgKey,
        bool outlined, Action onTap)
    {
        var iconLbl = new Label
        {
            Text            = icon,
            FontFamily      = "MaterialSymbols",
            FontSize        = 18,
            VerticalOptions = LayoutOptions.Center,
            Margin          = new Thickness(0, 0, 8, 0)
        };
        var textLbl = new Label
        {
            Text            = text,
            FontSize        = 13,
            FontAttributes  = FontAttributes.Bold,
            VerticalOptions = LayoutOptions.Center
        };

        string colorKey = outlined ? "TextSecondary" : "TextOnAccent";
        iconLbl.SetDynamicResource(Label.TextColorProperty, colorKey);
        textLbl.SetDynamicResource(Label.TextColorProperty, colorKey);

        var inner = new Grid { Padding = new Thickness(14, 0) };
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Star });
        inner.Add(iconLbl, 0, 0);
        inner.Add(textLbl, 1, 0);

        var btn = new Border
        {
            StrokeThickness = outlined ? 1 : 0,
            StrokeShape     = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 14 },
            HeightRequest   = 46,
            Padding         = new Thickness(0),
            Content         = inner
        };
        btn.SetDynamicResource(Border.BackgroundColorProperty, bgKey);
        if (outlined) btn.SetDynamicResource(Border.StrokeProperty, "Stroke");

        btn.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(onTap)
        });
        return btn;
    }
}
