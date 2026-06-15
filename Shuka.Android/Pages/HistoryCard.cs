using Shuka.Android.Services;

namespace Shuka.Android.Pages;

/// <summary>
/// A card showing a completed novel download with cover thumbnail,
/// title, author, chapter count, and action buttons.
/// </summary>
public class HistoryCard : ContentView
{
    public event Action<HistoryEntry>? OpenRequested;
    public event Action<HistoryEntry>? OptionsRequested;

    private readonly HistoryEntry _entry;

    public HistoryCard(HistoryEntry entry, bool isCompact)
    {
        _entry = entry;
        bool fileExists = entry.IsFileAvailable;

        if (isCompact)
        {
            // ── Grid view (Compact Grid style like Tachiyomi) ────────────────
            View coverView;
            if (entry.IsCoverAvailable && !string.IsNullOrWhiteSpace(entry.CoverLocalPath))
            {
                coverView = new Image
                {
                    Source = HistoryService.GetCoverImageSource(entry.CoverLocalPath),
                    Aspect = Aspect.AspectFill,
                };
            }
            else
            {
                var lilyImg = new Image
                {
                    Source            = HistoryService.GetCoverImageSource(null),
                    Aspect            = Aspect.AspectFit,
                    WidthRequest      = 28,
                    HeightRequest     = 28,
                    HorizontalOptions = LayoutOptions.Center,
                    VerticalOptions   = LayoutOptions.Center,
                    Opacity           = 0.45,
                };
                var fallbackGrid = new Grid();
                fallbackGrid.SetDynamicResource(Grid.BackgroundColorProperty, "AccentContainer");
                fallbackGrid.Add(lilyImg);
                coverView = fallbackGrid;
            }

            var titleLabel = new Label
            {
                Text          = entry.Title,
                FontSize      = 10.5,
                FontAttributes = FontAttributes.Bold,
                TextColor     = Colors.White,
                LineBreakMode = LineBreakMode.TailTruncation,
                MaxLines      = 2,
                Margin        = new Thickness(6, 4),
                VerticalOptions = LayoutOptions.Center,
            };

            var titleOverlay = new Border
            {
                StrokeThickness = 0,
                BackgroundColor = Color.FromRgba(0, 0, 0, 160), // semi-transparent black
                StrokeShape     = new Microsoft.Maui.Controls.Shapes.RoundRectangle 
                { 
                    CornerRadius = new CornerRadius(0, 0, 12, 12) 
                },
                VerticalOptions = LayoutOptions.End,
                Content         = titleLabel,
            };

            // Options button overlayed on the top right
            var moreIcon = new Label
            {
                Text            = "\uE5D4", // more_vert
                FontFamily      = "MaterialSymbols",
                FontSize        = 16,
                TextColor       = Colors.White,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions   = LayoutOptions.Center,
            };

            var moreBtn = new Border
            {
                StrokeThickness = 0,
                BackgroundColor = Color.FromRgba(0, 0, 0, 120),
                StrokeShape     = new Microsoft.Maui.Controls.Shapes.Ellipse(),
                WidthRequest    = 24,
                HeightRequest   = 24,
                HorizontalOptions = LayoutOptions.End,
                VerticalOptions = LayoutOptions.Start,
                Margin          = new Thickness(4),
                Content         = moreIcon,
            };

            moreBtn.GestureRecognizers.Add(new TapGestureRecognizer
            {
                Command = new Command(async () =>
                {
                    await moreBtn.ScaleToAsync(0.85, 70, Easing.CubicOut);
                    await moreBtn.ScaleToAsync(1.0,  70, Easing.SpringOut);
                    OptionsRequested?.Invoke(_entry);
                })
            });

            var cardGrid = new Grid();
            cardGrid.Add(coverView);
            cardGrid.Add(titleOverlay);
            cardGrid.Add(moreBtn);

            var card = new Border
            {
                StrokeThickness = 1,
                StrokeShape     = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 12 },
                Padding         = new Thickness(0),
                Content         = cardGrid,
            };
            card.SetDynamicResource(Border.BackgroundColorProperty, "BgCard");
            if (fileExists)
                card.SetDynamicResource(Border.StrokeProperty, "Stroke");
            else
                card.SetDynamicResource(Border.StrokeProperty, "Warning");

            card.GestureRecognizers.Add(new TapGestureRecognizer
            {
                Command = new Command(async () =>
                {
                    await card.ScaleToAsync(0.95, 60, Easing.CubicOut);
                    await card.ScaleToAsync(1.0,  60, Easing.SpringOut);
                    OpenRequested?.Invoke(_entry);
                })
            });

            Content = card;
        }
        else
        {
            // ── List view (standard layout) ──────────────────────────────────
            View coverView;
            if (entry.IsCoverAvailable && !string.IsNullOrWhiteSpace(entry.CoverLocalPath))
            {
                var img = new Image
                {
                    Source            = HistoryService.GetCoverImageSource(entry.CoverLocalPath),
                    Aspect            = Aspect.AspectFill,
                    WidthRequest      = 60,
                    HeightRequest     = 90,
                    HorizontalOptions = LayoutOptions.Center,
                    VerticalOptions   = LayoutOptions.Center,
                };
                coverView = new Border
                {
                    StrokeThickness = 0,
                    StrokeShape     = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
                    WidthRequest    = 60,
                    HeightRequest   = 90,
                    Content         = img,
                };
                ((Border)coverView).SetDynamicResource(Border.BackgroundColorProperty, "BgInput");
            }
            else
            {
                var lilyImg = new Image
                {
                    Source            = HistoryService.GetCoverImageSource(null),
                    Aspect            = Aspect.AspectFit,
                    WidthRequest      = 32,
                    HeightRequest     = 32,
                    HorizontalOptions = LayoutOptions.Center,
                    VerticalOptions   = LayoutOptions.Center,
                    Opacity           = 0.45,
                };

                coverView = new Border
                {
                    StrokeThickness = 0,
                    StrokeShape     = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
                    WidthRequest    = 60,
                    HeightRequest   = 90,
                    Content         = lilyImg,
                };
                ((Border)coverView).SetDynamicResource(Border.BackgroundColorProperty, "AccentContainer");
            }

            var titleLabel = new Label
            {
                Text          = entry.Title,
                FontSize      = 14,
                FontAttributes = FontAttributes.Bold,
                LineBreakMode = LineBreakMode.TailTruncation,
                MaxLines      = 2,
            };
            titleLabel.SetDynamicResource(Label.TextColorProperty, "TextPrimary");

            var authorLabel = new Label
            {
                Text          = string.IsNullOrWhiteSpace(entry.Author) ? "" : entry.Author,
                FontSize      = 12,
                LineBreakMode = LineBreakMode.TailTruncation,
                MaxLines      = 1,
                IsVisible     = !string.IsNullOrWhiteSpace(entry.Author),
            };
            authorLabel.SetDynamicResource(Label.TextColorProperty, "TextMuted");

            var metaLabel = new Label
            {
                Text     = $"{entry.ChapterCount} chapters  ·  {entry.CompletedAt:MMM d, yyyy}",
                FontSize = 11,
            };
            metaLabel.SetDynamicResource(Label.TextColorProperty, "TextMuted");

            var fileLabel = new Label
            {
                Text      = fileExists ? "\uE876  File available" : "\uE5CD  File missing",
                FontSize  = 11,
            };
            fileLabel.SetDynamicResource(Label.TextColorProperty,
                fileExists ? "Success" : "TextMuted");

            var textStack = new VerticalStackLayout
            {
                Spacing         = 4,
                VerticalOptions = LayoutOptions.Center,
                Children        = { titleLabel, authorLabel, metaLabel, fileLabel }
            };

            var moreIcon = new Label
            {
                Text            = "\uE5D4", // more_vert
                FontFamily      = "MaterialSymbols",
                FontSize        = 20,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions   = LayoutOptions.Center,
            };
            moreIcon.SetDynamicResource(Label.TextColorProperty, "TextMuted");

            var moreBtn = new Border
            {
                StrokeThickness = 0,
                BackgroundColor = Colors.Transparent,
                WidthRequest    = 40,
                HeightRequest   = 40,
                VerticalOptions = LayoutOptions.Center,
                Content         = moreIcon,
            };

            moreBtn.GestureRecognizers.Add(new TapGestureRecognizer
            {
                Command = new Command(async () =>
                {
                    await moreBtn.ScaleToAsync(0.85, 70, Easing.CubicOut);
                    await moreBtn.ScaleToAsync(1.0,  70, Easing.SpringOut);
                    OptionsRequested?.Invoke(_entry);
                })
            });

            var contentGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitionCollection
                {
                    new ColumnDefinition { Width = GridLength.Auto }, // coverView
                    new ColumnDefinition { Width = GridLength.Star }, // textStack
                    new ColumnDefinition { Width = GridLength.Auto }, // moreBtn
                },
                ColumnSpacing = 14,
                Padding       = new Thickness(12),
            };
            contentGrid.Add(coverView,   0, 0);
            contentGrid.Add(textStack,   1, 0);
            contentGrid.Add(moreBtn,     2, 0);

            var card = new Border
            {
                StrokeThickness = 1,
                StrokeShape     = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 20 },
                Padding         = new Thickness(0),
                Content         = contentGrid,
            };
            card.SetDynamicResource(Border.BackgroundColorProperty, "BgCard");
            if (fileExists)
                card.SetDynamicResource(Border.StrokeProperty, "Stroke");
            else
                card.SetDynamicResource(Border.StrokeProperty, "Warning");

            card.GestureRecognizers.Add(new TapGestureRecognizer
            {
                Command = new Command(async () =>
                {
                    await card.ScaleToAsync(0.97, 60, Easing.CubicOut);
                    await card.ScaleToAsync(1.0,  60, Easing.SpringOut);
                    OpenRequested?.Invoke(_entry);
                })
            });

            Content = card;
        }
    }
}
