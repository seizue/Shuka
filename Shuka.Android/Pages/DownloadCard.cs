using Shuka.Android.Services;

namespace Shuka.Android.Pages;

/// <summary>
/// A self-contained card view for a single DownloadItem.
/// Subscribes to PropertyChanged and updates itself live.
/// Includes a tap-to-expand log panel for debugging.
/// </summary>
public class DownloadCard : ContentView
{
    private DownloadItem? _item;

    public DownloadCard() : this(null!)
    {
    }

    private DownloadsPage? FindParentPage()
    {
        Element? parent = Parent;
        while (parent != null)
        {
            if (parent is DownloadsPage page)
                return page;
            parent = parent.Parent;
        }
        return null;
    }

    // Live-updated controls
    private readonly Label  _titleLabel;
    private readonly Label  _authorLabel;
    private readonly Label  _statusTextLabel;
    private readonly Border _statusDot;
    private readonly Label  _statusIconLabel;
    private readonly BoxView _progressFill;
    private readonly Label  _pctLabel;
    private readonly View   _progressSection;
    private readonly View   _logSection;
    private readonly Label  _logLabel;
    private readonly Label  _logToggleIcon;
    private readonly Grid   _trackContainer;
    private bool            _logExpanded = false;
    private string? _lastStrokeKey;

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
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center,
        };
        _logToggleIcon.SetDynamicResource(Label.TextColorProperty, "TextMuted");

        var logToggleBtn = new Border
        {
            StrokeThickness = 0,
            BackgroundColor = Colors.Transparent,
            Padding         = new Thickness(6, 4),
            VerticalOptions = LayoutOptions.Center,
            Content         = _logToggleIcon,
            WidthRequest    = 44,
            HeightRequest   = 44
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
            VerticalOptions = LayoutOptions.Center,
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center,
        };
        optionsLabel.SetDynamicResource(Label.TextColorProperty, "TextSecondary");

        var optionsBtn = new Border
        {
            StrokeThickness = 0,
            BackgroundColor = Colors.Transparent,
            Padding         = new Thickness(6, 6),
            VerticalOptions = LayoutOptions.Center,
            Content         = optionsLabel,
            WidthRequest    = 44,
            HeightRequest   = 44
        };
        optionsBtn.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(() => { if (_item != null) FindParentPage()?.HandleOptionsRequested(_item); })
        });

        // ── Header row ────────────────────────────────────────────────────────
        var headerGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new ColumnDefinition { Width = GridLength.Auto },  // status dot
                new ColumnDefinition { Width = GridLength.Star },  // text
                new ColumnDefinition { Width = GridLength.Auto },  // log toggle
                new ColumnDefinition { Width = GridLength.Auto },  // options
            },
            Padding = new Thickness(18, 16, 18, 14)
        };
        headerGrid.Add(_statusDot,     0, 0);
        headerGrid.Add(textStack,      1, 0);
        headerGrid.Add(logToggleBtn,   2, 0);
        headerGrid.Add(optionsBtn,     3, 0);

        // ── Progress bar ──────────────────────────────────────────────────────
        var progressTrack = new BoxView
        {
            HeightRequest     = 6,
            CornerRadius      = 3,
            HorizontalOptions = LayoutOptions.Fill
        };
        progressTrack.SetDynamicResource(BoxView.ColorProperty, "ProgressTrack");

        _progressFill = new BoxView
        {
            HeightRequest     = 6,
            CornerRadius      = 3,
            HorizontalOptions = LayoutOptions.Start,
            WidthRequest      = 0
        };
        _progressFill.SetDynamicResource(BoxView.ColorProperty, "AccentLight");

        _trackContainer = new Grid
        {
            HeightRequest   = 6,
            VerticalOptions = LayoutOptions.Center,
            Children        = { progressTrack, _progressFill }
        };

        _trackContainer.SizeChanged += (s, e) =>
        {
            UpdateProgressFill();
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
        progressRow.Add(_trackContainer, 0, 0);
        progressRow.Add(_pctLabel,      1, 0);

        _progressSection = new VerticalStackLayout
        {
            Padding  = new Thickness(18, 0, 18, 16),
            Spacing  = 8,
            Children = { progressRow }
        };

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
            Children = { headerGrid, _progressSection, _logSection }
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



        Content = card;

        if (_item != null)
        {
            _item.PropertyChanged += OnItemPropertyChanged;
            Refresh();
        }
    }

    protected override void OnBindingContextChanged()
    {
        base.OnBindingContextChanged();

        var newItem = BindingContext as DownloadItem;
        if (ReferenceEquals(_item, newItem))
            return;

        if (_item != null)
        {
            _item.PropertyChanged -= OnItemPropertyChanged;
        }

        _item = newItem;

        if (_item != null)
        {
            _item.PropertyChanged += OnItemPropertyChanged;
            Refresh();
        }
    }

    // ── Log toggle ────────────────────────────────────────────────────────────

    private async void ToggleLog()
    {
        if (_item == null) return;
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
            if (_logExpanded && _item != null && e.PropertyName == nameof(DownloadItem.LogText))
                _logLabel.Text = string.IsNullOrWhiteSpace(_item.LogText)
                    ? "(no log yet)"
                    : _item.LogText.TrimEnd();
        });
    }

    private void Refresh()
    {
        if (_item == null) return;

        if (_titleLabel.Text != _item.Title)
            _titleLabel.Text = _item.Title;

        if (_authorLabel.Text != _item.Author)
            _authorLabel.Text = _item.Author;

        bool authorVisible = !string.IsNullOrEmpty(_item.Author);
        if (_authorLabel.IsVisible != authorVisible)
            _authorLabel.IsVisible = authorVisible;

        string statusText = _item.Status == DownloadStatus.Pending
            ? $"Queued — position #{_item.QueuePosition}"
            : _item.StatusText;
        if (_statusTextLabel.Text != statusText)
            _statusTextLabel.Text = statusText;

        if (_pctLabel.Text != _item.ProgressPct)
            _pctLabel.Text = _item.ProgressPct;

        Color dotBg = _item.StatusColor.WithAlpha(0.15f);
        if (_statusDot.BackgroundColor != dotBg)
            _statusDot.BackgroundColor = dotBg;

        if (_statusIconLabel.Text != _item.StatusIcon)
            _statusIconLabel.Text = _item.StatusIcon;

        if (_statusIconLabel.TextColor != _item.StatusColor)
            _statusIconLabel.TextColor = _item.StatusColor;

        bool done    = _item.IsDone;

        bool progressVisible = _item.Status is DownloadStatus.Downloading or DownloadStatus.Resuming or DownloadStatus.Paused;
        if (_progressSection.IsVisible != progressVisible)
            _progressSection.IsVisible = progressVisible;

        UpdateProgressFill();

        string strokeKey = done ? "Success" : (_item.IsFailed ? "Danger" : (_item.IsCancelled ? "Warning" : "Stroke"));
        if (_lastStrokeKey != strokeKey)
        {
            _lastStrokeKey = strokeKey;
            ((Border)Content).SetDynamicResource(Border.StrokeProperty, strokeKey);
        }
    }

    private void UpdateProgressFill()
    {
        if (_item == null) return;
        double trackWidth = _trackContainer.Width;
        if (trackWidth <= 0) return;

        if (_progressSection.IsVisible)
        {
            double newWidth = Math.Max(0, trackWidth * _item.Progress);
            if (Math.Abs(_progressFill.WidthRequest - newWidth) > 0.5)
            {
                _progressFill.WidthRequest = newWidth;
            }
        }
    }

}
