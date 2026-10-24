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
    private readonly Label  _logToggleIcon;
    private readonly Border _retranslateBtn;
    private readonly Label  _retranslateIcon;
    private readonly Label  _retranslateLabel;
    private readonly Grid   _trackContainer;
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

        _retranslateIcon = new Label
        {
            Text            = "\uE8BA", // sync
            FontFamily      = "MaterialSymbols",
            FontSize        = 13,
            TextColor       = Color.FromArgb("#FF9F0A"),
            VerticalOptions = LayoutOptions.Center,
        };
        _retranslateLabel = new Label
        {
            Text            = "Translate Again",
            FontSize        = 11,
            FontAttributes  = FontAttributes.Bold,
            TextColor       = Color.FromArgb("#FF9F0A"),
            VerticalOptions = LayoutOptions.Center,
        };
        var retransLayout = new HorizontalStackLayout
        {
            Spacing  = 4,
            Children = { _retranslateIcon, _retranslateLabel }
        };
        _retranslateBtn = new Border
        {
            StrokeThickness = 1,
            Stroke          = new SolidColorBrush(Color.FromArgb("#FF9F0A")),
            StrokeShape     = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 12 },
            BackgroundColor = Color.FromArgb("#26FF9F0A"),
            Padding         = new Thickness(8, 3),
            Margin          = new Thickness(0, 3, 0, 0),
            HorizontalOptions = LayoutOptions.Start,
            Content         = retransLayout,
            IsVisible       = false
        };
        _retranslateBtn.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(async () =>
            {
                if (_item != null && _item.HasFailedChapters && _item.Status == DownloadStatus.Completed)
                {
                    var page = FindParentPage();
                    if (page != null)
                    {
                        string chStr = _item.FailedChapters.Count == 1
                            ? $"Chapter {_item.FailedChapters[0]}"
                            : $"Chapters {string.Join(", ", _item.FailedChapters.Take(4))}{(_item.FailedChapters.Count > 4 ? "..." : "")}";
                        bool confirm = await page.DisplayAlertAsync(
                            "Translate Again",
                            $"'{_item.Title}' has {_item.UntranslatedCount} untranslated chapter(s) ({chStr}).\n\nTranslate again now?",
                            "Translate Again",
                            "Cancel");
                        if (confirm)
                        {
                            DownloadManager.Instance.RetranslateFailedChapters(_item);
                        }
                    }
                }
            })
        });

        var textStack = new VerticalStackLayout
        {
            Spacing         = 3,
            VerticalOptions = LayoutOptions.Center,
            Children        = { _titleLabel, _authorLabel, _statusTextLabel, _retranslateBtn }
        };

        // ── Log sheet button ─────────────────────────────────────────────────
        _logToggleIcon = new Label
        {
            Text            = "\uE873", // description / log sheet icon
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
            Command = new Command(() =>
            {
                if (_item != null)
                {
                    FindParentPage()?.ShowLogSheetAsync(_item);
                }
            })
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

        // ── Card assembly ─────────────────────────────────────────────────────
        var cardInner = new VerticalStackLayout
        {
            Children = { headerGrid, _progressSection }
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

    // ── Property change ───────────────────────────────────────────────────────

    private void OnItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            Refresh();
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

        bool hasFailed = done && _item.HasFailedChapters;
        if (hasFailed)
        {
            _statusTextLabel.TextColor = Color.FromArgb("#FF9F0A");
        }
        else
        {
            _statusTextLabel.SetDynamicResource(Label.TextColorProperty, "TextMuted");
        }

        if (_retranslateBtn.IsVisible != hasFailed)
            _retranslateBtn.IsVisible = hasFailed;

        if (hasFailed)
        {
            _retranslateLabel.Text = _item.UntranslatedCount > 0
                ? $"Translate {_item.UntranslatedCount} Failed Chapter{(_item.UntranslatedCount > 1 ? "s" : "")}"
                : "Translate Again";
        }

        bool progressVisible = _item.Status is DownloadStatus.Downloading or DownloadStatus.Resuming or DownloadStatus.Paused;
        if (_progressSection.IsVisible != progressVisible)
            _progressSection.IsVisible = progressVisible;

        UpdateProgressFill();

        string strokeKey = (done && !_item.HasFailedChapters)
            ? "Success"
            : ((done && _item.HasFailedChapters) ? "Warning" : (_item.IsFailed ? "Danger" : (_item.IsCancelled ? "Warning" : "Stroke")));
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
