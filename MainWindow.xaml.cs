using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Input;

namespace BO1Tracker;

// ================================================================
//  MODÈLES DE DONNÉES
// ================================================================

record DropItem(string Id, string Label, string ImagePath);
record MapData(string Id, string Name, List<DropItem> Drops, List<string> BoxLocations);

/// <summary>Un bonus cliqué à un instant T dans le cycle courant.</summary>
record ClickedDrop(int Order, DropItem Drop);

/// <summary>Snapshot d'un cycle bonus terminé avec l'ordre de clic.</summary>
record CycleRecord(int Num, List<ClickedDrop> Clicks);

/// <summary>Un emplacement de boîte visité dans le cycle courant.</summary>
record VisitedBox(int Order, string Location);

/// <summary>Snapshot d'un cycle boîte terminé.</summary>
record BoxCycleRecord(int Num, List<VisitedBox> Visits);


// ================================================================
//  MODÈLE DE SAUVEGARDE / SYNCHRONISATION
// ================================================================

class AppState
{
    public string?          CurrentMapId      { get; set; }
    public List<string>     DisabledIds       { get; set; } = [];
    public List<string>     BoxSelected       { get; set; } = [];
    public List<VisitedBox> BoxVisits         { get; set; } = [];
    public List<BoxCycleRecord> BoxCycleHistory { get; set; } = [];
    public int              BoxCycleCount     { get; set; }
    public List<string>     Selected          { get; set; } = [];
    public List<ClickedDrop> ClickOrder       { get; set; } = [];
    public List<CycleRecord> CycleHistory     { get; set; } = [];
    public int              CycleCount        { get; set; }
    public int?             CurrentDogRound   { get; set; }
    public List<int>        DogRoundHistory   { get; set; } = [];
    // BO2
    public string?          CurrentBo2MapId   { get; set; }
    public List<string>     Bo2DisabledIds    { get; set; } = [];
    public List<string>     Bo2Selected       { get; set; } = [];
    public List<ClickedDrop> Bo2ClickOrder    { get; set; } = [];
    public List<CycleRecord> Bo2CycleHistory  { get; set; } = [];
    public int              Bo2CycleCount     { get; set; }
    // Multi
    public string           PlayerName        { get; set; } = "";
    // Boîte
    public string?          LastBoxLocation   { get; set; }
}

// ================================================================
//  CONFIG TWITCH — fichier séparé, JAMAIS synchronisé sur le réseau
//  (contient un refresh token : il ne doit pas transiter entre joueurs)
// ================================================================
class TwitchConfig
{
    public string TwitchClientId       { get; set; } = "";
    public string TwitchRefreshToken   { get; set; } = "";
    public string TwitchUserId         { get; set; } = "";
    public string TwitchLogin          { get; set; } = "";
    public string TwitchCommandTemplate { get; set; } = "!editcom !dogs {0}";
    public bool   TwitchAutoSend       { get; set; } = false;
    public string TwitchListenCommand  { get; set; } = "!dogs";
    public bool   TwitchListenEnabled  { get; set; } = true;
}

// ================================================================
//  FENÊTRE PRINCIPALE
// ================================================================

public partial class MainWindow : Window
{
    // ----------------------------------------------------------------
    //  DONNÉES — BONUS
    // ----------------------------------------------------------------
    private static readonly DropItem ItemMaxAmmo   = new("max_ammo",      "Max Ammo",      "Images/max_ammo.png");
    private static readonly DropItem ItemInstaKill = new("insta_kill",    "Insta-Kill",    "Images/insta_kill.png");
    private static readonly DropItem ItemDoublePts = new("double_pts",    "Double Points", "Images/double_pts.jpg");
    private static readonly DropItem ItemNuke      = new("nuke",          "Nuke",          "Images/nuke.png");
    private static readonly DropItem ItemCarpenter = new("carpenter",     "Carpenter",     "Images/carpenter.png");
    private static readonly DropItem ItemFireSale  = new("fire_sale",     "Fire Sale",     "Images/fire_sale.png");
    private static readonly DropItem ItemDeath      = new("death_machine", "Death Machine", "Images/death_machine.png");
    private static readonly DropItem ItemZombieBlood = new("zombie_blood",   "Zombie Blood",  "Images/zombie_blood.png");

    private static readonly DropItem[] BaseDrops = [ItemMaxAmmo, ItemInstaKill, ItemDoublePts, ItemNuke, ItemCarpenter];

    // ----------------------------------------------------------------
    //  DONNÉES — MAPS + EMPLACEMENTS BOÎTE
    // ----------------------------------------------------------------
    private static readonly List<MapData> Maps =
    [
        new("kino",      "Kino der Toten",   [..BaseDrops, ItemFireSale],
            ["First Room", "Mule", "Speed Cola", "Dressing", "Stage", "Theater", "Boiler Room", "Double Tap", "MPL"]),

        new("five",      "Five",             [..BaseDrops, ItemFireSale, ItemDeath],
            ["Olympia", "MPL", "Middle", "Bowie", "Pig", "Labs"]),

        new("ascension", "Ascension",        [..BaseDrops, ItemFireSale, ItemDeath],
            ["First Room", "Stam", "Clays", "PHD", "Power", "PaP", "Mule", "Speed Cola"]),

        new("cotd",      "Call of the Dead", [..BaseDrops, ItemFireSale, ItemDeath],
            ["First Room", "PHD", "Lighthouse", "Stam", "Boat", "Mule"]),

        new("shang",     "Shangri-La",       [..BaseDrops, ItemFireSale],
            ["First Room", "Waterfall", "Power", "AKU"]),

        new("moon",      "Moon",             [..BaseDrops, ItemFireSale, ItemDeath],
            ["First Room", "Power", "Mule", "Dome"]),

        new("nacht",     "Nacht der Untoten",[..BaseDrops],
            []),

        new("verruckt",  "Verrückt",         [..BaseDrops],
            ["Jug", "Double Tap", "Power", "Thompson", "STG-44"]),

        new("shi",       "Shi No Numa",      [..BaseDrops],
            ["Lobby", "Top Mid", "Comms", "Storage", "Doctors", "Fishing"]),

        new("riese",     "Der Riese",        [..BaseDrops],
            ["Power", "Thompson", "Trench", "Mp40", "Bowie", "Type"]),
    ];
    // ----------------------------------------------------------------
    //  DONNÉES — MAPS BO2
    // ----------------------------------------------------------------
    // BaseDrops = [MaxAmmo, InstaKill, DoublePoints, Nuke, Carpenter]
    // Tranzit/Depot/Town/Farm/DieRise : carpenter + nuke (pas fire sale par défaut)
    // Nuketown : fire sale (pas carpenter)
    // Mob of the Dead : fire sale (pas carpenter)
    // Buried : carpenter + fire sale
    // Origins : carpenter + fire sale + zombie blood
    private static readonly DropItem[] Bo2BaseDrops = [ItemMaxAmmo, ItemInstaKill, ItemDoublePts, ItemNuke];

    private static readonly List<MapData> Bo2Maps =
    [
        new("tranzit",  "TranZit",         [..Bo2BaseDrops, ItemCarpenter],                  []),
        new("depot",    "Depot",            [..Bo2BaseDrops, ItemCarpenter],                  []),
        new("town",     "Town",             [..Bo2BaseDrops, ItemCarpenter],                  []),
        new("farm",     "Farm",             [..Bo2BaseDrops, ItemCarpenter],                  []),
        new("nuketown", "Nuketown Zombies", [..Bo2BaseDrops, ItemFireSale],                   []),
        new("die_rise", "Die Rise",         [..Bo2BaseDrops, ItemCarpenter],                  []),
        new("mob",      "Mob of the Dead",  [..Bo2BaseDrops, ItemFireSale],                   []),
        new("buried",   "Buried",           [..Bo2BaseDrops, ItemCarpenter, ItemFireSale],     []),
        new("origins",  "Origins",          [..Bo2BaseDrops, ItemCarpenter, ItemFireSale, ItemZombieBlood], []),
    ];

    // ----------------------------------------------------------------
    //  ÉTAT — BO2
    // ----------------------------------------------------------------
    private MapData?          _currentBo2Map   = null;
    private List<DropItem>    _currentBo2Drops = [];
    private HashSet<string>   _bo2Selected     = [];
    private List<ClickedDrop> _bo2ClickOrder   = [];
    private List<CycleRecord> _bo2CycleHistory = [];
    private int               _bo2CycleCount   = 0;

    private readonly HashSet<string>              _bo2DisabledIds    = [];
    private readonly Dictionary<string, ToggleButton> _bo2MapButtons     = [];
    private readonly Dictionary<string, ToggleButton> _bo2ToggleButtons  = [];




    // ----------------------------------------------------------------
    //  ÉTAT — BONUS
    // ----------------------------------------------------------------
    private MapData?          _currentMap   = null;
    private List<DropItem>    _currentDrops = [];
    private HashSet<string>   _selected     = [];
    private List<ClickedDrop> _clickOrder   = [];
    private List<CycleRecord> _cycleHistory = [];
    private int               _cycleCount   = 0;

    private readonly HashSet<string>              _disabledIds   = [];
    private readonly Dictionary<string, ToggleButton> _mapButtons    = [];
    private readonly Dictionary<string, ToggleButton> _toggleButtons = [];

    // ----------------------------------------------------------------
    //  ÉTAT — BOÎTE
    // ----------------------------------------------------------------
    private HashSet<string>      _boxSelected     = [];
    private List<VisitedBox>     _boxVisits        = [];
    private List<BoxCycleRecord> _boxCycleHistory  = [];
    private int                  _boxCycleCount    = 0;

    // ----------------------------------------------------------------
    //  ÉTAT — MANCHES SPÉCIALES
    // ----------------------------------------------------------------
    private int?       _currentDogRound = null;
    private List<int>  _dogRoundHistory = [];

    // ----------------------------------------------------------------
    //  BRUSHES
    // ----------------------------------------------------------------
    private static SolidColorBrush Brush(string key) =>
        (SolidColorBrush)Application.Current.Resources[key];

    // ----------------------------------------------------------------
    //  SAUVEGARDE AUTOMATIQUE
    // ----------------------------------------------------------------
    private static readonly string SavePath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "BlackOpsTracker", "save.json");

    private static readonly string TwitchConfigPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "BlackOpsTracker", "twitch.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    // ================================================================
    //  TWITCH — état runtime (le token d'accès n'est jamais sauvegardé,
    //  seul le refresh token l'est ; on redemande un access token à
    //  chaque lancement via RefreshAsync)
    // ================================================================

    // Client ID de l'application Twitch (créée une seule fois sur
    // dev.twitch.tv/console/apps). Ce n'est PAS une donnée secrète :
    // c'est l'identifiant du LOGICIEL, pas d'un compte. Tous les
    // utilisateurs de l'app partagent ce même Client ID, mais chacun
    // se connecte ensuite avec SON PROPRE compte Twitch.
    private const string TwitchClientId = "COLLE_TON_CLIENT_ID_ICI";

    private TwitchConfig _twitchConfig = new();
    private string?      _twitchAccessToken = null;
    private CancellationTokenSource? _twitchAuthCts = null;

    private void SaveTwitchConfig()
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(TwitchConfigPath)!);
            File.WriteAllText(TwitchConfigPath, JsonSerializer.Serialize(_twitchConfig, JsonOpts));
        }
        catch { }
    }

    private void LoadTwitchConfig()
    {
        try
        {
            if (!File.Exists(TwitchConfigPath)) return;
            var cfg = JsonSerializer.Deserialize<TwitchConfig>(File.ReadAllText(TwitchConfigPath), JsonOpts);
            if (cfg != null) _twitchConfig = cfg;
        }
        catch { }
    }

    /// <summary>Envoie la commande avec TOUT l'historique des manches spéciales dans le chat Twitch.</summary>
    private void SendTwitchRoundCommand()
    {
        if (!_twitchConfig.TwitchAutoSend) return;
        if (string.IsNullOrWhiteSpace(_twitchConfig.TwitchUserId) || string.IsNullOrWhiteSpace(_twitchConfig.TwitchClientId))
        {
            SetTwitchStatus("Non connecté à Twitch.", isError: true);
            return;
        }
        if (_dogRoundHistory.Count == 0) return;

        // Liste triée du plus petit au plus grand : "5, 10, 14, 19, 23, 28, 32"
        string roundsList = string.Join(", ", _dogRoundHistory.OrderBy(r => r));

        string message;
        try { message = string.Format(_twitchConfig.TwitchCommandTemplate, roundsList); }
        catch { message = $"!dogs {roundsList}"; }

        _ = Task.Run(async () =>
        {
            var token = await EnsureTwitchAccessTokenAsync();
            if (token == null) return;

            var (success, error) = await TwitchChatService.SendMessageAsync(
                _twitchConfig.TwitchClientId, token, _twitchConfig.TwitchUserId, _twitchConfig.TwitchUserId, message);

            Dispatcher.Invoke(() =>
            {
                if (success) SetTwitchStatus($"Envoyé : \"{message}\"", isError: false);
                else         SetTwitchStatus($"Échec envoi : {error}", isError: true);
            });
        });
    }

    /// <summary>Retourne un access token valide, en rafraîchissant via le refresh token si besoin.</summary>
    private async Task<string?> EnsureTwitchAccessTokenAsync()
    {
        if (_twitchAccessToken != null) return _twitchAccessToken;
        if (string.IsNullOrWhiteSpace(_twitchConfig.TwitchRefreshToken)) return null;

        var result = await TwitchAuthService.RefreshAsync(_twitchConfig.TwitchClientId, _twitchConfig.TwitchRefreshToken);
        if (!result.Success || result.AccessToken == null)
        {
            Dispatcher.Invoke(() => SetTwitchStatus(result.Error ?? "Reconnexion Twitch nécessaire.", isError: true));
            return null;
        }

        _twitchAccessToken = result.AccessToken;
        _twitchConfig.TwitchRefreshToken = result.RefreshToken ?? _twitchConfig.TwitchRefreshToken;
        if (result.UserId != null) _twitchConfig.TwitchUserId = result.UserId;
        if (result.Login  != null) _twitchConfig.TwitchLogin  = result.Login;
        SaveTwitchConfig();
        return _twitchAccessToken;
    }

    private void SetTwitchStatus(string text, bool isError)
    {
        if (TwitchStatusText == null) return;
        TwitchStatusText.Text = text;
        TwitchStatusText.Foreground = isError ? Brushes.IndianRed : Brush("BrushAccentLight");
    }

    /// <summary>Démarre l'écoute du chat (WebSocket) pour répondre automatiquement à la commande configurée.</summary>
    private void StartTwitchChatListening()
    {
        if (_twitchAccessToken == null || string.IsNullOrWhiteSpace(_twitchConfig.TwitchUserId)) return;

        TwitchChatListener.Start(
            _twitchConfig.TwitchClientId, _twitchAccessToken, _twitchConfig.TwitchUserId, _twitchConfig.TwitchUserId,
            onChatMessage: (chatterId, chatterLogin, text) => Dispatcher.Invoke(() => OnTwitchChatMessage(chatterId, text)),
            onStatus: status => Dispatcher.Invoke(() => SetTwitchStatus(status, isError: status.Contains("Erreur") || status.Contains("perdue"))));
    }

    /// <summary>Appelé pour chaque message reçu dans le chat Twitch.</summary>
    private void OnTwitchChatMessage(string chatterId, string text)
    {
        if (!_twitchConfig.TwitchListenEnabled) return;
        // On ignore nos propres messages (évite toute boucle avec la réponse automatique)
        if (chatterId == _twitchConfig.TwitchUserId) return;

        var trigger = _twitchConfig.TwitchListenCommand.Trim();
        if (string.IsNullOrWhiteSpace(trigger)) return;

        if (string.Equals(text.Trim(), trigger, StringComparison.OrdinalIgnoreCase))
            SendTwitchRoundCommand();
    }

    private void TwitchConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TwitchClientId) || TwitchClientId == "COLLE_TON_CLIENT_ID_ICI")
        {
            MessageBox.Show(
                "Le Client ID Twitch n'a pas été configuré dans le code de l'app.\n" +
                "(Constante TwitchClientId dans MainWindow.xaml.cs)",
                "Configuration manquante", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _twitchConfig.TwitchClientId = TwitchClientId;
        SaveTwitchConfig();

        _twitchAuthCts?.Cancel();
        _twitchAuthCts = new CancellationTokenSource();
        var ct = _twitchAuthCts.Token;

        SetTwitchStatus("Connexion en cours…", isError: false);
        TwitchConnectButton.IsEnabled = false;

        _ = Task.Run(async () =>
        {
            var result = await TwitchAuthService.AuthenticateAsync(TwitchClientId, (userCode, verificationUri) =>
            {
                Dispatcher.Invoke(() =>
                {
                    SetTwitchStatus($"Va sur {verificationUri} et entre le code : {userCode}", isError: false);
                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(verificationUri) { UseShellExecute = true });
                    }
                    catch { /* si l'ouverture auto du navigateur échoue, le lien reste affiché */ }
                });
            }, ct);

            Dispatcher.Invoke(() =>
            {
                TwitchConnectButton.IsEnabled = true;
                if (result.Success)
                {
                    _twitchAccessToken = result.AccessToken;
                    _twitchConfig.TwitchRefreshToken = result.RefreshToken ?? "";
                    _twitchConfig.TwitchUserId       = result.UserId ?? "";
                    _twitchConfig.TwitchLogin        = result.Login ?? "";
                    SaveTwitchConfig();
                    SetTwitchStatus($"Connecté en tant que {result.Login}", isError: false);
                    StartTwitchChatListening();
                }
                else
                {
                    SetTwitchStatus(result.Error ?? "Échec de connexion.", isError: true);
                }
            });
        }, ct);
    }

    private void TwitchDisconnectButton_Click(object sender, RoutedEventArgs e)
    {
        _twitchAuthCts?.Cancel();
        TwitchChatListener.Stop();
        _twitchAccessToken = null;
        _twitchConfig.TwitchRefreshToken = "";
        _twitchConfig.TwitchUserId = "";
        _twitchConfig.TwitchLogin = "";
        SaveTwitchConfig();
        SetTwitchStatus("Déconnecté.", isError: false);
    }

    private void TwitchAutoSendCheck_Changed(object sender, RoutedEventArgs e)
    {
        _twitchConfig.TwitchAutoSend = TwitchAutoSendCheck.IsChecked == true;
        SaveTwitchConfig();
    }

    private void TwitchListenEnabledCheck_Changed(object sender, RoutedEventArgs e)
    {
        _twitchConfig.TwitchListenEnabled = TwitchListenEnabledCheck.IsChecked == true;
        SaveTwitchConfig();
    }

    private void TwitchListenCommandBox_LostFocus(object sender, RoutedEventArgs e)
    {
        var text = TwitchListenCommandBox.Text.Trim();
        _twitchConfig.TwitchListenCommand = string.IsNullOrWhiteSpace(text) ? "!dogs" : text;
        SaveTwitchConfig();
    }

    private void TwitchCommandTemplateBox_LostFocus(object sender, RoutedEventArgs e)
    {
        var text = TwitchCommandTemplateBox.Text.Trim();
        _twitchConfig.TwitchCommandTemplate = string.IsNullOrWhiteSpace(text) ? "!editcom !dogs {0}" : text;
        SaveTwitchConfig();
    }

    // ================================================================
    //  MULTIJOUEUR — variables réseau
    // ================================================================
    private enum NetRole { None, Host, Client }
    private NetRole _netRole = NetRole.None;

    private HttpListener? _httpListener;
    private CancellationTokenSource? _listenerCts;

    // L'hôte diffuse son état aux clients via polling
    // Le client poll l'hôte toutes les 500ms
    private CancellationTokenSource? _pollCts;

    // Flag pour éviter les boucles de sync infinie
    private bool _isSyncingFromRemote = false;
    // Dernière boîte du cycle
    private string? _lastVisitedLocation = null;

    private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(3) };

    // URL de l'hôte (utilisée côté client)
    private string _hostUrl = "";

    // Joueurs connectés : IP → pseudo (côté hôte uniquement)
    private readonly Dictionary<string, string> _connectedPlayers = [];
    // Joueurs en attente de validation : IP → pseudo
    private readonly Dictionary<string, string> _pendingPlayers   = [];
    // Joueurs bannis (exclus) : IP
    private readonly HashSet<string>            _bannedPlayers     = [];

    // ================================================================
    //  DÉMARRER EN MODE HÔTE
    // ================================================================
    private void StartHostButton_Click(object sender, RoutedEventArgs e)
    {
        if (_netRole != NetRole.None)
            Disconnect();

        if (!int.TryParse(HostPortBox.Text, out int port) || port < 1024 || port > 65535)
            port = 5757;

        // On essaie plusieurs préfixes dans l'ordre :
        // 1) http://*:port/   → écoute sur toutes les interfaces (idéal pour VPN), nécessite parfois admin
        // 2) http://localhost:port/ + http://127.0.0.1:port/ → fonctionne sans droits admin,
        //    mais le client doit se connecter à l'IP VPN qui sera routée vers localhost via netsh ou l'OS
        var prefixSets = new[]
        {
            new[] { $"http://*:{port}/" },
            new[] { $"http://localhost:{port}/", $"http://127.0.0.1:{port}/" },
        };

        HttpListener? listener = null;
        string[] usedPrefixes  = [];

        foreach (var prefixes in prefixSets)
        {
            var l = new HttpListener();
            foreach (var p in prefixes) l.Prefixes.Add(p);
            try
            {
                l.Start();
                listener     = l;
                usedPrefixes = prefixes;
                break;
            }
            catch
            {
                try { l.Stop(); } catch { }
            }
        }

        if (listener == null)
        {
            SetMultiStatus("🔴  Impossible d'ouvrir le port. Lancez l'appli en Administrateur ou changez de port.", "#FF6B6B");
            return;
        }

        _httpListener    = listener;
        _listenerCts     = new CancellationTokenSource();
        _netRole         = NetRole.Host;

        Task.Run(() => ListenLoop(_listenerCts.Token));

        bool fullAccess = usedPrefixes[0].Contains("*");
        if (fullAccess)
            SetMultiStatus($"🟢  HÔTE actif sur le port {port} — partagez votre IP VPN au co-joueur", "#1D9E75");
        else
            SetMultiStatus($"🟡  HÔTE actif (mode limité) sur le port {port} — si le client ne se connecte pas, lancez en Administrateur", "#F0A500");

        // Affiche le panneau joueurs (hôte uniquement)
        _connectedPlayers.Clear();
        PlayersListBorder.Visibility = Visibility.Visible;
        RefreshPlayersList();
        DisconnectButton.Visibility = Visibility.Visible;
    }

    /// <summary>Boucle serveur HTTP : attend les requêtes et répond.</summary>
    private async Task ListenLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _httpListener != null)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _httpListener.GetContextAsync();
            }
            catch
            {
                break;
            }

            _ = Task.Run(() => HandleRequest(ctx), ct);
        }
    }

    private void HandleRequest(HttpListenerContext ctx)
    {
        try
        {
            var req  = ctx.Request;
            var resp = ctx.Response;

            var clientIpGlobal = req.RemoteEndPoint?.Address?.ToString() ?? "?";

            if (req.HttpMethod == "POST" && req.Url?.AbsolutePath == "/join")
            {
                // Annonce immédiate du client à la connexion (pseudo dans le body)
                using var srJoin = new StreamReader(req.InputStream, Encoding.UTF8);
                var joinJson = srJoin.ReadToEnd();

                bool isBannedJoin  = Dispatcher.Invoke(() => _bannedPlayers.Contains(clientIpGlobal));
                bool isApprovedJoin = Dispatcher.Invoke(() => _connectedPlayers.ContainsKey(clientIpGlobal));
                bool isPendingJoin  = Dispatcher.Invoke(() => _pendingPlayers.ContainsKey(clientIpGlobal));

                if (isBannedJoin)
                {
                    resp.StatusCode = 403;
                }
                else if (!isApprovedJoin && !isPendingJoin)
                {
                    try
                    {
                        var joinState = JsonSerializer.Deserialize<AppState>(joinJson, JsonOpts);
                        var pseudo = string.IsNullOrWhiteSpace(joinState?.PlayerName) ? clientIpGlobal : joinState!.PlayerName;
                        Dispatcher.Invoke(() =>
                        {
                            _pendingPlayers[clientIpGlobal] = pseudo;
                            RefreshPlayersList();
                        });
                    }
                    catch { }
                    resp.StatusCode = 202;
                }
                else
                {
                    resp.StatusCode = isApprovedJoin ? 200 : 202;
                }
            }
            else if (req.HttpMethod == "GET" && req.Url?.AbsolutePath == "/state")
            {
                // Refuser les bannis et les non-approuvés
                bool isBanned  = Dispatcher.Invoke(() => _bannedPlayers.Contains(clientIpGlobal));
                bool isApproved = Dispatcher.Invoke(() => _connectedPlayers.ContainsKey(clientIpGlobal));

                if (isBanned)
                {
                    resp.StatusCode = 403;
                }
                else if (!isApproved)
                {
                    resp.StatusCode = 202;
                }
                else
                {
                    // Joueur approuvé : renvoie l'état
                    var json = Dispatcher.Invoke(BuildStateJson);
                    var bytes = Encoding.UTF8.GetBytes(json);
                    resp.ContentType     = "application/json";
                    resp.ContentLength64 = bytes.Length;
                    resp.OutputStream.Write(bytes);
                    resp.StatusCode = 200;
                }
            }
            else if (req.HttpMethod == "POST" && req.Url?.AbsolutePath == "/state")
            {
                // Lire le body dans tous les cas
                using var sr = new StreamReader(req.InputStream, Encoding.UTF8);
                var json = sr.ReadToEnd();

                bool isBanned   = Dispatcher.Invoke(() => _bannedPlayers.Contains(clientIpGlobal));
                bool isApproved = Dispatcher.Invoke(() => _connectedPlayers.ContainsKey(clientIpGlobal));
                bool isPending  = Dispatcher.Invoke(() => _pendingPlayers.ContainsKey(clientIpGlobal));

                if (isBanned)
                {
                    resp.StatusCode = 403;
                }
                else if (!isApproved)
                {
                    // Ajouter en liste d'attente si pas encore dedans
                    try
                    {
                        var clientState = JsonSerializer.Deserialize<AppState>(json, JsonOpts);
                        var pseudo = string.IsNullOrWhiteSpace(clientState?.PlayerName) ? clientIpGlobal : clientState!.PlayerName;
                        if (!isPending)
                        {
                            Dispatcher.Invoke(() =>
                            {
                                _pendingPlayers[clientIpGlobal] = pseudo;
                                RefreshPlayersList();
                            });
                        }
                    }
                    catch { }
                    resp.StatusCode = 202; // En attente
                }
                else
                {
                    // Joueur approuvé : appliquer son état
                    try
                    {
                        var clientState = JsonSerializer.Deserialize<AppState>(json, JsonOpts);
                        var pseudo = string.IsNullOrWhiteSpace(clientState?.PlayerName) ? clientIpGlobal : clientState!.PlayerName;
                        Dispatcher.Invoke(() =>
                        {
                            _connectedPlayers[clientIpGlobal] = pseudo;
                            RefreshPlayersList();
                            ApplyRemoteState(json);
                        });
                    }
                    catch
                    {
                        Dispatcher.Invoke(() => ApplyRemoteState(json));
                    }
                    resp.StatusCode = 200;
                }
            }
            else
            {
                resp.StatusCode = 404;
            }

            resp.Close();
        }
        catch { }
    }

    // ================================================================
    //  SE CONNECTER EN MODE CLIENT
    // ================================================================
    private void ConnectClientButton_Click(object sender, RoutedEventArgs e)
    {
        if (_netRole != NetRole.None)
            Disconnect();

        var ip   = ClientIpBox.Text.Trim();
        var port = ClientPortBox.Text.Trim();
        if (string.IsNullOrEmpty(ip)) return;

        _hostUrl = $"http://{ip}:{port}";
        _netRole = NetRole.Client;

        SetMultiStatus("🟠  Connexion à l'hôte...", "#F0A500");
        DisconnectButton.Visibility = Visibility.Visible;

        // Lance le polling (qui commence par un /join)
        _pollCts = new CancellationTokenSource();
        Task.Run(() => PollLoop(_pollCts.Token));
    }

    /// <summary>Boucle client : récupère l'état de l'hôte toutes les 500ms.</summary>
    private async Task PollLoop(CancellationToken ct)
    {
        // ── Annonce immédiate au serveur dès la connexion ──
        bool announced = false;
        while (!announced && !ct.IsCancellationRequested)
        {
            try
            {
                // BuildStateJson accède à des éléments UI → doit être sur le thread UI
                var joinJson    = Dispatcher.Invoke(BuildStateJson);
                var joinContent = new StringContent(joinJson, Encoding.UTF8, "application/json");
                var joinResp    = await _httpClient.PostAsync(_hostUrl + "/join", joinContent, ct);

                if (joinResp.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    Dispatcher.Invoke(() =>
                        SetMultiStatus("🔴  EXCLU — en attente de réautorisation de l'hôte...", "#FF6B6B"));
                    await Task.Delay(2000, ct).ContinueWith(_ => { });
                    continue;
                }
                announced = true;
                Dispatcher.Invoke(() =>
                    SetMultiStatus("🟠  En attente d'approbation de l'hôte...", "#F0A500"));
            }
            catch (OperationCanceledException) { return; }
            catch
            {
                Dispatcher.Invoke(() =>
                    SetMultiStatus($"🔴  Impossible de joindre l'hôte {_hostUrl} — vérifiez l'IP et le port", "#FF6B6B"));
                await Task.Delay(2000, ct).ContinueWith(_ => { });
            }
        }

        // ── Boucle de polling ──
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var response = await _httpClient.GetAsync(_hostUrl + "/state", ct);

                if (response.StatusCode == System.Net.HttpStatusCode.Forbidden) // 403 = exclu
                {
                    Dispatcher.Invoke(() =>
                        SetMultiStatus("🔴  EXCLU — en attente de réautorisation de l'hôte...", "#FF6B6B"));

                    // Retenter /join toutes les 2s pour détecter une réautorisation
                    await Task.Delay(2000, ct).ContinueWith(_ => { });
                    try
                    {
                        var rejoinContent = new StringContent(Dispatcher.Invoke(BuildStateJson), Encoding.UTF8, "application/json");
                        var rejoinResp    = await _httpClient.PostAsync(_hostUrl + "/join", rejoinContent, ct);
                        if (rejoinResp.StatusCode != System.Net.HttpStatusCode.Forbidden)
                            Dispatcher.Invoke(() =>
                                SetMultiStatus("🟠  En attente d'approbation de l'hôte...", "#F0A500"));
                    }
                    catch { }
                    continue;
                }
                else if (response.StatusCode == System.Net.HttpStatusCode.Accepted) // 202 = en attente
                {
                    Dispatcher.Invoke(() =>
                        SetMultiStatus("🟠  En attente d'approbation de l'hôte...", "#F0A500"));
                }
                else if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync(ct);
                    Dispatcher.Invoke(() =>
                    {
                        SetMultiStatus($"🔵  CLIENT — connecté à {_hostUrl}", "#4A90D9");
                        ApplyRemoteState(json);
                    });
                }
            }
            catch (OperationCanceledException) { break; }
            catch
            {
                Dispatcher.Invoke(() =>
                    SetMultiStatus($"🟡  CLIENT — tentative de reconnexion à {_hostUrl}...", "#F0A500"));
            }

            await Task.Delay(500, ct).ContinueWith(_ => { });
        }
    }

    // ================================================================
    //  DÉCONNECTER
    // ================================================================
    private void DisconnectButton_Click(object sender, RoutedEventArgs e) => Disconnect();

    private void Disconnect()
    {
        _pollCts?.Cancel();
        _pollCts = null;

        _listenerCts?.Cancel();
        _listenerCts = null;

        try { _httpListener?.Stop(); } catch { }
        _httpListener = null;

        _netRole = NetRole.None;
        _connectedPlayers.Clear();
        _pendingPlayers.Clear();
        _bannedPlayers.Clear();
        PlayersListBorder.Visibility = Visibility.Collapsed;
        PlayersListPanel.Children.Clear();
        SetMultiStatus("⚫  Hors ligne — choisissez un mode ci-dessous", "#6B8C7A");
        DisconnectButton.Visibility = Visibility.Collapsed;
    }

    // ================================================================
    //  ENVOI DE L'ÉTAT VERS L'HÔTE (depuis un client)
    // ================================================================
    private void PushStateToHost()
    {
        if (_netRole != NetRole.Client || string.IsNullOrEmpty(_hostUrl))
            return;

        var json = BuildStateJson();
        _ = Task.Run(async () =>
        {
            try
            {
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                await _httpClient.PostAsync(_hostUrl + "/state", content);
            }
            catch { }
        });
    }

    // ================================================================
    //  SERIALISATION / DÉSÉRIALISATION DE L'ÉTAT
    // ================================================================
    private string BuildStateJson()
    {
        var state = BuildState();
        return JsonSerializer.Serialize(state, JsonOpts);
    }

    private AppState BuildState() => new()
    {
        CurrentMapId     = _currentMap?.Id,
        DisabledIds      = [.._disabledIds],
        BoxSelected      = [.._boxSelected],
        BoxVisits        = [.._boxVisits],
        BoxCycleHistory  = [.._boxCycleHistory],
        BoxCycleCount    = _boxCycleCount,
        Selected         = [.._selected],
        ClickOrder       = [.._clickOrder],
        CycleHistory     = [.._cycleHistory],
        CycleCount       = _cycleCount,
        CurrentDogRound  = _currentDogRound,
        DogRoundHistory  = [.._dogRoundHistory],
        // BO2
        CurrentBo2MapId  = _currentBo2Map?.Id,
        Bo2DisabledIds   = [.._bo2DisabledIds],
        Bo2Selected      = [.._bo2Selected],
        Bo2ClickOrder    = [.._bo2ClickOrder],
        Bo2CycleHistory  = [.._bo2CycleHistory],
        Bo2CycleCount    = _bo2CycleCount,
        // Multi
        PlayerName       = PlayerNameBox.Text.Trim(),
        LastBoxLocation  = _lastVisitedLocation,
    };

    /// <summary>Applique un état reçu du réseau sans déclencher une nouvelle sync.</summary>
    private void ApplyRemoteState(string json)
    {
        if (_isSyncingFromRemote) return;
        _isSyncingFromRemote = true;
        try
        {
            var state = JsonSerializer.Deserialize<AppState>(json, JsonOpts);
            if (state == null) return;

            // --- disabled ---
            _disabledIds.Clear();
            foreach (var id in state.DisabledIds)
            {
                _disabledIds.Add(id);
                if (_toggleButtons.TryGetValue(id, out var tb)) tb.IsChecked = false;
            }
            // Réactiver les autres
            foreach (var (id, tb) in _toggleButtons)
                if (!_disabledIds.Contains(id)) tb.IsChecked = true;

            // --- dog rounds ---
            _currentDogRound = state.CurrentDogRound;
            _dogRoundHistory  = state.DogRoundHistory;
            UpdateDogRoundDisplay();
            RenderDogHistory();

            // --- map ---
            var map = state.CurrentMapId != null
                ? Maps.FirstOrDefault(m => m.Id == state.CurrentMapId)
                : null;

            if (map != null && map.Id != _currentMap?.Id)
            {
                _currentMap = map;
                foreach (var (id, btn) in _mapButtons)
                    btn.IsChecked = (id == map.Id);
                MapTitleText.Text = map.Name.ToUpper();

                bool hasBox = map.BoxLocations.Count > 0;
                BoxSeparator.Visibility        = hasBox ? Visibility.Visible : Visibility.Collapsed;
                BoxLabelPanel.Visibility        = hasBox ? Visibility.Visible : Visibility.Collapsed;
                BoxLocationsPanel.Visibility    = hasBox ? Visibility.Visible : Visibility.Collapsed;
                BoxResetButtonsPanel.Visibility = hasBox ? Visibility.Visible : Visibility.Collapsed;
                BoxHistoryBox.Visibility        = hasBox ? Visibility.Visible : Visibility.Collapsed;
                BonusSeparator.Visibility       = Visibility.Visible;
                BonusLabelPanel.Visibility      = Visibility.Visible;
                DropsPanel.Visibility           = Visibility.Visible;
                ResetButtonsPanel.Visibility    = Visibility.Visible;
                HistoryBox.Visibility           = Visibility.Visible;
            }

            if (_currentMap != null)
            {
                // Bonus
                _cycleCount   = state.CycleCount;
                _cycleHistory = state.CycleHistory;
                _currentDrops = _currentMap.Drops.Where(d => !_disabledIds.Contains(d.Id)).ToList();
                _selected     = [..state.Selected];
                _clickOrder   = state.ClickOrder;
                RenderDrops();
                RenderHistory();

                // Boîte
                _boxCycleCount   = state.BoxCycleCount;
                _boxCycleHistory = state.BoxCycleHistory;
                _boxSelected     = [..state.BoxSelected];
                _boxVisits       = state.BoxVisits;
                _lastVisitedLocation = state.LastBoxLocation;
                if (_currentMap.BoxLocations.Count > 0)
                {
                    RenderBoxLocations();
                    RenderBoxHistory();
                }
            }

            // ─── BO2 ───────────────────────────────────────────────

            // Toggles BO2 (Carpenter / Fire Sale)
            _bo2DisabledIds.Clear();
            foreach (var id in state.Bo2DisabledIds)
            {
                if (id == "zombie_blood") continue; // jamais désactivable
                _bo2DisabledIds.Add(id);
                if (_bo2ToggleButtons.TryGetValue(id, out var tb2)) tb2.IsChecked = false;
            }
            foreach (var (id, tb2) in _bo2ToggleButtons)
                if (!_bo2DisabledIds.Contains(id)) tb2.IsChecked = true;

            // Map BO2
            var bo2Map = state.CurrentBo2MapId != null
                ? Bo2Maps.FirstOrDefault(m => m.Id == state.CurrentBo2MapId)
                : null;

            if (bo2Map != null && bo2Map.Id != _currentBo2Map?.Id)
            {
                _currentBo2Map = bo2Map;
                foreach (var (id, btn) in _bo2MapButtons)
                    btn.IsChecked = (id == bo2Map.Id);
                Bo2MapTitleText.Text = bo2Map.Name.ToUpper();

                Bo2BonusSeparator.Visibility    = Visibility.Visible;
                Bo2BonusLabelPanel.Visibility   = Visibility.Visible;
                Bo2DropsPanel.Visibility        = Visibility.Visible;
                Bo2ResetButtonsPanel.Visibility = Visibility.Visible;
                Bo2HistoryBox.Visibility        = Visibility.Visible;
            }

            if (_currentBo2Map != null)
            {
                _bo2CycleCount   = state.Bo2CycleCount;
                _bo2CycleHistory = state.Bo2CycleHistory;
                _currentBo2Drops = _currentBo2Map.Drops
                    .Where(d => !_bo2DisabledIds.Contains(d.Id))
                    .ToList();
                _bo2Selected   = [..state.Bo2Selected];
                _bo2ClickOrder = state.Bo2ClickOrder;
                RenderBo2Drops();
                RenderBo2History();
            }
        }
        catch { }
        finally
        {
            _isSyncingFromRemote = false;
        }
    }



    // ================================================================
    //  COLLAPSE / EXPAND — CHOIX DE LA MAP
    // ================================================================
    private bool _mapExpanded = true;

    private void MapHeader_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _mapExpanded = !_mapExpanded;
        MapButtonsPanel.Visibility   = _mapExpanded ? Visibility.Visible  : Visibility.Collapsed;
        MapChevron.Text              = _mapExpanded ? "▼" : "▶";
        MapSelectedInline.Visibility = _mapExpanded || _currentMap == null
            ? Visibility.Collapsed : Visibility.Visible;
    }

    // ================================================================
    //  MÉTHODE UTILITAIRE — liste des joueurs connectés (hôte)
    // ================================================================
    private void RefreshPlayersList()
    {
        PlayersListPanel.Children.Clear();

        // ── Hôte ──
        var hostName = PlayerNameBox.Text.Trim();
        var hostRow  = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        hostRow.Children.Add(new TextBlock
        {
            Text       = $"👑  {(string.IsNullOrEmpty(hostName) ? "Hôte" : hostName)} (toi)",
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize   = 12,
            Foreground = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#1D9E75")),
            VerticalAlignment = VerticalAlignment.Center,
        });
        PlayersListPanel.Children.Add(hostRow);

        // ── Joueurs en attente ──
        foreach (var (ip, pseudo) in _pendingPlayers)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 5) };

            row.Children.Add(new TextBlock
            {
                Text       = $"⏳  {pseudo}  ({ip})",
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize   = 12,
                Foreground = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#F0A500")),
                VerticalAlignment = VerticalAlignment.Center,
                Margin     = new Thickness(0, 0, 10, 0),
                MinWidth   = 260,
            });

            var acceptBtn = MakePlayerButton("✔ ACCEPTER", "#1A4A30", "#1D9E75", "#A8D5BF");
            var rejectBtn = MakePlayerButton("✖ REFUSER",  "#3D1010", "#C0392B", "#FF6B6B");

            var capturedIp     = ip;
            var capturedPseudo = pseudo;
            acceptBtn.Click += (_, _) =>
            {
                _pendingPlayers.Remove(capturedIp);
                _connectedPlayers[capturedIp] = capturedPseudo;
                RefreshPlayersList();
            };
            rejectBtn.Click += (_, _) =>
            {
                _pendingPlayers.Remove(capturedIp);
                _bannedPlayers.Add(capturedIp);
                RefreshPlayersList();
            };

            row.Children.Add(acceptBtn);
            row.Children.Add(rejectBtn);
            PlayersListPanel.Children.Add(row);
        }

        // ── Joueurs approuvés ──
        foreach (var (ip, pseudo) in _connectedPlayers)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 5) };

            row.Children.Add(new TextBlock
            {
                Text       = $"🎮  {pseudo}  ({ip})",
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize   = 12,
                Foreground = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#A8D5BF")),
                VerticalAlignment = VerticalAlignment.Center,
                Margin     = new Thickness(0, 0, 10, 0),
                MinWidth   = 260,
            });

            var kickBtn = MakePlayerButton("⛔ EXCLURE", "#3D1010", "#C0392B", "#FF6B6B");
            var capturedIp = ip;
            kickBtn.Click += (_, _) =>
            {
                _connectedPlayers.Remove(capturedIp);
                _bannedPlayers.Add(capturedIp);
                RefreshPlayersList();
            };

            row.Children.Add(kickBtn);
            PlayersListPanel.Children.Add(row);
        }

        // ── Joueurs bannis (réautorisables) ──
        foreach (var ip in _bannedPlayers.ToList())
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 5) };

            row.Children.Add(new TextBlock
            {
                Text       = $"🚫  {ip}  (exclu)",
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize   = 12,
                Foreground = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FF6B6B")),
                VerticalAlignment = VerticalAlignment.Center,
                Margin     = new Thickness(0, 0, 10, 0),
                MinWidth   = 260,
            });

            var unbanBtn = MakePlayerButton("↩ RÉAUTORISER", "#1A2A40", "#4A90D9", "#90C8FF");
            var capturedIp = ip;
            unbanBtn.Click += (_, _) =>
            {
                _bannedPlayers.Remove(capturedIp);
                RefreshPlayersList();
            };

            row.Children.Add(unbanBtn);
            PlayersListPanel.Children.Add(row);
        }

        // ── Aucun joueur ──
        if (_connectedPlayers.Count == 0 && _pendingPlayers.Count == 0 && _bannedPlayers.Count == 0)
        {
            PlayersListPanel.Children.Add(new TextBlock
            {
                Text       = "   Aucun joueur connecté...",
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize   = 11,
                Foreground = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#4A6A58")),
            });
        }
    }

    private static Button MakePlayerButton(string label, string bg, string border, string fg)
        => new()
        {
            Content         = label,
            FontFamily      = new System.Windows.Media.FontFamily("Consolas"),
            FontSize        = 10,
            FontWeight      = FontWeights.Bold,
            Padding         = new Thickness(8, 4, 8, 4),
            Margin          = new Thickness(0, 0, 6, 0),
            Background      = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(bg)),
            BorderBrush     = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(border)),
            Foreground      = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(fg)),
            BorderThickness = new Thickness(1),
            Cursor          = Cursors.Hand,
        };

    // ================================================================
    //  MÉTHODE UTILITAIRE — mettre à jour le statut multi
    // ================================================================
    private void SetMultiStatus(string text, string hexColor)
    {
        MultiStatusText.Text       = text;
        MultiStatusText.Foreground = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(hexColor));
    }

    /// <summary>Met à jour le badge de map sélectionnée dans l'en-tête collapsed.</summary>
    private void UpdateMapInlineBadge()
    {
        if (_currentMap != null)
        {
            MapSelectedInlineText.Text = _currentMap.Name.ToUpper();
            // Affiche seulement si le panneau map est replié
            MapSelectedInline.Visibility = _mapExpanded ? Visibility.Collapsed : Visibility.Visible;
        }
        else
        {
            MapSelectedInline.Visibility = Visibility.Collapsed;
        }
    }

    // ================================================================
    //  SAUVEGARDE / CHARGEMENT LOCAL
    // ================================================================
    private void SaveState()
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(SavePath)!);
            File.WriteAllText(SavePath, JsonSerializer.Serialize(BuildState(), JsonOpts));
        }
        catch { }

        // Si on est client, on envoie aussi l'état à l'hôte
        if (!_isSyncingFromRemote)
            PushStateToHost();
    }

    private void LoadState()
    {
        try
        {
            if (!File.Exists(SavePath)) return;
            var state = JsonSerializer.Deserialize<AppState>(File.ReadAllText(SavePath), JsonOpts);
            if (state == null) return;

            foreach (var id in state.DisabledIds)
            {
                _disabledIds.Add(id);
                if (_toggleButtons.TryGetValue(id, out var tb)) tb.IsChecked = false;
            }

            _currentDogRound = state.CurrentDogRound;
            _dogRoundHistory  = state.DogRoundHistory;
            UpdateDogRoundDisplay();
            RenderDogHistory();

            if (state.CurrentMapId != null)
            {
                var map = Maps.FirstOrDefault(m => m.Id == state.CurrentMapId);
                if (map != null)
                {
                    _currentMap = map;
                    _lastVisitedLocation = state.LastBoxLocation;
                    if (_mapButtons.TryGetValue(map.Id, out var mb)) mb.IsChecked = true;
                    MapTitleText.Text = map.Name.ToUpper();

                    _cycleCount   = state.CycleCount;
                    _cycleHistory = state.CycleHistory;
                    _currentDrops = map.Drops.Where(d => !_disabledIds.Contains(d.Id)).ToList();
                    foreach (var id in state.Selected) _selected.Add(id);
                    _clickOrder = state.ClickOrder;

                    bool hasBox = map.BoxLocations.Count > 0;
                    BoxSeparator.Visibility        = hasBox ? Visibility.Visible : Visibility.Collapsed;
                    BoxLabelPanel.Visibility        = hasBox ? Visibility.Visible : Visibility.Collapsed;
                    BoxLocationsPanel.Visibility    = hasBox ? Visibility.Visible : Visibility.Collapsed;
                    BoxResetButtonsPanel.Visibility = hasBox ? Visibility.Visible : Visibility.Collapsed;
                    BoxHistoryBox.Visibility        = hasBox ? Visibility.Visible : Visibility.Collapsed;
                    BonusSeparator.Visibility       = Visibility.Visible;
                    BonusLabelPanel.Visibility      = Visibility.Visible;
                    DropsPanel.Visibility           = Visibility.Visible;
                    ResetButtonsPanel.Visibility    = Visibility.Visible;
                    HistoryBox.Visibility           = Visibility.Visible;

                    _boxCycleCount   = state.BoxCycleCount;
                    _boxCycleHistory = state.BoxCycleHistory;
                    foreach (var loc in state.BoxSelected) _boxSelected.Add(loc);
                    _boxVisits = state.BoxVisits;

                    if (hasBox) { RenderBoxLocations(); RenderBoxHistory(); }
                    RenderDrops();
                    RenderHistory();
                }
            }
        }
        catch { }

        // ---- BO2 restore ----
        try
        {
            if (!File.Exists(SavePath)) return;
            var state2 = JsonSerializer.Deserialize<AppState>(File.ReadAllText(SavePath), JsonOpts);
            if (state2 == null) return;

            foreach (var id in state2.Bo2DisabledIds)
            {
                // zombie_blood is never toggleable — ignore it even if old save has it
                if (id == "zombie_blood") continue;
                _bo2DisabledIds.Add(id);
                if (_bo2ToggleButtons.TryGetValue(id, out var tb)) tb.IsChecked = false;
            }

            if (state2.CurrentBo2MapId != null)
            {
                var map = Bo2Maps.FirstOrDefault(m => m.Id == state2.CurrentBo2MapId);
                if (map != null)
                {
                    _currentBo2Map = map;
                    if (_bo2MapButtons.TryGetValue(map.Id, out var mb)) mb.IsChecked = true;
                    Bo2MapTitleText.Text = map.Name.ToUpper();

                    _bo2CycleCount   = state2.Bo2CycleCount;
                    _bo2CycleHistory = state2.Bo2CycleHistory;
                    _currentBo2Drops = map.Drops.Where(d => !_bo2DisabledIds.Contains(d.Id)).ToList();
                    foreach (var id in state2.Bo2Selected) _bo2Selected.Add(id);
                    _bo2ClickOrder = state2.Bo2ClickOrder;

                    Bo2BonusSeparator.Visibility    = Visibility.Visible;
                    Bo2BonusLabelPanel.Visibility   = Visibility.Visible;
                    Bo2DropsPanel.Visibility        = Visibility.Visible;
                    Bo2ResetButtonsPanel.Visibility = Visibility.Visible;
                    Bo2HistoryBox.Visibility        = Visibility.Visible;

                    RenderBo2Drops();
                    RenderBo2History();
                }
            }
        }
        catch { }
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        Disconnect();
        SaveState();
    }

    // ----------------------------------------------------------------
    //  CONSTRUCTEUR
    // ----------------------------------------------------------------
    public MainWindow()
    {
        InitializeComponent();
        BuildMapButtons();
        BuildBo2MapButtons();
        BuildToggleButtons();
        BuildBo2ToggleButtons();
        LoadState();
        // Zombie Blood is exclusive to Origins and never disableable — purge any stale save data
        _bo2DisabledIds.Remove("zombie_blood");

        LoadTwitchConfig();
        InitTwitchUiFromConfig();
    }

    private void InitTwitchUiFromConfig()
    {
        // Le Client ID est intégré au code (constante TwitchClientId) : on l'impose
        // toujours, y compris pour migrer d'anciennes configs qui en stockaient un autre.
        _twitchConfig.TwitchClientId = TwitchClientId;
        TwitchCommandTemplateBox.Text = _twitchConfig.TwitchCommandTemplate;
        TwitchAutoSendCheck.IsChecked = _twitchConfig.TwitchAutoSend;
        TwitchListenCommandBox.Text = _twitchConfig.TwitchListenCommand;
        TwitchListenEnabledCheck.IsChecked = _twitchConfig.TwitchListenEnabled;

        if (!string.IsNullOrWhiteSpace(_twitchConfig.TwitchRefreshToken))
        {
            SetTwitchStatus(
                string.IsNullOrWhiteSpace(_twitchConfig.TwitchLogin)
                    ? "Connecté (vérification en cours…)"
                    : $"Connecté en tant que {_twitchConfig.TwitchLogin}",
                isError: false);

            // Reconnexion silencieuse en tâche de fond : récupère un access token frais
            // puis démarre l'écoute du chat sans action de l'utilisateur.
            _ = Task.Run(async () =>
            {
                var token = await EnsureTwitchAccessTokenAsync();
                if (token != null) Dispatcher.Invoke(StartTwitchChatListening);
            });
        }
        else
        {
            SetTwitchStatus("Non connecté.", isError: false);
        }
    }

    // ================================================================
    //  ONGLETS BO1 / BO2
    // ================================================================
    // Active tab: "bo1", "bo2", "multi"
    private string _activeTab = "bo1";

    private void SetActiveTab(string tab)
    {
        _activeTab = tab;

        // Reset all tabs
        foreach (var (btn, name) in new[] {
            (Bo1TabButton,   "bo1"),
            (Bo2TabButton,   "bo2"),
            (MultiTabButton, "multi") })
        {
            bool active = name == tab;
            btn.IsChecked  = active;
            btn.Background = active ? Brush("BrushAccentDark") : Brush("BrushBgCard");
            btn.Foreground = active ? Brush("BrushAccentLight") : Brush("BrushTextSecondary");
            btn.BorderBrush = active ? Brush("BrushAccent") : Brush("BrushBorder");
        }

        Bo1Panel.Visibility   = tab == "bo1"   ? Visibility.Visible : Visibility.Collapsed;
        Bo2Panel.Visibility   = tab == "bo2"   ? Visibility.Visible : Visibility.Collapsed;
        MultiPanel.Visibility = tab == "multi" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Bo1TabButton_Click(object sender, RoutedEventArgs e)   => SetActiveTab("bo1");
    private void Bo2TabButton_Click(object sender, RoutedEventArgs e)   => SetActiveTab("bo2");
    private void MultiTabButton_Click(object sender, RoutedEventArgs e) => SetActiveTab("multi");

    // ================================================================
    //  BOUTONS DE MAPS
    // ================================================================
    private void BuildMapButtons()
    {
        foreach (var map in Maps)
        {
            var btn = new ToggleButton
            {
                Content = map.Name,
                Style   = (Style)Application.Current.Resources["MapButtonStyle"],
                Margin  = new Thickness(0, 0, 8, 8),
                Tag     = map,
            };
            btn.Click += MapButton_Click;
            MapButtonsPanel.Children.Add(btn);
            _mapButtons[map.Id] = btn;
        }
    }

    private void MapButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton clickedBtn) return;
        var map = (MapData)clickedBtn.Tag;
        _currentMap = map;

        foreach (var (id, btn) in _mapButtons)
            btn.IsChecked = (id == map.Id);

        ResetCycleState();
        ResetBoxState();

        MapTitleText.Text = map.Name.ToUpper();

        bool hasBox = map.BoxLocations.Count > 0;
        BoxSeparator.Visibility        = hasBox ? Visibility.Visible : Visibility.Collapsed;
        BoxLabelPanel.Visibility        = hasBox ? Visibility.Visible : Visibility.Collapsed;
        BoxLocationsPanel.Visibility    = hasBox ? Visibility.Visible : Visibility.Collapsed;
        BoxResetButtonsPanel.Visibility = hasBox ? Visibility.Visible : Visibility.Collapsed;
        BoxHistoryBox.Visibility        = hasBox ? Visibility.Visible : Visibility.Collapsed;
        BonusSeparator.Visibility       = Visibility.Visible;
        BonusLabelPanel.Visibility      = Visibility.Visible;
        DropsPanel.Visibility           = Visibility.Visible;
        ResetButtonsPanel.Visibility    = Visibility.Visible;
        HistoryBox.Visibility           = Visibility.Visible;

        if (hasBox) { RenderBoxLocations(); RenderBoxHistory(); }
        RenderDrops();
        RenderHistory();
        UpdateMapInlineBadge();
        SaveState();
    }

    // ================================================================
    //  BOUTONS ACTIVER / DÉSACTIVER BONUS
    // ================================================================
    private void BuildToggleButtons()
    {
        var items = new (DropItem drop, string shortLabel)[]
        {
            (ItemCarpenter, "Carpenter"),
            (ItemFireSale,  "Fire Sale"),
            (ItemDeath,     "Death Machine"),
        };

        foreach (var (drop, label) in items)
        {
            var btn = new ToggleButton
            {
                Content   = label,
                IsChecked = true,
                Style     = (Style)Application.Current.Resources["ToggleDropButtonStyle"],
                Margin    = new Thickness(0, 0, 8, 0),
                Tag       = drop.Id,
            };
            btn.Click += DropToggleButton_Click;
            ToggleButtonsPanel.Children.Add(btn);
            _toggleButtons[drop.Id] = btn;
        }
    }

    private void DropToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton btn) return;
        var id = (string)btn.Tag;
        if (btn.IsChecked == true) _disabledIds.Remove(id);
        else                       _disabledIds.Add(id);

        if (_currentMap != null) { RefreshAvailableDrops(); RenderDrops(); RenderHistory(); }
        SaveState();
    }

    // ================================================================
    //  RESET ÉTAT
    // ================================================================

    // Met à jour la liste des drops actifs SANS effacer le cycle en cours.
    // Utilisée quand on active/désactive un bonus via les toggles.
    private void RefreshAvailableDrops()
    {
        _currentDrops = _currentMap!.Drops
            .Where(d => !_disabledIds.Contains(d.Id))
            .ToList();

        // Si un bonus désactivé était coché dans le cycle, on le retire proprement
        _selected.RemoveWhere(sid => !_currentDrops.Any(d => d.Id == sid));
        _clickOrder.RemoveAll(c => !_currentDrops.Any(d => d.Id == c.Drop.Id));
        for (int i = 0; i < _clickOrder.Count; i++)
            _clickOrder[i] = _clickOrder[i] with { Order = i + 1 };
    }

    private void ResetCycleState()
    {
        _selected   = [];
        _clickOrder = [];
        _currentDrops = _currentMap!.Drops
            .Where(d => !_disabledIds.Contains(d.Id))
            .ToList();
    }

    private void ResetBoxState()
    {
        _boxSelected = [];
        _boxVisits   = [];
    }

    // ================================================================
    //  MANCHES SPÉCIALES
    // ================================================================
    private void DogRoundInput_PreviewTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e)
        => e.Handled = !e.Text.All(char.IsDigit);

    private void SetDogRoundButton_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(DogRoundInput.Text, out int round) || round <= 0)
        {
            MessageBox.Show("Veuillez entrer un numéro de manche valide (nombre entier positif).",
                            "Manche invalide", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _currentDogRound = round;
        if (!_dogRoundHistory.Contains(round)) _dogRoundHistory.Insert(0, round);
        DogRoundInput.Clear();
        UpdateDogRoundDisplay();
        RenderDogHistory();
        SaveState();
        SendTwitchRoundCommand();
    }

    private void ResetDogRoundsButton_Click(object sender, RoutedEventArgs e)
    {
        _currentDogRound = null;
        _dogRoundHistory  = [];
        DogRoundInput.Clear();
        UpdateDogRoundDisplay();
        RenderDogHistory();
        SaveState();
    }

    private void DogNext4Badge_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_currentDogRound is int current)
        {
            int next = current + 4;
            _currentDogRound = next;
            if (!_dogRoundHistory.Contains(next)) _dogRoundHistory.Insert(0, next);
            UpdateDogRoundDisplay();
            RenderDogHistory();
            SaveState();
            SendTwitchRoundCommand();
        }
    }

    private void DogNext5Badge_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_currentDogRound is int current)
        {
            int next = current + 5;
            _currentDogRound = next;
            if (!_dogRoundHistory.Contains(next)) _dogRoundHistory.Insert(0, next);
            UpdateDogRoundDisplay();
            RenderDogHistory();
            SaveState();
            SendTwitchRoundCommand();
        }
    }

    private void UpdateDogRoundDisplay()
    {
        if (_currentDogRound is int current)
        {
            DogNextRoundPanel.Visibility = Visibility.Visible;
            DogNext4Text.Text = (current + 4).ToString();
            DogNext5Text.Text = (current + 5).ToString();
        }
        else
        {
            DogNextRoundPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void RenderDogHistory()
    {
        DogHistoryPanel.Children.Clear();
        if (_dogRoundHistory.Count == 0) { DogHistoryBox.Visibility = Visibility.Collapsed; return; }
        DogHistoryBox.Visibility = Visibility.Visible;

        foreach (int round in _dogRoundHistory)
        {
            bool isCurrent = (_currentDogRound == round);
            var badge = new Border
            {
                Background      = isCurrent ? Brush("BrushAccentDark") : Brush("BrushBgCard"),
                BorderBrush     = isCurrent ? Brush("BrushAccent")     : Brush("BrushBorder"),
                BorderThickness = new Thickness(isCurrent ? 2 : 1),
                CornerRadius    = new CornerRadius(6),
                Padding         = new Thickness(14, 6, 14, 6),
                Margin          = new Thickness(0, 0, 8, 8),
                Cursor          = System.Windows.Input.Cursors.Hand,
            };
            var stack = new StackPanel { Orientation = Orientation.Horizontal };
            if (isCurrent)
                stack.Children.Add(new TextBlock { Text = "● ", FontFamily = new FontFamily("Consolas"), FontSize = 11, Foreground = Brush("BrushAccent"), VerticalAlignment = VerticalAlignment.Center });
            stack.Children.Add(new TextBlock { Text = $"{round}", FontFamily = new FontFamily("Consolas"), FontSize = 13, FontWeight = isCurrent ? FontWeights.Bold : FontWeights.Normal, Foreground = isCurrent ? Brush("BrushAccentLight") : Brush("BrushTextSecondary"), VerticalAlignment = VerticalAlignment.Center });
            badge.Child = stack;
            badge.MouseLeftButtonUp += (_, _) => { _currentDogRound = round; UpdateDogRoundDisplay(); RenderDogHistory(); SaveState(); };
            DogHistoryPanel.Children.Add(badge);
        }
    }

    // ================================================================
    //  AFFICHAGE GRILLE DES BONUS
    // ================================================================
    private void RenderDrops()
    {
        DropsPanel.Children.Clear();
        foreach (var drop in _currentDrops)
            DropsPanel.Children.Add(BuildDropCard(drop, _selected.Contains(drop.Id)));
    }

    private Border BuildDropCard(DropItem drop, bool isSelected)
    {
        int? order = null;
        var click = _clickOrder.FirstOrDefault(c => c.Drop.Id == drop.Id);
        if (click != null) order = click.Order;

        var img = new Image { Width = 52, Height = 52, Stretch = Stretch.Uniform, Source = LoadImage(drop.ImagePath) };

        var orderBadge = new Border
        {
            Background   = Brush("BrushAccent"), CornerRadius = new CornerRadius(10),
            Padding      = new Thickness(6, 1, 6, 1), Margin = new Thickness(0, 0, 0, 4),
            Visibility   = order.HasValue ? Visibility.Visible : Visibility.Collapsed,
            Child        = new TextBlock { Text = order.HasValue ? $"{order}" : "", FontFamily = new FontFamily("Consolas"), FontSize = 15, FontWeight = FontWeights.Bold, Foreground = Brushes.White },
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var label = new TextBlock { Text = drop.Label.ToUpper(), FontFamily = new FontFamily("Consolas"), FontSize = 11, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Foreground = isSelected ? Brush("BrushAccentLight") : Brush("BrushTextSecondary"), MaxWidth = 90 };

        var stack = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(orderBadge);
        stack.Children.Add(img);
        stack.Children.Add(new Border { Height = 6 });
        stack.Children.Add(label);

        var card = new Border
        {
            Child           = stack, MinWidth = 96, Padding = new Thickness(14, 12, 14, 12),
            CornerRadius    = new CornerRadius(10),
            BorderThickness = isSelected ? new Thickness(2) : new Thickness(1),
            Background      = isSelected ? Brush("BrushAccentDark") : Brush("BrushBgCard"),
            BorderBrush     = isSelected ? Brush("BrushAccent")     : Brush("BrushBorder"),
            Cursor          = System.Windows.Input.Cursors.Hand, Margin = new Thickness(0, 0, 12, 12),
        };

        if (isSelected)
            card.Effect = new System.Windows.Media.Effects.DropShadowEffect
                { Color = (Color)ColorConverter.ConvertFromString("#1D9E75"), BlurRadius = 14, ShadowDepth = 0, Opacity = 0.55 };

        card.MouseEnter        += (_, _) => { if (!_selected.Contains(drop.Id)) card.BorderBrush = Brush("BrushBorderHover"); };
        card.MouseLeave        += (_, _) => { if (!_selected.Contains(drop.Id)) card.BorderBrush = Brush("BrushBorder"); };
        card.MouseLeftButtonUp += (_, _) => ToggleDrop(drop.Id);
        return card;
    }

    // ================================================================
    //  TOGGLE D'UN BONUS
    // ================================================================
    private void ToggleDrop(string id)
    {
        var drop = _currentDrops.FirstOrDefault(d => d.Id == id);
        if (drop == null) return;

        if (_selected.Contains(id))
        {
            _selected.Remove(id);
            _clickOrder.RemoveAll(c => c.Drop.Id == id);
            for (int i = 0; i < _clickOrder.Count; i++)
                _clickOrder[i] = _clickOrder[i] with { Order = i + 1 };
        }
        else
        {
            _selected.Add(id);
            _clickOrder.Add(new ClickedDrop(_clickOrder.Count + 1, drop));
            if (_selected.Count == _currentDrops.Count) { FinishCycle(); return; }
        }
        RenderDrops();
        SaveState();
    }

    private void FinishCycle()
    {
        _cycleCount++;
        _cycleHistory.Insert(0, new CycleRecord(_cycleCount, [.._clickOrder]));
        _selected = []; _clickOrder = [];
        var anim = new DoubleAnimation(0.15, 1.0, TimeSpan.FromMilliseconds(400)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        DropsPanel.BeginAnimation(OpacityProperty, anim);
        RenderDrops(); RenderHistory(); SaveState();
    }

    // ================================================================
    //  HISTORIQUE BONUS
    // ================================================================
    private void RenderHistory()
    {
        HistoryPanel.Children.Clear();
        if (_cycleHistory.Count == 0) { HistoryPanel.Children.Add(new TextBlock { Text = "Aucun cycle terminé", FontSize = 13, FontStyle = FontStyles.Italic, Foreground = Brush("BrushTextMuted") }); return; }
        foreach (var cycle in _cycleHistory.Take(5))
        {
            HistoryPanel.Children.Add(new TextBlock { Text = $"CYCLE {cycle.Num}", FontFamily = new FontFamily("Consolas"), FontSize = 13, FontWeight = FontWeights.Bold, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 4) });
            var badgesPanel = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            foreach (var click in cycle.Clicks.OrderBy(c => c.Order)) badgesPanel.Children.Add(BuildHistoryBadge(click));
            HistoryPanel.Children.Add(badgesPanel);
            HistoryPanel.Children.Add(new Rectangle { Height = 1, Fill = Brush("BrushSeparator"), Margin = new Thickness(0, 4, 0, 8), HorizontalAlignment = HorizontalAlignment.Stretch, Width = 9999 });
        }
    }

    private Border BuildHistoryBadge(ClickedDrop click)
    {
        var stack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new TextBlock { Text = $"{click.Order}", FontFamily = new FontFamily("Consolas"), FontSize = 15, FontWeight = FontWeights.Bold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        stack.Children.Add(new Image { Width = 18, Height = 18, Stretch = Stretch.Uniform, Source = LoadImage(click.Drop.ImagePath), Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
        stack.Children.Add(new TextBlock { Text = click.Drop.Label, FontFamily = new FontFamily("Consolas"), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Brush("BrushTextSecondary"), VerticalAlignment = VerticalAlignment.Center });
        return new Border { Child = stack, Background = Brush("BrushBgCard"), BorderBrush = Brush("BrushBorder"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 6, 6) };
    }

    private void ResetCycleButton_Click(object sender, RoutedEventArgs e)
        { _selected = []; _clickOrder = []; RenderDrops(); SaveState(); }

    private void ResetHistoryButton_Click(object sender, RoutedEventArgs e)
        { _cycleHistory = []; _cycleCount = 0; RenderHistory(); SaveState(); }

    // ================================================================
    //  AFFICHAGE EMPLACEMENTS BOÎTE
    // ================================================================
    private void RenderBoxLocations()
    {
        BoxLocationsPanel.Children.Clear();
        if (_currentMap == null) return;
        foreach (var loc in _currentMap.BoxLocations)
            BoxLocationsPanel.Children.Add(BuildBoxCard(loc));
    }

    private Border BuildBoxCard(string location)
    {
        bool isSelected = _boxSelected.Contains(location);
        int? order      = null;
        var  visit      = _boxVisits.FirstOrDefault(v => v.Location == location);
        if (visit != null) order = visit.Order;

        // Jaune si : dernière visitée dans le cycle actif OU mémo inter-cycles (pas encore cliquée = pas de numéro)
        int  maxOrder      = _boxVisits.Count > 0 ? _boxVisits.Max(v => v.Order) : 0;
        bool isLastActive  = isSelected && order.HasValue && order.Value == maxOrder && maxOrder > 0;
        bool isLastMemo    = !isSelected && location == _lastVisitedLocation;
        bool isLastVisited = isLastActive || isLastMemo;

        // Badge numéro (caché si mémo inter-cycles = pas de numéro à afficher)
        var badgeBg    = isLastVisited ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#B8860B")) : Brush("BrushAccent");
        var orderBadge = new Border { Background = badgeBg, CornerRadius = new CornerRadius(10), Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(0, 0, 0, 4), Visibility = order.HasValue ? Visibility.Visible : Visibility.Collapsed, Child = new TextBlock { Text = order.HasValue ? $"{order}" : "", FontFamily = new FontFamily("Consolas"), FontSize = 15, FontWeight = FontWeights.Bold, Foreground = Brushes.White }, HorizontalAlignment = HorizontalAlignment.Center };
        var boxIcon    = new Image { Width = 52, Height = 52, Stretch = Stretch.Uniform, Source = LoadLocationImage(location), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 6) };

        var labelFg = isLastVisited
            ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFE066"))
            : (isSelected ? Brush("BrushAccentLight") : Brush("BrushTextSecondary"));
        var label = new TextBlock { Text = location.ToUpper(), FontFamily = new FontFamily("Consolas"), FontSize = 11, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Foreground = labelFg, MaxWidth = 90 };

        var stack = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(orderBadge); stack.Children.Add(boxIcon); stack.Children.Add(new Border { Height = 6 }); stack.Children.Add(label);

        var cardBg     = isLastVisited ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3D3000"))
                       : (isSelected   ? Brush("BrushAccentDark") : Brush("BrushBgCard"));
        var cardBorder = isLastVisited ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFD700"))
                       : (isSelected   ? Brush("BrushAccent")     : Brush("BrushBorder"));

        var card = new Border { Child = stack, MinWidth = 96, Padding = new Thickness(14, 12, 14, 12), CornerRadius = new CornerRadius(10), BorderThickness = isLastVisited || isSelected ? new Thickness(2) : new Thickness(1), Background = cardBg, BorderBrush = cardBorder, Cursor = System.Windows.Input.Cursors.Hand, Margin = new Thickness(0, 0, 12, 12) };

        if (isLastVisited)
            card.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = (Color)ColorConverter.ConvertFromString("#FFD700"), BlurRadius = 16, ShadowDepth = 0, Opacity = 0.6 };
        else if (isSelected)
            card.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = (Color)ColorConverter.ConvertFromString("#1D9E75"), BlurRadius = 14, ShadowDepth = 0, Opacity = 0.55 };

        card.MouseEnter        += (_, _) => { if (!_boxSelected.Contains(location)) card.BorderBrush = isLastMemo ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFD700")) : Brush("BrushBorderHover"); };
        card.MouseLeave        += (_, _) => { if (!_boxSelected.Contains(location)) card.BorderBrush = isLastMemo ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFD700")) : Brush("BrushBorder"); };
        card.MouseLeftButtonUp += (_, _) => ToggleBox(location);
        card.ToolTip = new ToolTip { Content = "Clic gauche : visiter cet emplacement" };
        return card;
    }

    private void ToggleBox(string location)
    {
        if (_currentMap == null) return;
        if (_boxSelected.Contains(location))
        {
            _boxSelected.Remove(location);
            _boxVisits.RemoveAll(v => v.Location == location);
            for (int i = 0; i < _boxVisits.Count; i++)
                _boxVisits[i] = _boxVisits[i] with { Order = i + 1 };
        }
        else
        {
            _boxSelected.Add(location);
            _boxVisits.Add(new VisitedBox(_boxVisits.Count + 1, location));
            if (_boxSelected.Count == _currentMap.BoxLocations.Count) { FinishBoxCycle(); return; }
        }
        RenderBoxLocations(); RenderBoxHistory(); SaveState();
    }

    private void FinishBoxCycle()
    {
        _boxCycleCount++;
        _boxCycleHistory.Insert(0, new BoxCycleRecord(_boxCycleCount, [.._boxVisits]));
        // Mémoriser la dernière boîte visitée pour la mettre en jaune au cycle suivant
        _lastVisitedLocation = _boxVisits.Count > 0 ? _boxVisits[^1].Location : null;
        _boxSelected = []; _boxVisits = [];
        var anim = new DoubleAnimation(0.15, 1.0, TimeSpan.FromMilliseconds(400)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        BoxLocationsPanel.BeginAnimation(OpacityProperty, anim);
        RenderBoxLocations(); RenderBoxHistory(); SaveState();
    }

    private void RenderBoxHistory()
    {
        BoxHistoryPanel.Children.Clear();
        if (_boxCycleHistory.Count == 0) { BoxHistoryPanel.Children.Add(new TextBlock { Text = "Aucun cycle terminé", FontSize = 13, FontStyle = FontStyles.Italic, Foreground = Brush("BrushTextMuted") }); return; }
        foreach (var cycle in _boxCycleHistory.Take(5))
        {
            BoxHistoryPanel.Children.Add(new TextBlock { Text = $"CYCLE {cycle.Num}", FontFamily = new FontFamily("Consolas"), FontSize = 13, FontWeight = FontWeights.Bold, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 4) });
            var badgesPanel = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            foreach (var visit in cycle.Visits) badgesPanel.Children.Add(BuildBoxHistoryBadge(visit));
            BoxHistoryPanel.Children.Add(badgesPanel);
            BoxHistoryPanel.Children.Add(new Rectangle { Height = 1, Fill = Brush("BrushSeparator"), Margin = new Thickness(0, 4, 0, 8), HorizontalAlignment = HorizontalAlignment.Stretch, Width = 9999 });
        }
    }

    private Border BuildBoxHistoryBadge(VisitedBox visit)
    {
        var stack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new TextBlock { Text = $"#{visit.Order}", FontFamily = new FontFamily("Consolas"), FontSize = 10, Foreground = Brush("BrushAccentLight"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
        stack.Children.Add(new Image { Width = 18, Height = 18, Stretch = Stretch.Uniform, Source = LoadLocationImage(visit.Location), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) });
        stack.Children.Add(new TextBlock { Text = visit.Location, FontFamily = new FontFamily("Consolas"), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Brush("BrushTextSecondary"), VerticalAlignment = VerticalAlignment.Center });
        return new Border { Child = stack, Background = Brush("BrushBgCard"), BorderBrush = Brush("BrushBorder"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 6, 6) };
    }

    private void ResetBoxCycleButton_Click(object sender, RoutedEventArgs e)
        { _lastVisitedLocation = null; _boxSelected = []; _boxVisits = []; RenderBoxLocations(); RenderBoxHistory(); SaveState(); }

    private void ResetBoxHistoryButton_Click(object sender, RoutedEventArgs e)
        { _boxCycleHistory = []; _boxCycleCount = 0; _boxSelected = []; _boxVisits = []; RenderBoxLocations(); RenderBoxHistory(); SaveState(); }

    // ── Overlay OBS ────────────────────────────────────────────────────
    private OverlayWindow? _overlay;

    private void OpenOverlay(OverlayMode mode)
    {
        if (_overlay == null || !_overlay.IsLoaded)
        {
            _overlay = new OverlayWindow(mode);
            _overlay.Show();
        }
        else
        {
            _overlay.Activate();
        }
    }

    private void OverlayButton_Click     (object sender, RoutedEventArgs e) => OpenOverlay(OverlayMode.Box);
    private void OverlayBonusButton_Click(object sender, RoutedEventArgs e) => OpenOverlay(OverlayMode.Bo1Bonus);
    private void OverlayBo2Button_Click  (object sender, RoutedEventArgs e) => OpenOverlay(OverlayMode.Bo2Bonus);

    // ================================================================
    //  CHARGEMENT D'IMAGE
    // ================================================================
    private static BitmapImage LoadImage(string relativePath)
    {
        var uri = new Uri($"pack://application:,,,/{relativePath}", UriKind.Absolute);
        var bmp = new BitmapImage();
        bmp.BeginInit(); bmp.UriSource = uri; bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.EndInit();
        return bmp;
    }

    private static BitmapImage LoadLocationImage(string location)
    {
        try { return LoadImage($"Images/locations/{location}.png"); }
        catch { return LoadImage("Images/mystery_box.png"); }
    }

    // ================================================================
    //  RESET GLOBAL
    // ================================================================
    private void ResetAllButton_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show("Effacer toutes les données ?\n(manches spéciales, cycles boîte, cycles bonus BO1 + cycles bonus BO2)",
            "Tout effacer", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        // ── BO1 ──
        _currentDogRound = null; _dogRoundHistory = [];
        DogRoundInput.Clear(); UpdateDogRoundDisplay(); RenderDogHistory();

        _selected = []; _clickOrder = []; _cycleHistory = []; _cycleCount = 0;
        if (_currentMap != null) { _currentDrops = _currentMap.Drops.Where(d => !_disabledIds.Contains(d.Id)).ToList(); RenderDrops(); RenderHistory(); }

        _boxSelected = []; _boxVisits = []; _boxCycleHistory = []; _boxCycleCount = 0; _lastVisitedLocation = null;
        if (_currentMap != null && _currentMap.BoxLocations.Count > 0) { RenderBoxLocations(); RenderBoxHistory(); }

        // ── BO2 ──
        _bo2Selected = []; _bo2ClickOrder = []; _bo2CycleHistory = []; _bo2CycleCount = 0;
        if (_currentBo2Map != null) { _currentBo2Drops = _currentBo2Map.Drops.Where(d => !_bo2DisabledIds.Contains(d.Id)).ToList(); RenderBo2Drops(); RenderBo2History(); }

        SaveState();
    }

    // ================================================================
    //  EXPORT NOTEPAD
    // ================================================================
    private void ExportTxtButton_Click(object sender, RoutedEventArgs e)
    {
        var sb  = new StringBuilder();
        var now = DateTime.Now.ToString("dd/MM/yyyy HH:mm");

        sb.AppendLine("╔══════════════════════════════════════════════════╗");
        sb.AppendLine("║           BLACK OPS TRACKER — EXPORT             ║");
        sb.AppendLine($"║  {now,-48}║");
        sb.AppendLine("╚══════════════════════════════════════════════════╝");
        sb.AppendLine();

        if (_activeTab == "bo2")
        {
            // ── Export BO2 ──
            sb.AppendLine($"JEU  : BLACK OPS 2");
            sb.AppendLine($"MAP  : {(_currentBo2Map?.Name ?? "Aucune")}");
            sb.AppendLine();
            sb.AppendLine("══════════════════════════════════");
            sb.AppendLine("  CYCLES DE BONUS");
            sb.AppendLine("══════════════════════════════════");
            if (_bo2CycleHistory.Count == 0) sb.AppendLine("  Aucun cycle terminé.");
            else foreach (var cycle in _bo2CycleHistory) { sb.AppendLine($"  Cycle #{cycle.Num} :"); foreach (var click in cycle.Clicks.OrderBy(c => c.Order)) sb.AppendLine($"    {click.Order}. {click.Drop.Label}"); }
            if (_bo2ClickOrder.Count > 0) { sb.AppendLine("  Cycle en cours :"); foreach (var c in _bo2ClickOrder) sb.AppendLine($"    {c.Order}. {c.Drop.Label}"); }
            sb.AppendLine();
        }
        else
        {
            // ── Export BO1 ──
            sb.AppendLine($"JEU  : BLACK OPS 1");
            sb.AppendLine($"MAP  : {(_currentMap?.Name ?? "Aucune")}");
            sb.AppendLine();
            sb.AppendLine("══════════════════════════════════");
            sb.AppendLine("  MANCHES SPÉCIALES");
            sb.AppendLine("══════════════════════════════════");
            if (_dogRoundHistory.Count == 0)
            {
                sb.AppendLine("  Aucune manche enregistrée.");
            }
            else
            {
                var sorted = _dogRoundHistory.OrderBy(r => r).ToList();
                sb.AppendLine("  Manches : " + string.Join(", ", sorted));
                sb.AppendLine();

                // Calcul des intervalles entre chaque manche spéciale
                if (sorted.Count >= 2)
                {
                    var intervals = new List<int>();
                    for (int i = 1; i < sorted.Count; i++)
                        intervals.Add(sorted[i] - sorted[i - 1]);
                    // Ajoute aussi le premier intervalle (depuis manche 0)
                    // sorted[0] correspond à la manche 0 → premier écart
                    var allIntervals = new List<int> { sorted[0] };
                    allIntervals.AddRange(intervals);

                    int count4 = allIntervals.Count(x => x == 4);
                    int count5 = allIntervals.Count(x => x == 5);

                    sb.AppendLine("  ── Intervalles ──");
                    sb.AppendLine("  " + string.Join(", ", allIntervals.Select(i => $"+{i}")));
                    sb.AppendLine();
                    if (count4 > 0) sb.AppendLine($"  4 Rounder : {count4} fois");
                    if (count5 > 0) sb.AppendLine($"  5 Rounder : {count5} fois");
                    var autres = allIntervals.Where(x => x != 4 && x != 5).GroupBy(x => x);
                    foreach (var g in autres.OrderBy(g => g.Key))
                        sb.AppendLine($"  {g.Key} Rounder : {g.Count()} fois");
                }
                else
                {
                    sb.AppendLine($"  Premier écart : +{sorted[0]}");
                }
            }
            sb.AppendLine();
            sb.AppendLine("══════════════════════════════════");
            sb.AppendLine("  CYCLES D'EMPLACEMENT DE BOÎTE");
            sb.AppendLine("══════════════════════════════════");
            if (_boxCycleHistory.Count == 0) sb.AppendLine("  Aucun cycle terminé.");
            else foreach (var cycle in _boxCycleHistory) { sb.AppendLine($"  Cycle #{cycle.Num} :"); foreach (var visit in cycle.Visits) sb.AppendLine($"    {visit.Order}. {visit.Location}"); }
            if (_boxVisits.Count > 0) { sb.AppendLine("  Cycle en cours :"); foreach (var v in _boxVisits) sb.AppendLine($"    {v.Order}. {v.Location}"); }
            sb.AppendLine();
            sb.AppendLine("══════════════════════════════════");
            sb.AppendLine("  CYCLES DE BONUS");
            sb.AppendLine("══════════════════════════════════");
            if (_cycleHistory.Count == 0) sb.AppendLine("  Aucun cycle terminé.");
            else foreach (var cycle in _cycleHistory) { sb.AppendLine($"  Cycle #{cycle.Num} :"); foreach (var click in cycle.Clicks.OrderBy(c => c.Order)) sb.AppendLine($"    {click.Order}. {click.Drop.Label}"); }
            if (_clickOrder.Count > 0) { sb.AppendLine("  Cycle en cours :"); foreach (var c in _clickOrder) sb.AppendLine($"    {c.Order}. {c.Drop.Label}"); }
            sb.AppendLine();
        }

        var dlg = new Microsoft.Win32.SaveFileDialog { Title = "Enregistrer le rapport", FileName = $"BlackOpsTracker_{DateTime.Now:yyyyMMdd_HHmm}.txt", DefaultExt = ".txt", Filter = "Fichier texte (*.txt)|*.txt" };
        if (dlg.ShowDialog() == true)
        {
            File.WriteAllText(dlg.FileName, sb.ToString(), Encoding.UTF8);
            MessageBox.Show($"Rapport enregistré :\n{dlg.FileName}", "Enregistrement réussi", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
    // ================================================================
    //  BOUTONS DE MAPS BO2
    // ================================================================
    private void BuildBo2MapButtons()
    {
        foreach (var map in Bo2Maps)
        {
            var btn = new ToggleButton
            {
                Content = map.Name,
                Style   = (Style)Application.Current.Resources["MapButtonStyle"],
                Margin  = new Thickness(0, 0, 8, 8),
                Tag     = map,
            };
            btn.Click += Bo2MapButton_Click;
            Bo2MapButtonsPanel.Children.Add(btn);
            _bo2MapButtons[map.Id] = btn;
        }
    }

    private void Bo2MapButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton clickedBtn) return;
        var map = (MapData)clickedBtn.Tag;
        _currentBo2Map = map;

        foreach (var (id, btn) in _bo2MapButtons)
            btn.IsChecked = (id == map.Id);

        ResetBo2CycleState();

        Bo2MapTitleText.Text = map.Name.ToUpper();
        Bo2BonusSeparator.Visibility  = Visibility.Visible;
        Bo2BonusLabelPanel.Visibility = Visibility.Visible;
        Bo2DropsPanel.Visibility      = Visibility.Visible;
        Bo2ResetButtonsPanel.Visibility = Visibility.Visible;
        Bo2HistoryBox.Visibility      = Visibility.Visible;

        RenderBo2Drops();
        RenderBo2History();
        SaveState();
    }

    private void ResetBo2CycleState()
    {
        _bo2Selected   = [];
        _bo2ClickOrder = [];
        _currentBo2Drops = _currentBo2Map!.Drops
            .Where(d => !_bo2DisabledIds.Contains(d.Id))
            .ToList();
    }

    // ================================================================
    //  BOUTONS ACTIVER / DÉSACTIVER BONUS BO2
    // ================================================================
    private void BuildBo2ToggleButtons()
    {
        var items = new (DropItem drop, string shortLabel)[]
        {
            (ItemCarpenter, "Carpenter"),
            (ItemFireSale,  "Fire Sale"),
        };

        foreach (var (drop, label) in items)
        {
            var btn = new ToggleButton
            {
                Content   = label,
                IsChecked = true,
                Style     = (Style)Application.Current.Resources["ToggleDropButtonStyle"],
                Margin    = new Thickness(0, 0, 8, 0),
                Tag       = drop.Id,
            };
            btn.Click += Bo2DropToggleButton_Click;
            Bo2ToggleButtonsPanel.Children.Add(btn);
            _bo2ToggleButtons[drop.Id] = btn;
        }
    }

    private void Bo2DropToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton btn) return;
        var id = (string)btn.Tag;
        if (id == "zombie_blood") return; // never toggleable
        if (btn.IsChecked == true) _bo2DisabledIds.Remove(id);
        else                       _bo2DisabledIds.Add(id);

        if (_currentBo2Map != null) { RefreshBo2AvailableDrops(); RenderBo2Drops(); RenderBo2History(); }
        SaveState();
    }

    // Met à jour la liste des drops BO2 actifs SANS effacer le cycle en cours.
    private void RefreshBo2AvailableDrops()
    {
        _currentBo2Drops = _currentBo2Map!.Drops
            .Where(d => !_bo2DisabledIds.Contains(d.Id))
            .ToList();

        // Si un bonus désactivé était coché dans le cycle, on le retire proprement
        _bo2Selected.RemoveWhere(sid => !_currentBo2Drops.Any(d => d.Id == sid));
        _bo2ClickOrder.RemoveAll(c => !_currentBo2Drops.Any(d => d.Id == c.Drop.Id));
        for (int i = 0; i < _bo2ClickOrder.Count; i++)
            _bo2ClickOrder[i] = _bo2ClickOrder[i] with { Order = i + 1 };
    }

    private void RenderBo2Drops()
    {
        Bo2DropsPanel.Children.Clear();
        if (_currentBo2Map == null) return;
        foreach (var drop in _currentBo2Drops)
            Bo2DropsPanel.Children.Add(BuildBo2DropCard(drop, _bo2Selected.Contains(drop.Id)));
    }

    private Border BuildBo2DropCard(DropItem drop, bool isSelected)
    {
        int? order = null;
        var click = _bo2ClickOrder.FirstOrDefault(c => c.Drop.Id == drop.Id);
        if (click != null) order = click.Order;

        var img = new Image { Width = 52, Height = 52, Stretch = Stretch.Uniform, Source = LoadImage(drop.ImagePath) };

        var orderBadge = new Border
        {
            Background   = Brush("BrushAccent"), CornerRadius = new CornerRadius(10),
            Padding      = new Thickness(6, 1, 6, 1), Margin = new Thickness(0, 0, 0, 4),
            Visibility   = order.HasValue ? Visibility.Visible : Visibility.Collapsed,
            Child        = new TextBlock { Text = order.HasValue ? $"{order}" : "", FontFamily = new FontFamily("Consolas"), FontSize = 15, FontWeight = FontWeights.Bold, Foreground = Brushes.White },
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var label = new TextBlock { Text = drop.Label.ToUpper(), FontFamily = new FontFamily("Consolas"), FontSize = 11, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Foreground = isSelected ? Brush("BrushAccentLight") : Brush("BrushTextSecondary"), MaxWidth = 90 };

        var stack = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(orderBadge);
        stack.Children.Add(img);
        stack.Children.Add(new Border { Height = 6 });
        stack.Children.Add(label);

        var card = new Border
        {
            Child           = stack, MinWidth = 96, Padding = new Thickness(14, 12, 14, 12),
            CornerRadius    = new CornerRadius(10),
            BorderThickness = isSelected ? new Thickness(2) : new Thickness(1),
            Background      = isSelected ? Brush("BrushAccentDark") : Brush("BrushBgCard"),
            BorderBrush     = isSelected ? Brush("BrushAccent")     : Brush("BrushBorder"),
            Cursor          = System.Windows.Input.Cursors.Hand, Margin = new Thickness(0, 0, 12, 12),
        };

        if (isSelected)
            card.Effect = new System.Windows.Media.Effects.DropShadowEffect
                { Color = (Color)ColorConverter.ConvertFromString("#1D9E75"), BlurRadius = 14, ShadowDepth = 0, Opacity = 0.55 };

        card.MouseEnter        += (_, _) => { if (!_bo2Selected.Contains(drop.Id)) card.BorderBrush = Brush("BrushBorderHover"); };
        card.MouseLeave        += (_, _) => { if (!_bo2Selected.Contains(drop.Id)) card.BorderBrush = Brush("BrushBorder"); };
        card.MouseLeftButtonUp += (_, _) => ToggleBo2Drop(drop.Id);
        return card;
    }

    private void ToggleBo2Drop(string id)
    {
        var drop = _currentBo2Drops.FirstOrDefault(d => d.Id == id);
        if (drop == null) return;

        if (_bo2Selected.Contains(id))
        {
            _bo2Selected.Remove(id);
            _bo2ClickOrder.RemoveAll(c => c.Drop.Id == id);
            for (int i = 0; i < _bo2ClickOrder.Count; i++)
                _bo2ClickOrder[i] = _bo2ClickOrder[i] with { Order = i + 1 };
        }
        else
        {
            _bo2Selected.Add(id);
            _bo2ClickOrder.Add(new ClickedDrop(_bo2ClickOrder.Count + 1, drop));
            if (_bo2Selected.Count == _currentBo2Drops.Count) { FinishBo2Cycle(); return; }
        }
        RenderBo2Drops();
        SaveState();
    }

    private void FinishBo2Cycle()
    {
        _bo2CycleCount++;
        _bo2CycleHistory.Insert(0, new CycleRecord(_bo2CycleCount, [.._bo2ClickOrder]));
        _bo2Selected = []; _bo2ClickOrder = [];
        var anim = new DoubleAnimation(0.15, 1.0, TimeSpan.FromMilliseconds(400)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Bo2DropsPanel.BeginAnimation(OpacityProperty, anim);
        RenderBo2Drops(); RenderBo2History(); SaveState();
    }

    private void RenderBo2History()
    {
        Bo2HistoryPanel.Children.Clear();
        if (_bo2CycleHistory.Count == 0)
        {
            Bo2HistoryPanel.Children.Add(new TextBlock { Text = "Aucun cycle terminé", FontSize = 13, FontStyle = FontStyles.Italic, Foreground = Brush("BrushTextMuted") });
            return;
        }
        foreach (var cycle in _bo2CycleHistory.Take(5))
        {
            Bo2HistoryPanel.Children.Add(new TextBlock { Text = $"CYCLE {cycle.Num}", FontFamily = new FontFamily("Consolas"), FontSize = 13, FontWeight = FontWeights.Bold, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 4) });
            var badgesPanel = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            foreach (var click in cycle.Clicks.OrderBy(c => c.Order)) badgesPanel.Children.Add(BuildHistoryBadge(click));
            Bo2HistoryPanel.Children.Add(badgesPanel);
            Bo2HistoryPanel.Children.Add(new Rectangle { Height = 1, Fill = Brush("BrushSeparator"), Margin = new Thickness(0, 4, 0, 8), HorizontalAlignment = HorizontalAlignment.Stretch, Width = 9999 });
        }
    }

    private void Bo2ResetCycleButton_Click(object sender, RoutedEventArgs e)
        { _bo2Selected = []; _bo2ClickOrder = []; RenderBo2Drops(); SaveState(); }

    private void Bo2ResetHistoryButton_Click(object sender, RoutedEventArgs e)
        { _bo2CycleHistory = []; _bo2CycleCount = 0; RenderBo2History(); SaveState(); }


}
