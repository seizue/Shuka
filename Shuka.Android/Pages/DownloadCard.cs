using Shuka.Android.Services;

namespace Shuka.Android.Pages;

/// <summary>
/// A self-contained card view for a single DownloadItem.
/// Subscribes to PropertyChanged and updates itself live.
/// </summary>
public class DownloadCard : ContentView
{
    public event Action<DownloadItem>? CancelRequested;
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
    private readonly Border _cancelBtn;
    private readonly View   _progressSection;
    private readonly View   _actionRow;
    private readonly View   _retryRow;

    // Track card width for progress fill calculation
    private double _cardWidth = 0;

    public DownloadCard(DownloadItem item)
    {
        _item = item;

        _statusIconLabel = new Label
        {
            FontFamily      = "MaterialSymbols",
            FontSize        = 18,
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
            FontSize        = 14,
            FontAttributes  = FontAttributes.Bold,
            LineBreakMode   = LineBreakMode.TailTruncation,
            MaxLines        = 1
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

        var cancelLabel = new Label
        {
            Text           = "Cancel",
            FontSize       = 11,
            FontAttributes = FontAttributes.Bold,
            VerticalOptions = LayoutOptions.Center
        };
        cancelLabel.SetDynamicResource(Label.TextColorProperty, "Danger");

        _cancelBtn = new Border
        {
            StrokeThickness = 1,
            StrokeShape     = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
            Padding         = new Thickness(10, 6),
            VerticalOptions = LayoutOptions.Center,
            Content         = cancelLabel
        };
        _cancelBtn.SetDynamicResource(Border.BackgroundColorProperty, "BgInput");
        _cancelBtn.SetDynamicResource(Border.StrokeProperty, "Stroke");
        _cancelBtn.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(() => CancelRequested?.Invoke(_item))
        });

        var headerGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Star },
                new ColumnDefinition { Width = GridLength.Auto }
            },
            Padding = new Thickness(18, 16, 18, 14)
        };
        headerGrid.Add(_statusDot,  0, 0);
        headerGrid.Add(textStack,   1, 0);
        headerGrid.Add(_cancelBtn,  2, 0);

        var progressTrack = new Border
        {
            StrokeThickness = 0,
            StrokeShape     = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 3 },
            HeightRequest   = 6,
            HorizontalOptions = LayoutOptions.Fill
        };
        progressTrack.SetDynamicResource(Border.BackgroundColorProperty, "ProgressTrack");

        _progressFill = new Border
        {
            StrokeThickness = 0,
            StrokeShape     = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 3 },
            HeightRequest   = 6,
            HorizontalOptions = LayoutOptions.Start,
            WidthRequest    = 0
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
            FontSize       = 12,
            FontAttributes = FontAttributes.Bold,
            VerticalOptions = LayoutOptions.Center,
            Margin         = new Thickness(10, 0, 0, 0)
        };
        _pctLabel.SetDynamicResource(Label.TextColorProperty, "AccentLight");

        var progressRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new ColumnDefinition { Width = GridLength.Star },
                new ColumnDefinition { Width = GridLength.Auto }
            },
            ColumnSpacing = 0,
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

        var shareLabel = new Label
        {
            Text           = "Share",
            FontSize       = 13,
            FontAttributes = FontAttributes.Bold,
            VerticalOptions = LayoutOptions.Center
        };
        shareLabel.SetDynamicResource(Label.TextColorProperty, "TextOnAccent");

        var shareIcon = new Label
        {
            Text           = "\uE6B8",
            FontFamily     = "MaterialSymbols",
            FontSize       = 18,
            VerticalOptions = LayoutOptions.Center,
            Margin         = new Thickness(0, 0, 8, 0)
        };
        shareIcon.SetDynamicResource(Label.TextColorProperty, "TextOnAccent");

        var shareInner = new Grid { Padding = new Thickness(14, 0) };
        shareInner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        shareInner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Star });
        shareInner.Add(shareIcon,  0, 0);
        shareInner.Add(shareLabel, 1, 0);

        var shareBtn = new Border
        {
            StrokeThickness = 0,
            StrokeShape     = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 14 },
            HeightRequest   = 46,
            Padding         = new Thickness(0),
            Content         = shareInner
        };
        shareBtn.SetDynamicResource(Border.BackgroundColorProperty, "Accent");
        shareBtn.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(() => ShareRequested?.Invoke(_item))
        });

        var openLabel = new Label
        {
            Text           = "Open",
            FontSize       = 13,
            FontAttributes = FontAttributes.Bold,
            VerticalOptions = LayoutOptions.Center
        };
        openLabel.SetDynamicResource(Label.TextColorProperty, "TextSecondary");

        var openIcon = new Label
        {
            Text           = "\uE2C7",
            FontFamily     = "MaterialSymbols",
            FontSize       = 18,
            VerticalOptions = LayoutOptions.Center,
            Margin         = new Thickness(0, 0, 8, 0)
        };
        openIcon.SetDynamicResource(Label.TextColorProperty, "TextSecondary");

        var openInner = new Grid { Padding = new Thickness(14, 0) };
        openInner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        openInner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Star });
        openInner.Add(openIcon,  0, 0);
        openInner.Add(openLabel, 1, 0);

        var openBtn = new Border
        {
            StrokeThickness = 1,
            StrokeShape     = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 14 },
            HeightRequest   = 46,
            Padding         = new Thickness(0),
            Content         = openInner
        };
        openBtn.SetDynamicResource(Border.BackgroundColorProperty, "BgInput");
        openBtn.SetDynamicResource(Border.StrokeProperty, "Stroke");
        openBtn.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(() => OpenRequested?.Invoke(_item))
        });

        var actionGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new ColumnDefinition { Width = GridLength.Star },
                new ColumnDefinition { Width = new GridLength(12) },
                new ColumnDefinition { Width = GridLength.Star }
            },
            Padding    = new Thickness(18, 0, 18, 16),
            IsVisible  = false
        };
        actionGrid.Add(shareBtn, 0, 0);
        actionGrid.Add(openBtn,  2, 0);
        _actionRow = actionGrid;

        var retryLabel = new Label
        {
            Text            = "Retry",
            FontSize        = 13,
            FontAttributes  = FontAttributes.Bold,
            VerticalOptions = LayoutOptions.Center
        };
        retryLabel.SetDynamicResource(Label.TextColorProperty, "TextOnAccent");

        var retryIcon = new Label
        {
            Text            = "\uE5D5",
            FontFamily      = "MaterialSymbols",
            FontSize        = 18,
            VerticalOptions = LayoutOptions.Center,
            Margin          = new Thickness(0, 0, 8, 0)
        };
        retryIcon.SetDynamicResource(Label.TextColorProperty, "TextOnAccent");

        var retryInner = new Grid { Padding = new Thickness(14, 0) };
        retryInner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        retryInner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Star });
        retryInner.Add(retryIcon,  0, 0);
        retryInner.Add(retryLabel, 1, 0);

        var retryBtn = new Border
        {
            StrokeThickness = 0,
            StrokeShape     = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 14 },
            HeightRequest   = 46,
            Padding         = new Thickness(0),
            Content         = retryInner
        };
        retryBtn.SetDynamicResource(Border.BackgroundColorProperty, "Accent");
        retryBtn.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(() => RetryRequested?.Invoke(_item))
        });

        var dismissLabel = new Label
        {
            Text            = "Dismiss",
            FontSize        = 13,
            FontAttributes  = FontAttributes.Bold,
            VerticalOptions = LayoutOptions.Center
        };
        dismissLabel.SetDynamicResource(Label.TextColorProperty, "TextSecondary");

        var dismissIcon = new Label
        {
            Text            = "\uE5CD",
            FontFamily      = "MaterialSymbols",
            FontSize        = 18,
            VerticalOptions = LayoutOptions.Center,
            Margin          = new Thickness(0, 0, 8, 0)
        };
        dismissIcon.SetDynamicResource(Label.TextColorProperty, "TextSecondary");

        var dismissInner = new Grid { Padding = new Thickness(14, 0) };
        dismissInner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        dismissInner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Star });
        dismissInner.Add(dismissIcon,  0, 0);
        dismissInner.Add(dismissLabel, 1, 0);

        var dismissBtn = new Border
        {
            StrokeThickness = 1,
            StrokeShape     = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 14 },
            HeightRequest   = 46,
            Padding         = new Thickness(0),
            Content         = dismissInner
        };
        dismissBtn.SetDynamicResource(Border.BackgroundColorProperty, "BgInput");
        dismissBtn.SetDynamicResource(Border.StrokeProperty, "Stroke");
        dismissBtn.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(() => DismissRequested?.Invoke(_item))
        });

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

        var cardInner = new VerticalStackLayout
        {
            Children = { headerGrid, _progressSection, _actionRow, _retryRow }
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

        // Track width for progress fill
        card.SizeChanged += (s, e) =>
        {
            _cardWidth = card.Width - 36; // subtract padding
            UpdateProgressFill();
        };

        Content = card;

        // Subscribe to live updates
        _item.PropertyChanged += OnItemPropertyChanged;

        // Apply initial state
        Refresh();
    }

    private void OnItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(Refresh);
    }

    private void Refresh()
    {
        _titleLabel.Text      = _item.Title;
        _authorLabel.Text     = _item.Author;
        _authorLabel.IsVisible = !string.IsNullOrEmpty(_item.Author);
        _statusTextLabel.Text = _item.StatusText;
        _pctLabel.Text        = _item.ProgressPct;

        // Status dot color + icon
        _statusDot.BackgroundColor = _item.StatusColor.WithAlpha(0.15f);
        _statusIconLabel.Text      = _item.StatusIcon;
        _statusIconLabel.TextColor = _item.StatusColor;

        // Progress fill
        UpdateProgressFill();

        // Show/hide sections based on state
        bool running  = _item.IsRunning;
        bool done     = _item.IsDone;
        bool finished = _item.IsFinished;

        _cancelBtn.IsVisible       = running;
        _progressSection.IsVisible = running;
        _actionRow.IsVisible       = done;
        _retryRow.IsVisible        = _item.IsFailed || _item.IsCancelled;

        // Card border accent for done/failed
        if (done)
        {
            ((Border)Content).SetDynamicResource(Border.StrokeProperty, "Success");
        }
        else if (_item.IsFailed)
        {
            ((Border)Content).SetDynamicResource(Border.StrokeProperty, "Danger");
        }
        else if (_item.IsCancelled)
        {
            ((Border)Content).SetDynamicResource(Border.StrokeProperty, "Warning");
        }
        else
        {
            ((Border)Content).SetDynamicResource(Border.StrokeProperty, "Stroke");
        }
    }

    private void UpdateProgressFill()
    {
        if (_cardWidth <= 0) return;
        double fillWidth = _cardWidth * _item.Progress;
        _progressFill.WidthRequest = Math.Max(0, fillWidth);
    }
}
