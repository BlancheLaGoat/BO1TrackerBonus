using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace BO1Tracker
{
    public enum OverlayMode { Bo1Bonus, Bo2Bonus, Box }

    public partial class OverlayWindow : Window
    {
        private static readonly string SavePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BlackOpsTracker", "save.json");

        private FileSystemWatcher? _watcher;
        private bool _locked = false;

        public OverlayMode Mode { get; private set; }

        // ── État BO1/BO2 Bonus ───────────────────────────────────────────
        private List<(int Order, string Label, string ImagePath)> _bo1Clicks = [];
        private List<(int Order, string Label, string ImagePath)> _bo2Clicks = [];
        
        

        // ── État Boîte ───────────────────────────────────────────────────
        private List<(int Order, string Location)> _boxVisits   = [];
        private HashSet<string>                     _boxSelected = [];
        private string?                             _lastVisited = null;
        

        // ── Couleurs (correspondant exactement au tracker) ───────────────
        private static readonly Color ColAccent     = (Color)ColorConverter.ConvertFromString("#1D9E75");
        private static readonly Color ColAccentLight = (Color)ColorConverter.ConvertFromString("#2DD4A2");
        private static readonly Color ColAccentDark  = (Color)ColorConverter.ConvertFromString("#0D3327");
        private static readonly Color ColBgCard      = (Color)ColorConverter.ConvertFromString("#1A1A1A");
        private static readonly Color ColBorder      = (Color)ColorConverter.ConvertFromString("#2A2A2A");
        private static readonly Color ColTextSecond  = (Color)ColorConverter.ConvertFromString("#888888");
        private static readonly Color ColYellow      = (Color)ColorConverter.ConvertFromString("#FFD700");
        private static readonly Color ColYellowDark  = (Color)ColorConverter.ConvertFromString("#B8860B");
        private static readonly Color ColBgYellow    = (Color)ColorConverter.ConvertFromString("#3D3000");

        public OverlayWindow(OverlayMode mode = OverlayMode.Bo1Bonus)
        {
            InitializeComponent();
            Mode = mode;
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            UpdateModeButtons();
            ReadAndRefresh();

            var dir = Path.GetDirectoryName(SavePath)!;
            Directory.CreateDirectory(dir);

            _watcher = new FileSystemWatcher(dir, Path.GetFileName(SavePath))
            {
                NotifyFilter        = NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += (_, _) =>
                Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)ReadAndRefresh);
        }

        // ── Lecture JSON ─────────────────────────────────────────────────
        private void ReadAndRefresh()
        {
            try
            {
                if (!File.Exists(SavePath)) return;

                string? json = null;
                for (int i = 0; i < 5; i++)
                {
                    try { json = File.ReadAllText(SavePath); break; }
                    catch (IOException) { System.Threading.Thread.Sleep(60); }
                }
                if (json == null) return;

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                
                

                _bo1Clicks = ParseClicks(root, "ClickOrder");
                _bo2Clicks = ParseClicks(root, "Bo2ClickOrder");

                // Boîte
                
                _lastVisited   = root.TryGetProperty("LastBoxLocation", out var lb)  ? lb.GetString()  : null;
                _boxVisits   = [];
                _boxSelected = [];
                if (root.TryGetProperty("BoxVisits", out var bv))
                    foreach (var v in bv.EnumerateArray())
                    {
                        var loc = v.TryGetProperty("Location", out var l) ? l.GetString() ?? "" : "";
                        var ord = v.TryGetProperty("Order",    out var o) ? o.GetInt32()        : 0;
                        _boxVisits.Add((ord, loc));
                    }
                if (root.TryGetProperty("BoxSelected", out var bs))
                    foreach (var s in bs.EnumerateArray())
                        _boxSelected.Add(s.GetString() ?? "");

                RebuildCards();
            }
            catch { }
        }

        private static List<(int Order, string Label, string ImagePath)> ParseClicks(JsonElement root, string key)
        {
            var result = new List<(int, string, string)>();
            if (!root.TryGetProperty(key, out var arr)) return result;
            foreach (var v in arr.EnumerateArray())
            {
                int ord = v.TryGetProperty("Order", out var o) ? o.GetInt32() : 0;
                string lbl = "", img = "";
                if (v.TryGetProperty("Drop", out var d))
                {
                    if (d.TryGetProperty("Label",     out var l)) lbl = l.GetString() ?? "";
                    if (d.TryGetProperty("ImagePath", out var p)) img = p.GetString() ?? "";
                }
                result.Add((ord, lbl, img));
            }
            return result;
        }

        // ── Construction cartes ──────────────────────────────────────────
        private void RebuildCards()
        {
            CardsPanel.Children.Clear();
            switch (Mode)
            {
                case OverlayMode.Bo1Bonus: BuildBonusCards(_bo1Clicks); break;
                case OverlayMode.Bo2Bonus: BuildBonusCards(_bo2Clicks); break;
                case OverlayMode.Box:      BuildBoxCards();                              break;
            }
        }

        // ── Cartes Bonus ─────────────────────────────────────────────────
        private void BuildBonusCards(List<(int Order, string Label, string ImagePath)> clicks)
        {
            int maxOrder = clicks.Count > 0 ? clicks.Max(c => c.Order) : 0;
            foreach (var (ord, lbl, imgPath) in clicks.OrderBy(c => c.Order))
                CardsPanel.Children.Add(MakeBonusCard(lbl, imgPath, ord, ord == maxOrder && maxOrder > 0));

            bool empty = CardsPanel.Children.Count == 0;
            EmptyText.Text        = "En attente du premier bonus...";
            EmptyText.Visibility  = empty ? Visibility.Visible  : Visibility.Collapsed;
            CardsPanel.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        }

        private Border MakeBonusCard(string label, string imagePath, int order, bool isLast)
        {
            bool isSelected = true; // dans l'overlay, on n'affiche que les bonus déjà cliqués

            var bgColor     = isLast ? ColBgYellow  : ColAccentDark;
            var borderColor = isLast ? ColYellow     : ColAccent;
            var labelColor  = isLast ? ColYellow     : ColAccentLight;
            var badgeColor  = isLast ? ColYellowDark : ColAccent;

            // Badge numéro
            var orderBadge = new Border
            {
                Background          = new SolidColorBrush(badgeColor),
                CornerRadius        = new CornerRadius(10),
                Padding             = new Thickness(6, 1, 6, 1),
                Margin              = new Thickness(0, 0, 0, 4),
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = new TextBlock
                {
                    Text       = $"{order}",
                    FontFamily = new FontFamily("Consolas"),
                    FontSize   = 15,
                    FontWeight = FontWeights.Bold,
                    Foreground = Brushes.White,
                }
            };

            // Image du bonus
            var img = new Image
            {
                Width               = 52,
                Height              = 52,
                Stretch             = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                Source              = TryLoadImage(imagePath),
            };

            var lblBlock = new TextBlock
            {
                Text          = label.ToUpper(),
                FontFamily    = new FontFamily("Consolas"),
                FontSize      = 11,
                FontWeight    = FontWeights.SemiBold,
                TextAlignment = TextAlignment.Center,
                TextWrapping  = TextWrapping.Wrap,
                Foreground    = new SolidColorBrush(labelColor),
                MaxWidth      = 90,
            };

            var stack = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Center };
            stack.Children.Add(orderBadge);
            stack.Children.Add(img);
            stack.Children.Add(new Border { Height = 6 });
            stack.Children.Add(lblBlock);

            var card = new Border
            {
                Child           = stack,
                MinWidth        = 96,
                Padding         = new Thickness(14, 12, 14, 12),
                CornerRadius    = new CornerRadius(10),
                BorderThickness = new Thickness(2),
                Background      = new SolidColorBrush(bgColor),
                BorderBrush     = new SolidColorBrush(borderColor),
                Margin          = new Thickness(0, 0, 12, 0),
            };

            card.Effect = isLast
                ? new DropShadowEffect { Color = ColYellow,  BlurRadius = 14, ShadowDepth = 0, Opacity = 0.6 }
                : new DropShadowEffect { Color = ColAccent,  BlurRadius = 14, ShadowDepth = 0, Opacity = 0.55 };

            return card;
        }

        // ── Cartes Boîte ─────────────────────────────────────────────────
        private void BuildBoxCards()
        {
            int maxOrder = _boxVisits.Count > 0 ? _boxVisits.Max(v => v.Order) : 0;
            foreach (var (ord, loc) in _boxVisits.OrderBy(v => v.Order))
                CardsPanel.Children.Add(MakeBoxCard(loc, ord, ord == maxOrder && maxOrder > 0, false));

            if (_lastVisited != null && !_boxSelected.Contains(_lastVisited))
                CardsPanel.Children.Add(MakeBoxCard(_lastVisited, null, false, true));

            bool empty = CardsPanel.Children.Count == 0;
            EmptyText.Text        = "En attente du premier emplacement...";
            EmptyText.Visibility  = empty ? Visibility.Visible  : Visibility.Collapsed;
            CardsPanel.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        }

        private Border MakeBoxCard(string location, int? order, bool isLast, bool isMemo)
        {
            bool highlight  = isLast || isMemo;
            var bgColor     = highlight ? ColBgYellow : (order.HasValue ? ColAccentDark : ColBgCard);
            var borderColor = highlight ? ColYellow   : (order.HasValue ? ColAccent     : ColBorder);
            var labelColor  = highlight ? ColYellow   : (order.HasValue ? ColAccentLight : ColTextSecond);
            var badgeColor  = highlight ? ColYellowDark : ColAccent;

            var orderBadge = new Border
            {
                Background          = new SolidColorBrush(badgeColor),
                CornerRadius        = new CornerRadius(10),
                Padding             = new Thickness(6, 1, 6, 1),
                Margin              = new Thickness(0, 0, 0, 4),
                HorizontalAlignment = HorizontalAlignment.Center,
                Visibility          = order.HasValue ? Visibility.Visible : Visibility.Collapsed,
                Child = new TextBlock
                {
                    Text       = order.HasValue ? $"{order}" : "",
                    FontFamily = new FontFamily("Consolas"),
                    FontSize   = 15,
                    FontWeight = FontWeights.Bold,
                    Foreground = Brushes.White,
                }
            };

            var img = new Image
            {
                Width               = 52,
                Height              = 52,
                Stretch             = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin              = new Thickness(0, 0, 0, 6),
                Source              = TryLoadLocationImage(location),
            };

            var lbl = new TextBlock
            {
                Text          = location.ToUpper(),
                FontFamily    = new FontFamily("Consolas"),
                FontSize      = 11,
                FontWeight    = FontWeights.SemiBold,
                TextAlignment = TextAlignment.Center,
                TextWrapping  = TextWrapping.Wrap,
                Foreground    = new SolidColorBrush(labelColor),
                MaxWidth      = 90,
            };

            var stack = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Center };
            stack.Children.Add(orderBadge);
            stack.Children.Add(img);
            stack.Children.Add(new Border { Height = 6 });
            stack.Children.Add(lbl);

            var card = new Border
            {
                Child           = stack,
                MinWidth        = 96,
                Padding         = new Thickness(14, 12, 14, 12),
                CornerRadius    = new CornerRadius(10),
                BorderThickness = new Thickness(highlight || order.HasValue ? 2 : 1),
                Background      = new SolidColorBrush(bgColor),
                BorderBrush     = new SolidColorBrush(borderColor),
                Margin          = new Thickness(0, 0, 12, 0),
            };

            if (highlight)
                card.Effect = new DropShadowEffect { Color = ColYellow, BlurRadius = 14, ShadowDepth = 0, Opacity = 0.6 };
            else if (order.HasValue)
                card.Effect = new DropShadowEffect { Color = ColAccent, BlurRadius = 14, ShadowDepth = 0, Opacity = 0.55 };

            return card;
        }

        // ── Chargement images (pack URI = ressources embarquées) ─────────
        private static BitmapImage? TryLoadImage(string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath)) return null;
            try
            {
                var uri = new Uri($"pack://application:,,,/{relativePath}", UriKind.Absolute);
                var bmp = new BitmapImage();
                bmp.BeginInit(); bmp.UriSource = uri; bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.EndInit();
                return bmp;
            }
            catch { return null; }
        }

        private static BitmapImage? TryLoadLocationImage(string location)
        {
            return TryLoadImage($"Images/locations/{location}.png")
                ?? TryLoadImage("Images/mystery_box.png");
        }

        // ── Sélecteur de mode ────────────────────────────────────────────
        private void UpdateModeButtons()
        {
            var active   = new SolidColorBrush(Color.FromArgb(200, 29, 158, 117));
            var inactive = new SolidColorBrush(Color.FromArgb(50,  255, 255, 255));
            var fgActive   = Brushes.White;
            var fgInactive = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA));

            ModeBO1Btn.Background = Mode == OverlayMode.Bo1Bonus ? active : inactive;
            ModeBO2Btn.Background = Mode == OverlayMode.Bo2Bonus ? active : inactive;
            ModeBoxBtn.Background = Mode == OverlayMode.Box      ? active : inactive;
            ModeBO1Btn.Foreground = Mode == OverlayMode.Bo1Bonus ? fgActive : fgInactive;
            ModeBO2Btn.Foreground = Mode == OverlayMode.Bo2Bonus ? fgActive : fgInactive;
            ModeBoxBtn.Foreground = Mode == OverlayMode.Box      ? fgActive : fgInactive;
        }

        private void ModeBO1_Click(object sender, RoutedEventArgs e) { Mode = OverlayMode.Bo1Bonus; UpdateModeButtons(); RebuildCards(); }
        private void ModeBO2_Click(object sender, RoutedEventArgs e) { Mode = OverlayMode.Bo2Bonus; UpdateModeButtons(); RebuildCards(); }
        private void ModeBox_Click (object sender, RoutedEventArgs e) { Mode = OverlayMode.Box;      UpdateModeButtons(); RebuildCards(); }

        // ── Drag / contrôles ─────────────────────────────────────────────
        private void DragHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!_locked && e.ButtonState == MouseButtonState.Pressed)
                DragMove();
        }

        private void LockButton_Click(object sender, RoutedEventArgs e)
        {
            _locked = !_locked;
            LockBtn.Content = _locked ? "UNLOCK" : "LOCK";
        }

        private void TopButton_Click(object sender, RoutedEventArgs e)
        {
            Topmost = !Topmost;
            TopBtn.Content = Topmost ? "FOND" : "DEVANT";
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        private void OverlayWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
            => _watcher?.Dispose();
    }
}
