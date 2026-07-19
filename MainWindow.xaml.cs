using Microsoft.Win32;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace AIFlashcardMaker;

public sealed class Flashcard
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Front { get; set; } = "";
    public string Back { get; set; } = "";
    public string Tags { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime DueAt { get; set; } = DateTime.UtcNow;
    public DateTime LastStudiedAt { get; set; } = DateTime.MinValue;

    public int Repetitions { get; set; }
    public int AgainCount { get; set; }
    public int HardCount { get; set; }
    public int GoodCount { get; set; }
    public int EasyCount { get; set; }

    public int ReviewCount => AgainCount + HardCount + GoodCount + EasyCount;

    // UI-only display helpers (single-line previews for list templates).
    public string FrontLine => Front.Replace("\r", " ").Replace("\n", " ").Trim();
    public string BackLine => Back.Replace("\r", " ").Replace("\n", " ").Trim();

    public override string ToString()
    {
        string preview = Front.Replace("\r", " ").Replace("\n", " ");
        return preview.Length > 95 ? preview[..95] + "..." : preview;
    }
}

public sealed class StudyDeck
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Default Deck";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastStudiedAt { get; set; } = DateTime.MinValue;
    public List<Flashcard> Cards { get; set; } = new();

    // UI-only display helpers for the deck library list template.
    public int DueCount => Cards.Count(c => c.DueAt <= DateTime.UtcNow);
    public string CardsLabel => $"{Cards.Count} cards • {DueCount} due";
    public string LastStudiedLabel => LastStudiedAt == DateTime.MinValue
        ? "Not studied yet"
        : $"Last studied {LastStudiedAt.ToLocalTime():MMM d, HH:mm}";

    public override string ToString()
    {
        int due = Cards.Count(c => c.DueAt <= DateTime.UtcNow);
        return $"{Name}   ({Cards.Count} cards, {due} due)";
    }
}

public sealed class StudyStats
{
    public DateTime LastStudyDate { get; set; } = DateTime.MinValue;
    public int CurrentStreak { get; set; }
    public int StudiedToday { get; set; }
    public int TotalReviews { get; set; }
    public int SuccessfulReviews { get; set; }
}

public sealed class DeckStore
{
    public string ActiveDeckId { get; set; } = "";
    public List<StudyDeck> Decks { get; set; } = new();
    public StudyStats Stats { get; set; } = new();
}

public sealed class LocalAccount
{
    public string Email { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Plan { get; set; } = "";
    public DateTime SubscriptionExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class UsedActivationCode
{
    public string Code { get; set; } = "";
    public string UsedByEmail { get; set; } = "";
    public string Plan { get; set; } = "";
    public DateTime UsedAt { get; set; }
}

public sealed class LocalStore
{
    public List<LocalAccount> Accounts { get; set; } = new();
    public List<UsedActivationCode> UsedCodes { get; set; } = new();
}

// Legacy local settings container. Phase 8: the provider fields below are always
// purged on load (see PurgeLegacyProviderCredentials) and are never used to make a
// request — all AI now goes through the OrbitLab backend. Kept only so old config
// files still deserialize; no provider key, model, or URL is defaulted in source.
public sealed class AppSettings
{
    public string ApiProvider { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "";
    public string BaseUrl { get; set; } = "";
}

public sealed partial class MainWindow : Window
{
    private readonly string dataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AIFlashcardMaker");

    private string StorePath => Path.Combine(dataDir, "accounts.json");
    private string SettingsPath => Path.Combine(dataDir, "settings.json");
    private string DecksPath => Path.Combine(dataDir, "decks.json");
    private string ResearchPath => Path.Combine(dataDir, "research_projects.json");
    // Phase 4: additive local autosave/backup files (existing paths above are unchanged).
    private string ResearchAutosavePath => Path.Combine(dataDir, "research_projects.autosave.json");
    private string ResearchLastGoodPath => Path.Combine(dataDir, "research_projects.lastgood.json");

    // Research Lab (Phase 1) — kept separate from deck/card storage.
    private const int ResearchProjectLimit = 2;

    private LocalStore _store = new();
    private AppSettings _settings = new();
    private DeckStore _deckStore = new();
    private ResearchLabData _researchData = new();
    private string _openResearchId = "";
    private string _pendingDeleteResearchId = "";

    // Research Lab (Phase 2B) — in-app Research AI. Provider-neutral: the app
    // talks only to a configurable backend endpoint and NEVER stores provider
    // API keys. A development mock (behind a settings toggle) lets the flow run
    // before a real backend exists.
    private ResearchAiOptions _researchAiOptions = new();
    private IResearchAiService _researchAi = null!;   // built in the constructor

    // Phase 8: the single AI transport for the whole app. Sends the current license
    // session token as the bearer; the backend injects the provider key + model.
    private readonly OrbitLabAiProxyClient _aiProxy;
    private string ResearchAiConfigPath => Path.Combine(dataDir, "research_ai_config.json");

    // Last developer-safe diagnostic line from a failed Research AI call (action,
    // timeout, elapsed, provider mode, status, empty/parse flags). Shown behind an
    // "Open details" affordance. Never contains keys or request content.
    private string? _lastAiDiagnostics;

    // Research Lab (Phase 2E/F) — import + generic confirm state.
    // Guards for pasted/uploaded proposal text length (protects the model call
    // and keeps the UI responsive). The proposal text is never logged or printed.
    private const int ImportProposalMaxChars = 24000;
    private const int ImportProposalMinChars = 40;
    private const long ImportProposalMaxFileBytes = 5 * 1024 * 1024;   // 5 MB
    // Upper bound on the COMPACT text actually sent to Research AI for proposal
    // extraction. Kept well under the 24k paste limit: once References/DOIs are
    // stripped, real proposals fit comfortably, and this cap protects the model
    // call from timing out on unusually long documents. The user's full original
    // text is never truncated by this — only the AI-input copy.
    private const int AiProposalInputMaxChars = 16000;
    private ProposalExtractionResult? _currentExtraction;
    // Full, cleaned original proposal text (references INCLUDED) — stored for the
    // user and shown/kept as ImportedProposalText. Never the AI input.
    private string _lastImportText = "";
    // Compact, reference-free copy actually sent to Research AI, reused on retry
    // so a timeout retry never re-sends the huge raw document.
    private string _lastAiProposalInput = "";
    // Safe, key-free summary (char counts + refs-removed flag) of the last import
    // compaction, surfaced behind "Open details".
    private string _lastProposalCompactionSummary = "";
    // Text read from a chosen file, kept separate from the paste box so that a
    // non-empty paste always takes precedence at analyze time.
    private string _importedFileText = "";
    private string _importedFileError = "";
    private Action? _rlConfirmAction;

    // Research Lab (Phase 3) — Data Extraction Sheet state.
    // The grid binds to this live collection; on save it is copied back into the
    // open project's Variables list.
    private readonly System.Collections.ObjectModel.ObservableCollection<ResearchVariable> _extractionVariables = new();
    // Editable working copy of AI suggestions shown in the review overlay.
    private readonly System.Collections.ObjectModel.ObservableCollection<ResearchVariable> _reviewVariables = new();
    // Live list behind the Resolve Conflicts window; ignored keys are session-only.
    private readonly System.Collections.ObjectModel.ObservableCollection<ExtractionConflict> _conflicts = new();
    private readonly HashSet<string> _ignoredConflictKeys = new();
    // Captured when the conflict window opens so Cancel can discard all staged
    // decisions and any immediate edits made during the session.
    private List<ResearchVariable>? _conflictSnapshot;
    private HashSet<string>? _conflictIgnoredSnapshot;
    // True while an optional "Fix with Research AI" runs from inside the Resolve
    // Conflicts modal, so a timeout/failure keeps the modal open (with a retry/
    // manual banner) instead of dropping the student out of the workflow.
    private bool _fixFromConflicts;

    // Session-only undo/redo for the extraction sheet (snapshots of the variable
    // list). Deliberately not persisted — restarting the app clears history.
    private readonly Stack<List<ResearchVariable>> _dxUndo = new();
    private readonly Stack<List<ResearchVariable>> _dxRedo = new();
    // Snapshot taken the moment a grid cell enters edit mode (DxGrid_BeginningEdit),
    // so a direct in-table edit (not just Add/Edit-modal/Delete/etc.) is undoable
    // too. Only pushed onto _dxUndo if the value actually changed on commit.
    private List<ResearchVariable>? _dxPendingCellSnapshot;

    // Pre-run delay for Data Extraction AI actions (prevents accidental API usage).
    private Action? _aiDelayAction;
    private DispatcherTimer? _aiDelayTimer;

    // Phase 4: local autosave (debounced) + crash recovery (local-first; no backend/cloud/network).
    private DispatcherTimer? _autosaveTimer;
    private bool _autosaveRecoveryChecked;
    private enum SaveState { Idle, Saving, Saved, Failed }
    private int _aiDelaySecondsLeft;

    // ---- Long-running Research AI work tracking (elapsed/stage/cancel/background) ----
    // One heavy AI action at a time. The CancellationTokenSource is real — Cancel
    // actually cancels the in-flight HTTP request (see ResearchLabServices.cs),
    // unlike the pre-run delay above which only guards against accidental clicks.
    private CancellationTokenSource? _aiWorkCts;
    private DispatcherTimer? _aiWorkTimer;
    private DateTime _aiWorkStartedAt;

    // Generic AI-failure overlay state: Retry re-invokes whatever action failed.
    private Action? _aiFailureRetryAction;
    private ExtractionSheetResult? _pendingSheet;   // AI suggestions awaiting review/apply
    // Structured AI conflict-fix proposals awaiting review (accept/edit/delete).
    private readonly System.Collections.ObjectModel.ObservableCollection<ConflictFixProposal> _fixProposals = new();
    private ResearchVariable? _editingVariable;      // row open in the single-variable modal
    private const int CsvMaxSampleRows = 50;
    private const long CsvMaxFileBytes = 10 * 1024 * 1024;   // 10 MB

    private LocalAccount? _currentUser;
    private string _activeDeckId = "";
    private string _currentCardId = "";

    private readonly List<Flashcard> _studyQueue = new();
    private int _studyIndex = -1;
    private int _studySessionTotal;
    private bool _answerShown;
    private bool _suppressSelection;

    private readonly Dictionary<UIElement, Button> _navMap = new();

    // Research dashboard sub-tabs: segmented button -> content panel.
    private readonly List<(Button Button, UIElement Panel)> _researchTabs = new();

    public MainWindow()
    {
        InitializeComponent();

        // Phase 8: single AI transport, authenticated with the live license session.
        _aiProxy = new OrbitLabAiProxyClient(() => _license?.Token);

        // Phase 3: load the real OrbitLab brand PNGs if present (safe neutral fallback otherwise).
        LoadBrandAssets();

        // Phase 4: debounced local autosave timer for Research Lab data.
        _autosaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        _autosaveTimer.Tick += AutosaveTick;

        // Never open larger than the usable screen area (keeps the window on
        // small laptops and clear of the taskbar).
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);

        BuildNavMap();
        BuildResearchTabMap();
        InitializeGifAnimations();

        // Escape closes the topmost Data Extraction dialog without applying changes.
        PreviewKeyDown += DataExtraction_PreviewKeyDown;

        Directory.CreateDirectory(dataDir);

        LoadStore();
        LoadSettings();
        LoadDecks();
        LoadResearch();
        LoadPendingFinalizations();   // Phase 9: resume any unfinished first-run recordings
        LoadResearchAiConfig();
        // Phase 8 hardening: ALL AI features (Research Lab + flashcards) go through
        // the single OrbitLab backend proxy, authenticated with the current license
        // session token. No provider key ever lives in the desktop client.
        _researchAi = new ResearchAiService(() => _researchAiOptions, _aiProxy);
        EnsureDefaultDeck();
        SetupCombos();

        ShowAuth();
        RefreshAll();

        // Phase 6A: version label + async bootstrap/update check once the window is
        // up. Fail-open by design — without a configured backend, or with the backend
        // unreachable, the app behaves exactly as before (5s network budget, no locks).
        try
        {
            UpdateVersionText.Text = $"{Branding.ProductName} v{AppConfig.CurrentVersionLabel} · {AppConfig.ReleaseChannel} channel";
        }
        catch { /* label is cosmetic */ }
        Loaded += async (_, _) =>
        {
            // Phase 7: silent sign-in from the encrypted license cache first, then
            // the bootstrap/update check. Both are fail-open and never block startup.
            await TryRestoreSessionAsync();
            await StartupUpdateAndBootstrapAsync();
        };
    }

    private void SetupCombos()
    {
        ModeCombo.ItemsSource = new[] { "Step 1 High-Yield", "Basic Q/A", "Cloze Deletion", "Image/OCR", "English + Arabic Explanation" };
        DifficultyCombo.ItemsSource = new[] { "Easy", "Medium", "Hard", "Exam Style" };
        AnswerLengthCombo.ItemsSource = new[] { "Very Short", "Normal", "Detailed" };
        CountCombo.ItemsSource = new[] { "Auto", "5", "10", "20", "30", "40" };
        LanguageCombo.ItemsSource = new[] { "English", "Arabic", "English with Arabic explanation" };

        ModeCombo.SelectedIndex = 0;
        DifficultyCombo.SelectedIndex = 3;
        AnswerLengthCombo.SelectedIndex = 0;
        CountCombo.SelectedIndex = 0;
        LanguageCombo.SelectedIndex = 0;
    }

    private void ShowAuth()
    {
        HideLoading();

        AuthGrid.Visibility = Visibility.Visible;
        AppGrid.Visibility = Visibility.Collapsed;

        FadeIn(AuthGrid, 220);
    }

    // Phase 3: load the real OrbitLab brand PNGs (Assets/Brand/*) if they are present and
    // included as WPF Resources. If not, the neutral fallback UI stays visible. This never
    // throws and never breaks the build — the PNGs simply appear once placed + resourced.
    private void LoadBrandAssets()
    {
        var logo = TryLoadBrandBitmap("pack://application:,,,/Assets/Brand/orbitlab-logo.png");
        if (logo is not null)
        {
            BrandLogoImage.Source = logo;
            BrandLogoImage.Visibility = Visibility.Visible;
            BrandLogoFallback.Visibility = Visibility.Collapsed;

            AuthCardLogoImage.Source = logo;
            AuthCardLogoImage.Visibility = Visibility.Visible;

            SignupLogoImage.Source = logo;
            SignupLogoImage.Visibility = Visibility.Visible;
        }

        var hero = TryLoadBrandBitmap("pack://application:,,,/Assets/Brand/orbitlab-login-hero.png");
        if (hero is not null)
        {
            LoginHeroImage.Source = hero;
            LoginHeroImage.Visibility = Visibility.Visible;
            LoginHeroFallback.Visibility = Visibility.Collapsed;
        }
    }

    private static System.Windows.Media.Imaging.BitmapImage? TryLoadBrandBitmap(string packUri)
    {
        try
        {
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(packUri, UriKind.Absolute);
            bmp.EndInit();   // throws if the resource is not present/embedded
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            // Asset not placed yet (or not yet included as a WPF Resource) — callers keep their fallback UI.
            return null;
        }
    }

    private void ShowApp()
    {
        HideLoading();

        AuthGrid.Visibility = Visibility.Collapsed;
        AppGrid.Visibility = Visibility.Visible;

        FadeIn(AppGrid, 240);

        UserSummaryText.Text = GetAccountSummary();

        // Phase 4: offer crash recovery of local research data once, right after login.
        CheckAutosaveRecovery();

        RefreshAll();
        ShowPage(PageOverview);   // Phase 3A: research-only shell lands on the OrbitLab dashboard (Research Lab is one click away)
        UpdatePlanCard();         // Phase 9: real, read-only plan card (backend entitlement)
        SetStatus("Logged in successfully.");

        // Phase 7: heartbeat on app entry (throttled, fail-open; offline grace applies).
        _ = RefreshLicenseAsync();
        // Phase 9: load current-cycle usage for the plan card (fresh from the backend).
        _ = RefreshAccountOverviewAsync();
        // Phase 9: resolve any pending first-run finalizations now that we're signed in.
        if (_pendingFinalizations.Count > 0) { EnsurePendingRetryTimer(); _ = RetryPendingFinalizationsAsync(); }
        // Phase 10: release orphan drafts + register grandfathered projects (once per run).
        _ = ReconcileDraftsAsync();
    }

    private void ShowPage(UIElement page)
    {
        foreach (var child in ContentRoot.Children.OfType<UIElement>())
            child.Visibility = Visibility.Collapsed;

        page.Visibility = Visibility.Visible;

        FadeIn(page, 180);
        SetActiveNav(page);

        RefreshAll();
    }

    private void BuildNavMap()
    {
        _navMap[PageDashboard] = NavDashboard;
        _navMap[PageGenerate] = NavGenerate;
        _navMap[PageImport] = NavImport;
        _navMap[PageDecks] = NavDecks;
        _navMap[PageStudy] = NavStudy;
        _navMap[PagePreview] = NavPreview;
        _navMap[PageExport] = NavExport;
        _navMap[PageSettings] = NavSettings;
        _navMap[PageAccount] = NavAccount;
        _navMap[PageResearch] = NavResearch;
        _navMap[PageResearchDashboard] = NavResearch;
        _navMap[PageOverview] = NavOverview;
        _navMap[PageChartsStudio] = NavChartsStudio;
    }

    private void SetActiveNav(UIElement page)
    {
        var normal = (Style)FindResource("NavButton");
        var active = (Style)FindResource("NavButtonActive");

        foreach (var button in _navMap.Values)
            button.Style = normal;

        if (_navMap.TryGetValue(page, out var activeButton))
            activeButton.Style = active;
    }

    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        string email = NormalizeEmail(LoginEmailBox.Text);
        string password = LoginPasswordBox.Password;

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            ShowToast("Enter email and password.");
            return;
        }

        // Phase 7: real backend login when a license server is configured (the
        // production default). The legacy local store below remains only for
        // explicit dev/offline builds (backendBaseUrl = "disabled").
        if (AppConfig.IsBackendConfigured)
        {
            await BackendLoginAsync(email, password);
            return;
        }

        var user = _store.Accounts.FirstOrDefault(x =>
            string.Equals(x.Email, email, StringComparison.OrdinalIgnoreCase));

        if (user is null)
        {
            ShowToast("Account not found.");
            return;
        }

        if (user.PasswordHash != HashPassword(email, password))
        {
            ShowToast("Wrong password.");
            return;
        }

        if (DateTime.UtcNow > user.SubscriptionExpiresAt)
        {
            ShowToast("Your activation expired.");
            return;
        }

        _currentUser = user;
        ShowApp();
    }

    private async void Signup_Click(object sender, RoutedEventArgs e)
    {
        string email = NormalizeEmail(SignupEmailBox.Text);
        string password = SignupPasswordBox.Password;
        string code = NormalizeCode(SignupCodeBox.Text);

        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
        {
            ShowToast("Enter a valid email.");
            return;
        }

        // Phase 7: real backend registration when a license server is configured
        // (the production default). The legacy local stub below remains only for
        // explicit dev/offline builds (backendBaseUrl = "disabled").
        if (AppConfig.IsBackendConfigured)
        {
            await BackendSignupAsync(email, password, code);
            return;
        }

        if (password.Length < 4)
        {
            ShowToast("Password must be at least 4 characters for this demo.");
            return;
        }

        // Phase 3A stub: validate the new Commercial Beta onboarding fields in the UI.
        // These are NOT persisted yet (account persistence schema unchanged); real
        // capture + backend device binding is Phase 5-7.
        if (string.IsNullOrWhiteSpace(SignupFullNameBox.Text))
        {
            ShowToast("Enter your full name.");
            return;
        }

        if (string.IsNullOrWhiteSpace(SignupTelegramBox.Text))
        {
            ShowToast("Enter your Telegram username.");
            return;
        }

        if (SignupTelegramAckCheck.IsChecked != true)
        {
            ShowToast("Please acknowledge the official Telegram channel notice to continue.");
            return;
        }

        if (_store.Accounts.Any(x => string.Equals(x.Email, email, StringComparison.OrdinalIgnoreCase)))
        {
            ShowToast("This email already has an account.");
            return;
        }

        var activation = GetActivationInfo(code);

        if (activation is null)
        {
            ShowToast("Invalid activation code.");
            return;
        }

        if (IsCodeUsed(code))
        {
            ShowToast("This activation code was already used.");
            return;
        }

        var user = new LocalAccount
        {
            Email = email,
            PasswordHash = HashPassword(email, password),
            Plan = activation.Value.Plan,
            CreatedAt = DateTime.UtcNow,
            SubscriptionExpiresAt = activation.Value.Lifetime
                ? DateTime.MaxValue
                : DateTime.UtcNow.AddDays(activation.Value.Days)
        };

        _store.Accounts.Add(user);
        _store.UsedCodes.Add(new UsedActivationCode
        {
            Code = code,
            UsedByEmail = email,
            Plan = activation.Value.Plan,
            UsedAt = DateTime.UtcNow
        });

        SaveStore();

        _currentUser = user;
        ShowApp();

        // Phase 3A stub: show the one-time Activation Welcome after a successful (stub)
        // activation. Session-only guard here; the real per-device one-time trigger
        // (userId + subscriptionId + deviceHash) is Phase 7.
        if (!_activationWelcomeShownThisSession)
        {
            _activationWelcomeShownThisSession = true;
            ShowActivationWelcome();
        }
    }

    private void Logout_Click(object sender, RoutedEventArgs e)
    {
        // Phase 7: explicit logout also clears the encrypted license session cache.
        _license = null;
        LicenseStore.Clear();
        _currentUser = null;
        ShowAuth();
    }

    // "Need access?" strip on the login tab jumps to the Create Account tab.
    private void NeedAccess_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        AuthTabs.SelectedIndex = 1;
        SignupEmailBox.Focus();
    }

    // Pressing Enter in any login field submits the login form.
    private void LoginField_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            e.Handled = true;
            Login_Click(sender, e);
        }
    }

    // Pressing Enter in any Create Account field submits the signup form.
    private void SignupField_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            e.Handled = true;
            Signup_Click(sender, e);
        }
    }

    // ============ Phase 3A: OrbitLab dashboard, Charts Studio (coming soon), Activation Welcome (stub) ============
    // UI / navigation + a local design-review stub only. No backend, no server, no persistence, no secrets.

    private void Overview_Click(object sender, RoutedEventArgs e) => ShowPage(PageOverview);

    private void ChartsStudio_Click(object sender, RoutedEventArgs e) => EnterChartsStudio();

    private async void Research_QuickAction(object sender, System.Windows.Input.MouseButtonEventArgs e) => await EnterResearchLabAsync();

    private void ChartsStudio_QuickAction(object sender, System.Windows.Input.MouseButtonEventArgs e) => EnterChartsStudio();

    // ---- Charts Studio host ------------------------------------------------
    // Phase 1. This is the ENTIRE footprint of Charts Studio inside MainWindow: one lazily
    // created view model and one entry call. Everything else lives under ChartsStudio\ and
    // MainWindow knows nothing about contexts, sessions, figures or persistence.
    //
    // The module reaches back into research data through two callbacks rather than holding a
    // reference to _researchData, so it always sees the current project list without owning
    // or mutating it.

    private ChartsStudio.Presentation.ViewModels.ChartsStudioViewModel? _chartsStudioViewModel;

    private void EnterChartsStudio()
    {
        if (_chartsStudioViewModel is null)
        {
            var store = new ChartsStudio.Infrastructure.Persistence.ChartsStudioStore(dataDir);

            // The project source is handed to the ADAPTER, not to the session or view model.
            // That is what keeps ResearchProject out of every layer above Infrastructure.
            var provider = new ChartsStudio.Infrastructure.ResearchLabAdapter.AnalysisContextProvider(
                () => _researchData.Projects);

            var session = new ChartsStudio.Application.Session.ChartsStudioSession(store, provider);

            // Phase 2: figures are drawn by ScottPlot behind the IFigureRenderer interface, and
            // scheduled off the UI thread by the render queue.
            var renderer = new ChartsStudio.Infrastructure.Rendering.ScottPlotFigureRenderer();
            var renderQueue = new ChartsStudio.Application.Rendering.FigureRenderQueue(renderer);

            _chartsStudioViewModel = new ChartsStudio.Presentation.ViewModels.ChartsStudioViewModel(
                session, renderQueue, Dispatcher);

            ChartsStudioHost.DataContext = _chartsStudioViewModel;
        }

        ShowPage(PageChartsStudio);
        ChartsStudioHost.Enter();
    }

    // Phase 9: the Dashboard license/plan state is now real, read-only, and backed by
    // backend entitlement data (see UpdatePlanCard). The design-review license-state
    // stub (simulated Active/Expired/Unlicensed) has been removed entirely.
    private bool _activationWelcomeShownThisSession;

    // ----- Activation Welcome (shown once after a real successful registration) -----

    private void ShowActivationWelcome()
    {
        ActivationWelcomeOverlay.Visibility = Visibility.Visible;
        FadeIn(ActivationWelcomeOverlay, 240);

        // Subtle scale-up entrance on the card (premium, not flashy).
        var scale = new System.Windows.Media.ScaleTransform(0.95, 0.95);
        ActivationWelcomeCard.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
        ActivationWelcomeCard.RenderTransform = scale;
        var anim = new System.Windows.Media.Animation.DoubleAnimation(0.95, 1.0, new System.Windows.Duration(TimeSpan.FromMilliseconds(280)))
        {
            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
        };
        scale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, anim);
        scale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, anim);
    }

    private void HideActivationWelcome() => ActivationWelcomeOverlay.Visibility = Visibility.Collapsed;

    private void WelcomeGoDashboard_Click(object sender, RoutedEventArgs e)
    {
        HideActivationWelcome();
        ShowPage(PageOverview);
    }

    private void WelcomeOpenResearch_Click(object sender, RoutedEventArgs e)
    {
        HideActivationWelcome();
        ShowPage(PageResearch);
    }

    private void JoinTelegram_Click(object sender, RoutedEventArgs e)
    {
        // The app never ships a hardcoded seller list; users are pointed to the official channel.
        // Phase 6A: the URL is admin-configured on the backend (public bootstrap) with the
        // Branding constant as fallback; until one is configured this stays a safe no-op.
        var url = EffectiveTelegramUrl();
        if (url is null)
        {
            ShowToast("The official Telegram channel link is not configured yet.");
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            ShowToast("Could not open the Telegram channel link.");
        }
    }

    // ---------------------------------------------------------------------
    // Phase 6A — public bootstrap (Telegram link etc.) + startup update check.
    // Everything here is fail-open: no backend configured, unreachable backend,
    // or bad data must never crash the app or lock the user out. The forced
    // gate appears ONLY when the backend was reachable and explicitly returned
    // forced=true with this version below the minimum supported version.
    // ---------------------------------------------------------------------

    private BootstrapInfo? _bootstrap;          // last known public bootstrap (cache or live)
    private UpdatePolicyResult? _updatePolicy;  // last update policy from the backend
    private bool _updateGateShown;
    private bool _updatePromptShownThisRun;   // Phase 10: optional-update dialog, once per run

    /// <summary>Best available official Telegram URL (backend-configured first). Https only.</summary>
    private string? EffectiveTelegramUrl()
    {
        var url = _bootstrap?.TelegramChannelUrl;
        if (string.IsNullOrWhiteSpace(url) && !Branding.TelegramChannelUrl.StartsWith("{{", StringComparison.Ordinal))
            url = Branding.TelegramChannelUrl;
        return Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps ? url : null;
    }

    private void SetUpdateStatus(string text)
    {
        try { UpdateStatusText.Text = text; } catch { /* settings page not built yet */ }
    }

    private async Task StartupUpdateAndBootstrapAsync()
    {
        try
        {
            _bootstrap = UpdatePolicyClient.LoadCachedBootstrap();
            if (!AppConfig.IsBackendConfigured)
            {
                // Normal state until domain+HTTPS exist: quiet note in Settings only.
                SetUpdateStatus("Update check unavailable (no update server is configured in this beta build).");
                return;
            }

            var boot = await UpdatePolicyClient.TryGetBootstrapAsync();
            if (boot is not null) _bootstrap = boot;

            await CheckForUpdatesAsync(interactive: false);
        }
        catch
        {
            SetUpdateStatus("Update check unavailable.");
        }
    }

    private async Task CheckForUpdatesAsync(bool interactive)
    {
        var policy = await UpdatePolicyClient.TryGetVersionPolicyAsync();
        if (policy is null)
        {
            // Unreachable server must never lock the user out — subtle note only.
            SetUpdateStatus("Update check unavailable (could not reach the update server).");
            if (interactive) ShowToast("Update check unavailable — could not reach the update server.");
            return;
        }

        _updatePolicy = policy;

        if (policy.Forced && VersionUtil.IsBelow(AppConfig.CurrentVersion, policy.MinimumSupportedVersion))
        {
            SetUpdateStatus($"Required update: v{policy.LatestVersion} (minimum supported is v{policy.MinimumSupportedVersion}).");
            if (_updateGateShown) return;
            _updateGateShown = true;
            var gate = new UpdateRequiredWindow(policy, EffectiveTelegramUrl());
            if (IsLoaded && IsVisible) gate.Owner = this;
            gate.ShowDialog();   // closing the gate closes the app
            return;
        }

        if (VersionUtil.TryParse(policy.LatestVersion) is not null &&
            VersionUtil.IsBelow(AppConfig.CurrentVersion, policy.LatestVersion))
        {
            SetUpdateStatus($"Update v{policy.LatestVersion} is available (optional). Downloads are announced in the official Telegram channel.");
            // Phase 10: a clear once-per-run dialog (Install Update / Later) instead of
            // only a toast. Optional — "Later" continues into the app normally.
            if (!_updatePromptShownThisRun)
            {
                _updatePromptShownThisRun = true;
                var prompt = new UpdatePromptWindow(policy);
                if (IsLoaded && IsVisible) prompt.Owner = this;
                prompt.ShowDialog();
            }
            else
            {
                ShowToast($"{Branding.ProductName} v{policy.LatestVersion} is available — see Settings → Updates & Telegram.");
            }
        }
        else
        {
            SetUpdateStatus($"You're up to date (v{AppConfig.CurrentVersionDisplay}, {AppConfig.ReleaseChannel} channel).");
            if (interactive) ShowToast("You're up to date.");
        }
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (!AppConfig.IsBackendConfigured)
        {
            SetUpdateStatus("Update check unavailable (no update server is configured in this beta build).");
            ShowToast("Update checks are not available yet in this beta build.");
            return;
        }

        ShowToast("Checking for updates…");
        var boot = await UpdatePolicyClient.TryGetBootstrapAsync();
        if (boot is not null) _bootstrap = boot;
        await CheckForUpdatesAsync(interactive: true);

        // Phase 6A.1: when a Velopack update feed is configured (localhost staging
        // only until domain+HTTPS exist), also ask the installed updater. Detection
        // only — nothing is downloaded or applied from here.
        if (!string.IsNullOrEmpty(AppConfig.UpdateFeedUrl))
        {
            try
            {
                var feedResult = await UpdateServiceFactory.Create().CheckAsync();
                string note = feedResult.Status switch
                {
                    UpdateCheckStatus.UpdateAvailable => $"Installer feed: package v{feedResult.AvailableVersion} is available.",
                    UpdateCheckStatus.UpToDate => "Installer feed: package is up to date.",
                    UpdateCheckStatus.NotInstalled => "Installer feed: this is a dev build (not an installed copy), so package updates do not apply.",
                    UpdateCheckStatus.Failed => "Installer feed: check failed.",
                    _ => "",
                };
                if (note.Length > 0) UpdateStatusText.Text += $"  {note}";
            }
            catch { /* feed check is best-effort; never disturbs the app */ }
        }
    }

    // ---------------------------------------------------------------------
    // Phase 7 (Checkpoint D) — backend account/license integration (MVP).
    // Everything here exchanges ACCOUNT/LICENSE METADATA ONLY — no research
    // projects, datasets, results, reports, or exports are ever transmitted.
    // All flows fail safe: an unreachable server never crashes the app, and a
    // valid cached license keeps working within its offline-grace window.
    // The staging session token is honestly NOT a signed offline license;
    // Phase 5B (Ed25519 tokens) will replace validation without changing callers.
    // ---------------------------------------------------------------------

    private LicenseCache? _license;                 // decrypted in-memory license session
    private bool _authBusy;                         // prevents double-submit of auth forms
    private DateTime _lastHeartbeatAttemptUtc = DateTime.MinValue;

    // Phase 9: server-authoritative account overview (current-cycle project usage +
    // Telegram-prompt state). Refreshed from the backend; never trusted from local state.
    private AccountOverviewDto? _overview;
    private bool _telegramPromptShownThisRun;       // guards against duplicate modal within a run

    // Phase 9: projects whose first-run reservation succeeded + analysis completed, but
    // whose finalize call has not yet landed (transient network / crash). Persisted locally
    // (opaque ids + the on-hold result, LOCAL ONLY — never uploaded) and retried/recovered
    // automatically. Such projects are blocked from further first-run actions and deletion
    // until resolved. See PendingFinalization / PendingFinalizationRecovery.
    private List<PendingFinalization> _pendingFinalizations = new();
    private DispatcherTimer? _pendingRetryTimer;
    private string _pendingResultOverlayProjectId = "";
    private string PendingFinalizationsPath => Path.Combine(dataDir, "research_pending_finalizations.json");

    // Phase 10: true only when research_projects.json loaded without error (or is a
    // genuinely fresh store). Gates draft reconciliation so a corrupt/unreadable file
    // can never cause server-side draft registrations to be released by mistake.
    private bool _researchLoadedCleanly;
    private bool _draftReconcileDoneThisRun;

    /// <summary>
    /// Phase 10 housekeeping, run once per session after sign-in:
    ///  1. RECONCILE — release server-side draft registrations whose local project no
    ///     longer exists (e.g. a crash between registration and the local save, or a
    ///     deletion that couldn't reach the server), freeing their plan positions.
    ///  2. GRANDFATHER — register drafts for pre-update, never-counted local projects
    ///     (best-effort; if the allowance is already full they stay usable locally and
    ///     the first-analysis reservation remains the hard gate).
    /// Only opaque project ids ever cross the wire.
    /// </summary>
    private async Task ReconcileDraftsAsync()
    {
        if (_draftReconcileDoneThisRun || !_researchLoadedCleanly) return;
        if (_license is null || string.IsNullOrEmpty(_license.Token) || !AppConfig.IsBackendConfigured) return;
        _draftReconcileDoneThisRun = true;
        try
        {
            var drafts = await LicenseApiClient.GetDraftIdsAsync(_license.Token);
            if (drafts.Ok)
            {
                var localIds = _researchData.Projects.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
                foreach (var orphan in drafts.Data!.ProjectIds.Where(id => !localIds.Contains(id)))
                    try { await LicenseApiClient.ReleaseDraftAsync(_license.Token, orphan); } catch { }
            }

            // Grandfather existing never-counted projects (created before drafts existed).
            foreach (var p in _researchData.Projects.Where(p => p.DescriptiveStatistics is null))
                try { await LicenseApiClient.RegisterProjectAsync(_license.Token, p.Id); } catch { }

            await RefreshAccountOverviewAsync();
        }
        catch { /* best-effort housekeeping; retried next run */ }
    }

    /// <summary>Register → login → bind device → heartbeat → enter the app.</summary>
    private async Task BackendSignupAsync(string email, string password, string code)
    {
        if (_authBusy) return;

        // Local validation mirrors the backend's rules so users get instant feedback.
        if (password.Length < 8) { ShowToast("Password must be at least 8 characters."); return; }
        if (string.IsNullOrWhiteSpace(SignupFullNameBox.Text)) { ShowToast("Enter your full name."); return; }
        if (string.IsNullOrWhiteSpace(SignupTelegramBox.Text)) { ShowToast("Enter your Telegram username."); return; }
        if (SignupTelegramAckCheck.IsChecked != true) { ShowToast("Please acknowledge the official Telegram channel notice to continue."); return; }
        if (string.IsNullOrWhiteSpace(code)) { ShowToast("Enter your activation code."); return; }

        _authBusy = true;
        try
        {
            ShowToast("Creating your OrbitLab account…");
            var reg = await LicenseApiClient.RegisterAsync(
                SignupFullNameBox.Text.Trim(), email, password,
                SignupInstitutionBox.Text.Trim(), SignupRoleBox.Text.Trim(),
                SignupTelegramBox.Text.Trim(), telegramChannelAcknowledged: true,
                activationCode: code);
            if (!reg.Ok) { ShowToast(reg.Error!.Message); return; }

            // The activation code is now redeemed server-side; it is never stored locally.
            bool entered = await BackendEstablishSessionAsync(email, password);
            if (entered && !_activationWelcomeShownThisSession)
            {
                _activationWelcomeShownThisSession = true;
                ShowActivationWelcome();
            }
        }
        finally { _authBusy = false; }
    }

    private async Task BackendLoginAsync(string email, string password)
    {
        if (_authBusy) return;
        _authBusy = true;
        try
        {
            ShowToast("Signing in…");
            await BackendEstablishSessionAsync(email, password);
        }
        finally { _authBusy = false; }
    }

    /// <summary>Login + device activation + first heartbeat; saves the encrypted session cache.</summary>
    private async Task<bool> BackendEstablishSessionAsync(string email, string password)
    {
        var login = await LicenseApiClient.LoginAsync(email, password);
        if (!login.Ok) { ShowToast(login.Error!.Message); return false; }

        var data = login.Data!;
        if (string.IsNullOrEmpty(data.Token) || data.User is null)
        { ShowToast("The server returned an unexpected login response."); return false; }
        if (data.Subscription is null)
        { ShowToast("This account has no subscription. Please contact support."); return false; }

        string deviceHash = DeviceIdentity.ComputeDeviceHash();
        var device = await LicenseApiClient.ActivateDeviceAsync(
            data.Token, deviceHash, DeviceIdentity.DeviceName, DeviceIdentity.OsInfo);
        if (!device.Ok)
        {
            ShowToast(device.Error!.Code == "device_limit"
                ? "This subscription is already linked to another device. Please contact support to reset your device activation."
                : device.Error.Message);
            return false;
        }

        _license = new LicenseCache
        {
            UserId = data.User.Id,
            Email = data.User.Email ?? email,
            FullName = data.User.FullName ?? "",
            Token = data.Token!,
            SubscriptionId = data.Subscription.Id,
            PlanCode = data.Subscription.PlanCode ?? "",
            PlanName = data.Subscription.PlanName ?? "Commercial Beta",
            Status = data.Subscription.Status ?? "Active",
            EntitlementsJson = data.Subscription.Entitlements.ValueKind == System.Text.Json.JsonValueKind.Undefined
                ? "" : data.Subscription.Entitlements.GetRawText(),
            GraceDays = data.Subscription.GraceDays,
            EndsAtUtc = data.Subscription.EndsAtUtc,
            DeviceActivationId = device.Data!.DeviceActivationId,
            DeviceHash = deviceHash,
            LastHeartbeatUtc = DateTime.UtcNow,     // provisional until the heartbeat below
        };
        await RefreshLicenseAsync(force: true);
        LicenseStore.Save(_license);

        _currentUser = new LocalAccount
        {
            Email = _license.Email,
            Plan = _license.PlanName,
            CreatedAt = DateTime.UtcNow,
            SubscriptionExpiresAt = DateTime.MaxValue,   // real gating uses _license, not this legacy field
        };
        ShowApp();
        return true;
    }

    /// <summary>
    /// Heartbeat: refreshes subscription status/entitlements. Throttled to one
    /// attempt per 5 minutes unless forced. Network failures keep the cached
    /// license (offline grace); an invalid token marks the session expired.
    /// </summary>
    private async Task RefreshLicenseAsync(bool force = false)
    {
        if (_license is null || !AppConfig.IsBackendConfigured) return;
        if (!force && DateTime.UtcNow - _lastHeartbeatAttemptUtc < TimeSpan.FromMinutes(5)) return;
        _lastHeartbeatAttemptUtc = DateTime.UtcNow;

        var hb = await LicenseApiClient.HeartbeatAsync(_license.Token, _license.SubscriptionId, _license.DeviceHash);
        if (hb.Ok)
        {
            var d = hb.Data!;
            _license.Status = d.Status ?? _license.Status;
            _license.GraceDays = d.GraceDays;
            _license.EndsAtUtc = d.EndsAtUtc;
            // The server's authoritative expiry answer. It was already being sent and
            // deserialized, then discarded - the client re-derived entitlement from the
            // status string instead, so a subscription past its end date still looked
            // active here until the string happened to be flipped.
            _license.Expired = d.Expired;
            // The server's clock, and the highest local time ever seen. Offline grace is
            // measured against the LOCAL clock, so without these a user could roll the PC
            // date back and extend the offline window indefinitely.
            _license.LastServerTimeUtc = d.ServerTimeUtc;
            if (DateTime.UtcNow > _license.MaxObservedUtc) _license.MaxObservedUtc = DateTime.UtcNow;
            if (d.ServerTimeUtc > _license.MaxObservedUtc) _license.MaxObservedUtc = d.ServerTimeUtc;
            if (d.Entitlements.ValueKind is not System.Text.Json.JsonValueKind.Undefined
                and not System.Text.Json.JsonValueKind.Null)
                _license.EntitlementsJson = d.Entitlements.GetRawText();
            _license.LastHeartbeatUtc = DateTime.UtcNow;
            LicenseStore.Save(_license);
        }
        else if (hb.Error!.Code == "unauthorized")
        {
            _license.Status = "SessionExpired";     // token no longer valid server-side
            LicenseStore.Save(_license);
        }
        // "network" and other transient errors: keep the cache — offline grace applies.
    }

    /// <summary>Silent sign-in from the encrypted cache at startup (installed/returning users).</summary>
    private async Task TryRestoreSessionAsync()
    {
        try
        {
            if (!AppConfig.IsBackendConfigured || _currentUser is not null) return;
            var cached = LicenseStore.Load();
            if (cached is null || string.IsNullOrEmpty(cached.Token)) return;

            _license = cached;
            await RefreshLicenseAsync(force: true);

            if (_license.Status == "SessionExpired")
            {
                _license = null;
                LicenseStore.Clear();
                ShowToast("Your session has expired. Please log in again.");
                return;
            }

            // Enter the app even when the subscription is suspended/expired: the
            // user keeps read/export access to their local data; protected actions
            // are gated separately below.
            _currentUser = new LocalAccount
            {
                Email = _license.Email,
                Plan = _license.PlanName,
                CreatedAt = DateTime.UtcNow,
                SubscriptionExpiresAt = DateTime.MaxValue,
            };
            ShowApp();
        }
        catch { /* session restore must never break startup */ }
    }

    /// <summary>
    /// Gate for protected actions (new project, running analyses). Read/export of
    /// existing local data is deliberately NOT gated — user data stays accessible.
    /// </summary>
    private bool LicenseAllowsProtectedAction(out string message)
    {
        message = "";
        if (!AppConfig.IsBackendConfigured) return true;   // explicit dev/offline build

        _ = RefreshLicenseAsync();                         // background refresh, throttled

        if (_license is null)
        {
            message = "Please log in to your OrbitLab account to use this feature.";
            return false;
        }
        if (_license.Status == "SessionExpired")
        {
            message = "Your session has expired. Please log out and log in again.";
            return false;
        }
        // One authoritative decision (LicenseCache.AllowsProtectedActions). The checks
        // below only work out WHICH reason to show; they can no longer disagree with the
        // decision itself, which is what a second inline copy of this logic risked.
        if (_license.AllowsProtectedActions) return true;

        if (!_license.IsActive)
        {
            message = "Your OrbitLab subscription is not active. Please contact support or renew your Commercial Beta access to continue creating projects and running analyses.";
            return false;
        }
        if (_license.ClockLooksRolledBack)
        {
            message = "Your computer's clock appears to be set incorrectly, so your subscription could not be verified offline. Please correct the date and time, then connect to the internet once.";
            return false;
        }
        message = "OrbitLab could not verify your subscription recently. Please connect to the internet once so your license can refresh.";
        return false;
    }

    /// <summary>Commercial Beta: 1 active project (from entitlements); legacy dev builds keep the old limit.</summary>
    private int EffectiveProjectLimit() =>
        AppConfig.IsBackendConfigured && _license is not null
            ? Math.Max(1, _license.MaxProjects)
            : ResearchProjectLimit;

    private string ProjectLimitMessage(int limit) =>
        limit == 1
            ? "Your Commercial Beta plan includes 1 project for this subscription cycle."
            : $"Your plan includes {limit} projects for this subscription cycle.";

    // =====================================================================
    // Phase 9 — server-authoritative project-usage accounting, plan card,
    // and the once-per-account Telegram prompt. The backend owns the limit,
    // the cycle, and the usage count; the client only displays and requests.
    // =====================================================================

    /// <summary>Refreshes the current-cycle usage + Telegram-prompt state from the backend, then repaints the plan card.</summary>
    private async Task RefreshAccountOverviewAsync()
    {
        if (!AppConfig.IsBackendConfigured || _license is null || string.IsNullOrEmpty(_license.Token)) return;
        var r = await LicenseApiClient.GetAccountOverviewAsync(_license.Token);
        if (r.Ok) _overview = r.Data;
        UpdatePlanCard();
    }

    /// <summary>Fetches a project's server-side usage state (null when the backend is unreachable).</summary>
    private async Task<ProjectStatusDto?> GetProjectServerStateAsync(string projectId)
    {
        if (!AppConfig.IsBackendConfigured || _license is null || string.IsNullOrEmpty(_license.Token)) return null;
        var r = await LicenseApiClient.GetProjectStatusAsync(_license.Token, projectId);
        return r.Ok ? r.Data : null;
    }

    // ----- pending finalization (crash/network-safe first-run recording) -----

    private void LoadPendingFinalizations()
    {
        try
        {
            var loaded = File.Exists(PendingFinalizationsPath)
                ? JsonSerializer.Deserialize<List<PendingFinalization>>(File.ReadAllText(PendingFinalizationsPath)) ?? new()
                : new List<PendingFinalization>();
            // Drop corrupt/missing entries: a record without an opaque project id can never be
            // resolved or matched to a project, so it is discarded rather than left to block.
            _pendingFinalizations = loaded.Where(p => p is not null && !string.IsNullOrWhiteSpace(p.ProjectId)).ToList();
            if (_pendingFinalizations.Count != loaded.Count) SavePendingFinalizations();
        }
        catch { _pendingFinalizations = new(); }
    }

    private void SavePendingFinalizations()
    {
        try { File.WriteAllText(PendingFinalizationsPath, JsonSerializer.Serialize(_pendingFinalizations)); }
        catch { /* best-effort; retried in memory regardless */ }
    }

    private PendingFinalization? GetPending(string projectId) =>
        _pendingFinalizations.FirstOrDefault(p => p.ProjectId == projectId);

    // A recoverable pending record blocks first-run/deletion while it is still being recorded.
    // An unrecoverable one (allowance full) does NOT block — it is resolved by discarding.
    private bool HasBlockingPending(string projectId) =>
        _pendingFinalizations.Any(p => p.ProjectId == projectId && !p.Unrecoverable);

    private bool HasUnrecoverablePending(string projectId) =>
        _pendingFinalizations.Any(p => p.ProjectId == projectId && p.Unrecoverable);

    private void AddPendingFinalization(string projectId, string reservationId, DescriptiveStatisticsRecord result)
    {
        if (GetPending(projectId) is null)
        {
            _pendingFinalizations.Add(new PendingFinalization
            {
                ProjectId = projectId,
                ReservationId = reservationId,
                Result = result,               // stored LOCALLY so recovery can show it after finalize; never uploaded
                CreatedAtUtc = DateTime.UtcNow,
            });
            SavePendingFinalizations();
        }
        EnsurePendingRetryTimer();
    }

    private void EnsurePendingRetryTimer()
    {
        // Only run while there is at least one still-recoverable record to retry.
        if (!_pendingFinalizations.Any(p => !p.Unrecoverable)) return;
        if (_pendingRetryTimer is not null) return;
        _pendingRetryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
        _pendingRetryTimer.Tick += async (_, _) => await RetryPendingFinalizationsAsync();
        _pendingRetryTimer.Start();
    }

    /// <summary>
    /// Recovers outstanding first-run finalizations (see PendingFinalizationRecovery). On success
    /// the project becomes properly counted AND its on-hold result is committed/shown. If the slot
    /// can no longer be obtained (allowance full), the record is flagged unrecoverable so the user
    /// can discard it — the result is never committed and the project is never counted twice.
    /// </summary>
    private async Task RetryPendingFinalizationsAsync()
    {
        if (_license is null || string.IsNullOrEmpty(_license.Token)) return;
        string token = _license.Token;   // capture once so a mid-loop session change can't NRE
        var recovery = new PendingFinalizationRecovery(
            (pid, rid) => LicenseApiClient.FinalizeProjectAsync(token, pid, rid),
            pid => LicenseApiClient.ReserveProjectAsync(token, pid));

        foreach (var pending in _pendingFinalizations.Where(p => !p.Unrecoverable).ToList())
        {
            var outcome = await recovery.ResolveAsync(pending);
            switch (outcome)
            {
                case PendingRecoveryOutcome.Committed:
                    CommitPendingResult(pending);                              // show the on-hold result
                    _pendingFinalizations.RemoveAll(p => p.ProjectId == pending.ProjectId);
                    SavePendingFinalizations();
                    break;
                case PendingRecoveryOutcome.Unrecoverable:
                    pending.Unrecoverable = true;
                    pending.UnrecoverableReason = "allowance_full";
                    SavePendingFinalizations();                               // keep for the user to discard; stop retrying it
                    ShowToast("A finished analysis couldn't be activated — your current plan's project allowance is now full.");
                    break;
                case PendingRecoveryOutcome.StillPending:
                    SavePendingFinalizations();                               // ReservationId may have been refreshed
                    break;
            }
        }
        if (!_pendingFinalizations.Any(p => !p.Unrecoverable)) { _pendingRetryTimer?.Stop(); _pendingRetryTimer = null; }
        await RefreshAccountOverviewAsync();
    }

    // Activates an on-hold result once its project is definitely counted server-side. Persists the
    // record into the project and renders it if that project's Statistics tab is open. Local only.
    private void CommitPendingResult(PendingFinalization pending)
    {
        var p = _researchData.Projects.FirstOrDefault(x => x.Id == pending.ProjectId);
        if (p is null || pending.Result is null) return;      // project gone or legacy record without a result
        p.DescriptiveStatistics = pending.Result;
        p.UpdatedAt = DateTime.UtcNow;
        SaveResearch();
        if (_openResearchId == p.Id)
        {
            StStaleBanner.Visibility = Visibility.Collapsed;
            RenderStatisticsOutput(pending.Result);
            var data = LoadStatisticsDataset(p, out _);
            if (data is not null) RenderDatasetOverview(p, data, BuildStatisticsMatchInput(p, data));
        }
        ShowToast("Your earlier analysis has been recorded and is now available.");
    }

    // Discards a pending result the user chose not to keep (typically an unrecoverable one). The
    // result is dropped locally; the project was never counted server-side, so nothing is refunded
    // and nothing is double-counted. Never leaves the project stuck in a pending state.
    private void DiscardPendingFinalization(string projectId)
    {
        _pendingFinalizations.RemoveAll(p => p.ProjectId == projectId);
        SavePendingFinalizations();
        if (!_pendingFinalizations.Any(p => !p.Unrecoverable)) { _pendingRetryTimer?.Stop(); _pendingRetryTimer = null; }
    }

    /// <summary>Renders the read-only Dashboard plan card purely from backend entitlement data.</summary>
    private void UpdatePlanCard()
    {
        if (PlanCardName is null) return;   // dashboard not built yet

        // Not signed in / no backend: neutral state.
        if (_license is null)
        {
            PlanCardName.Text = "Not signed in";
            PlanCardStatusChip.Visibility = Visibility.Collapsed;
            PlanCardProjects.Text = "";
            PlanCardExpiry.Text = "";
            PlanCardNote.Text = "Sign in to your OrbitLab account to see your plan.";
            return;
        }

        var u = _overview?.Usage;
        PlanCardName.Text = u?.PlanName is { Length: > 0 } pn ? pn : (string.IsNullOrEmpty(_license.PlanName) ? "Commercial Beta" : _license.PlanName);

        string status = u?.Status ?? _license.Status;
        bool active = (u?.Active ?? _license.IsActive) && !string.Equals(status, "SessionExpired", StringComparison.OrdinalIgnoreCase);
        string chipText = active ? "Active" : (string.Equals(status, "Expired", StringComparison.OrdinalIgnoreCase) ? "Expired" : "Inactive");
        PlanCardStatusChip.Visibility = Visibility.Visible;
        PlanCardStatusText.Text = chipText;
        var chipBrush = active ? "SuccessSoftBrush" : "DangerSoftBrush";
        var chipFg = active ? "SuccessBrush" : "DangerBrush";
        try { PlanCardStatusChip.Background = (Brush)FindResource(chipBrush); PlanCardStatusText.Foreground = (Brush)FindResource(chipFg); } catch { }

        if (u is not null)
        {
            // Phase 10: drafts occupy positions too, so show the full picture.
            PlanCardProjects.Text = u.Drafts > 0
                ? $"Projects: {u.Used} counted + {u.Drafts} draft{(u.Drafts == 1 ? "" : "s")} of {u.Limit}"
                : $"Projects used: {u.Used} of {u.Limit}";
            PlanCardExpiry.Text = u.EndsAtUtc is { } end
                ? $"Renews / ends: {end.ToLocalTime():MMM d, yyyy}"
                : "No expiry set";
            PlanCardNote.Text = active
                ? "A draft holds a project position from creation; it becomes permanently counted the first time you run its descriptive statistics. Deleting an uncounted draft frees its position."
                : "Your subscription is not active. Contact support or start a new subscription cycle to continue.";
        }
        else
        {
            // Backend temporarily unavailable — be honest, don't show a fake count.
            PlanCardProjects.Text = "Project usage unavailable";
            PlanCardExpiry.Text = "";
            PlanCardNote.Text = "OrbitLab couldn't reach the server to load your plan usage. Check your connection.";
        }
    }

    /// <summary>
    /// Shows the once-per-account Telegram prompt the first time an authenticated,
    /// actively-subscribed user opens Research Lab. Account-level (server) state, so it
    /// never reappears after acknowledgement — across logout, reinstall, or renewal.
    /// </summary>
    private async Task MaybeShowTelegramPromptAsync()
    {
        if (_telegramPromptShownThisRun) return;
        if (!AppConfig.IsBackendConfigured || _license is null || string.IsNullOrEmpty(_license.Token)) return;
        if (!_license.IsActive) return;

        // Authoritative check: only skip when the account has definitively acknowledged.
        var r = await LicenseApiClient.GetAccountOverviewAsync(_license.Token);
        if (r.Ok) _overview = r.Data;
        if (_overview is null || _overview.TelegramPromptAcknowledged) return;

        _telegramPromptShownThisRun = true;
        TelegramPromptOverlay.Visibility = Visibility.Visible;
        FadeIn(TelegramPromptOverlay, 160);
    }

    private bool _telegramAckInFlight;

    private async Task<bool> AckTelegramPromptAsync()
    {
        if (_license is null || string.IsNullOrEmpty(_license.Token)) return false;
        var r = await LicenseApiClient.AckTelegramPromptAsync(_license.Token);
        if (r.Ok && _overview is not null) _overview.TelegramPromptAcknowledged = true;
        return r.Ok;
    }

    private async void TelegramPromptJoin_Click(object sender, RoutedEventArgs e)
    {
        if (_telegramAckInFlight) return;
        _telegramAckInFlight = true;
        try
        {
            bool acked = await AckTelegramPromptAsync();
            if (!acked)
            {
                ShowToast("OrbitLab couldn't save your choice. Check your connection and try again.");
                return;   // keep the modal open so the account is not falsely marked acknowledged
            }
            TelegramPromptOverlay.Visibility = Visibility.Collapsed;
            var url = EffectiveTelegramUrl();
            if (url is not null)
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
                catch { ShowToast("Could not open the Telegram channel link."); }
            }
            else ShowToast("The official Telegram channel link is not configured yet.");
        }
        finally { _telegramAckInFlight = false; }
    }

    private async void TelegramPromptLater_Click(object sender, RoutedEventArgs e)
    {
        if (_telegramAckInFlight) return;
        _telegramAckInFlight = true;
        try
        {
            if (await AckTelegramPromptAsync())
                TelegramPromptOverlay.Visibility = Visibility.Collapsed;
            else
                ShowToast("OrbitLab couldn't save your choice. Check your connection and try again.");
        }
        finally { _telegramAckInFlight = false; }
    }

    // ---- Phase 9: unrecoverable pending-result dialog (allowance full) ----

    private void ShowPendingResultOverlay(string projectId, string message)
    {
        _pendingResultOverlayProjectId = projectId;
        PendingResultText.Text = message;
        PendingResultOverlay.Visibility = Visibility.Visible;
    }

    private void PendingResultDiscard_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_pendingResultOverlayProjectId))
            DiscardPendingFinalization(_pendingResultOverlayProjectId);
        _pendingResultOverlayProjectId = "";
        PendingResultOverlay.Visibility = Visibility.Collapsed;
        ShowToast("The pending result was discarded.");
    }

    private void PendingResultKeep_Click(object sender, RoutedEventArgs e)
    {
        _pendingResultOverlayProjectId = "";
        PendingResultOverlay.Visibility = Visibility.Collapsed;
    }

    // Left/Right arrow keys flip between cards on Preview/Edit — unless the user
    // is actively typing in a text field, where arrows should move the caret.
    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (PagePreview.Visibility != Visibility.Visible)
            return;

        if (System.Windows.Input.Keyboard.FocusedElement is TextBox)
            return;

        if (e.Key == System.Windows.Input.Key.Left)
        {
            NavigateCard(-1);
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Right)
        {
            NavigateCard(1);
            e.Handled = true;
        }
    }

    private void Dashboard_Click(object sender, RoutedEventArgs e) => ShowPage(PageDashboard);
    private void GeneratePage_Click(object sender, RoutedEventArgs e) => ShowPage(PageGenerate);
    private void ImportPage_Click(object sender, RoutedEventArgs e) => ShowPage(PageImport);
    private void DecksPage_Click(object sender, RoutedEventArgs e) => ShowPage(PageDecks);

    private void StudyPage_Click(object sender, RoutedEventArgs e)
    {
        StartStudySession(GetSelectedDeckIdFromCombo(StudyDeckCombo));
        ShowPage(PageStudy);
    }

    private void PreviewPage_Click(object sender, RoutedEventArgs e)
    {
        UpdatePreview();
        ShowPage(PagePreview);
    }

    private void ExportPage_Click(object sender, RoutedEventArgs e)
    {
        SaveCurrentEdits();
        ExportPreviewBox.Text = GetSelectedDeckAnkiText();
        ShowPage(PageExport);
    }

    private void SettingsPage_Click(object sender, RoutedEventArgs e)
    {
        // Phase 8: Settings no longer holds any AI/provider configuration.
        ShowPage(PageSettings);
    }

    private void AccountPage_Click(object sender, RoutedEventArgs e)
    {
        RefreshAccountPage();
        ShowPage(PageAccount);
    }

    private async void GenerateAutomatic_Click(object sender, RoutedEventArgs e)
    {
        string source = SourceBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(source))
        {
            ShowToast("Paste notes first.");
            return;
        }

        var deck = GetActiveDeck();

        if (deck is null)
        {
            ShowToast("Create or select a deck first.");
            ShowPage(PageDecks);
            return;
        }

        if (!_aiProxy.IsAvailable)
        {
            ShowToast("Please sign in to your OrbitLab account to use AI features.");
            return;
        }

        try
        {
            ShowLoading("Generating flashcards…");
            SetStatus("Generating flashcards…");
            IsEnabled = false;

            string prompt = BuildPrompt(source);
            string aiText = await CallAiAsync(prompt);
            var parsed = ParseFlashcards(aiText);

            if (parsed.Count == 0)
            {
                ImportBox.Text = aiText;
                ShowToast("AI replied, but no cards were parsed. Response placed in Import JSON.");
                ShowPage(PageImport);
                return;
            }

            foreach (var card in parsed)
            {
                card.Id = Guid.NewGuid().ToString("N");
                card.CreatedAt = DateTime.UtcNow;
                card.DueAt = DateTime.UtcNow;
            }

            deck.Cards.AddRange(parsed);
            _currentCardId = parsed.First().Id;

            SaveDecks();
            RefreshAll();
            UpdatePreview();

            ShowToast($"Generated {parsed.Count} cards into deck: {deck.Name}");
            ShowPage(PagePreview);
            SetStatus($"Generated {parsed.Count} cards into {deck.Name}.");
        }
        catch (Exception ex)
        {
            ShowToast("Generation failed: " + ex.Message);
            SetStatus("Generation failed.");
        }
        finally
        {
            IsEnabled = true;
            HideLoading();
        }
    }

    private void CreatePrompt_Click(object sender, RoutedEventArgs e)
    {
        string source = SourceBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(source))
        {
            ShowToast("Paste notes first.");
            return;
        }

        PromptBox.Text = BuildPrompt(source);
        Clipboard.SetText(PromptBox.Text);
        SetStatus("Manual prompt created and copied.");
    }

    private void CopyPrompt_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(PromptBox.Text))
        {
            ShowToast("Create a prompt first.");
            return;
        }

        Clipboard.SetText(PromptBox.Text);
        SetStatus("Prompt copied.");
    }

    private void ClearSource_Click(object sender, RoutedEventArgs e) => SourceBox.Clear();

    private void ImportCards_Click(object sender, RoutedEventArgs e)
    {
        string text = ImportBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(text))
        {
            ShowToast("Paste JSON first.");
            return;
        }

        var deck = GetActiveDeck();

        if (deck is null)
        {
            ShowToast("Create or select a deck first.");
            ShowPage(PageDecks);
            return;
        }

        var parsed = ParseFlashcards(text);

        if (parsed.Count == 0)
        {
            ShowToast("No cards found. Make sure the AI returned valid JSON with front/back/tags.");
            return;
        }

        foreach (var card in parsed)
        {
            card.Id = Guid.NewGuid().ToString("N");
            card.CreatedAt = DateTime.UtcNow;
            card.DueAt = DateTime.UtcNow;
        }

        deck.Cards.AddRange(parsed);
        _currentCardId = parsed.First().Id;

        SaveDecks();
        RefreshAll();
        UpdatePreview();

        ShowToast($"Imported {parsed.Count} cards into deck: {deck.Name}");
        ShowPage(PagePreview);
        SetStatus($"Imported {parsed.Count} cards into {deck.Name}.");
    }

    private void ClearImport_Click(object sender, RoutedEventArgs e) => ImportBox.Clear();

    private void DeckSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection)
            return;

        string deckId = "";

        if (sender is ComboBox cb)
            deckId = GetSelectedDeckIdFromCombo(cb);

        if (!string.IsNullOrWhiteSpace(deckId))
        {
            _activeDeckId = deckId;
            _deckStore.ActiveDeckId = deckId;
            SaveDecks();
            RefreshAll();
        }
    }

    private void ImportedList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection) return;

        if (ImportedList.SelectedItem is Flashcard card)
        {
            SaveCurrentEdits();
            _currentCardId = card.Id;
            UpdatePreview();
        }
    }

    private void DeckList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection) return;

        if (DeckList.SelectedItem is StudyDeck deck)
        {
            _activeDeckId = deck.Id;
            _deckStore.ActiveDeckId = deck.Id;
            SaveDecks();
            RefreshAll();
        }
    }

    private void CardsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection) return;

        if (CardsList.SelectedItem is Flashcard card)
        {
            SaveCurrentEdits();
            _currentCardId = card.Id;
            UpdatePreview();
        }
    }

    private void CreateDeck_Click(object sender, RoutedEventArgs e)
    {
        string name = DeckNameBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(name))
        {
            ShowToast("Enter a deck name first.");
            return;
        }

        if (_deckStore.Decks.Any(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            ShowToast("A deck with this name already exists.");
            return;
        }

        var deck = new StudyDeck
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            CreatedAt = DateTime.UtcNow
        };

        _deckStore.Decks.Add(deck);
        _activeDeckId = deck.Id;
        _deckStore.ActiveDeckId = deck.Id;
        DeckNameBox.Clear();

        SaveDecks();
        RefreshAll();

        ShowToast($"Deck created: {deck.Name}");
    }

    private void RenameDeck_Click(object sender, RoutedEventArgs e)
    {
        var deck = GetActiveDeck();

        if (deck is null)
        {
            ShowToast("Select a deck first.");
            return;
        }

        string name = DeckNameBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(name))
        {
            ShowToast("Type the new deck name in the box.");
            return;
        }

        if (_deckStore.Decks.Any(d => d.Id != deck.Id && string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            ShowToast("Another deck already has this name.");
            return;
        }

        deck.Name = name;
        DeckNameBox.Clear();

        SaveDecks();
        RefreshAll();

        ShowToast("Deck renamed.");
    }

    private void DeleteDeck_Click(object sender, RoutedEventArgs e)
    {
        var deck = GetActiveDeck();

        if (deck is null)
        {
            ShowToast("Select a deck first.");
            return;
        }

        if (_deckStore.Decks.Count <= 1)
        {
            ShowToast("You must keep at least one deck.");
            return;
        }

        var result = MessageBox.Show(
            $"Delete deck '{deck.Name}' and all its cards?",
            "Delete deck",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
            return;

        _deckStore.Decks.Remove(deck);
        _activeDeckId = _deckStore.Decks.First().Id;
        _deckStore.ActiveDeckId = _activeDeckId;
        _currentCardId = "";

        SaveDecks();
        RefreshAll();

        ShowToast("Deck deleted.");
    }

    private void SearchFilter_Changed(object sender, RoutedEventArgs e)
    {
        RefreshCardLists();
    }

    // Global top-bar search — Phase 3A stub. Research search is not implemented yet, so
    // searching never mirrors into, filters, or navigates to any legacy flashcard/deck
    // surface (My Decks, cards, decks, study). It stays on the current page.
    private void TopSearch_Changed(object sender, RoutedEventArgs e)
    {
        // Intentionally no-op for Phase 3A: do not touch the legacy deck/card filter.
    }

    private void TopSearch_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter)
            return;

        e.Handled = true;
        ShowToast("Research search will be available in a future beta update.");
    }

    private void DueOnly_Checked(object sender, RoutedEventArgs e)
    {
        RefreshCardLists();
    }

    private void StudySelectedDeck_Click(object sender, RoutedEventArgs e)
    {
        StartStudySession(_activeDeckId);
        ShowPage(PageStudy);
    }

    private void PreviewSelected_Click(object sender, RoutedEventArgs e)
    {
        if (CardsList.SelectedItem is Flashcard card)
        {
            _currentCardId = card.Id;
            UpdatePreview();
            ShowPage(PagePreview);
            return;
        }

        ShowToast("Select a card first.");
    }

    private void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        if (CardsList.SelectedItem is Flashcard card)
        {
            _currentCardId = card.Id;
            DeleteCurrentCard();
            return;
        }

        ShowToast("Select a card first.");
    }

    private void MoveSelected_Click(object sender, RoutedEventArgs e)
    {
        var sourceDeck = GetActiveDeck();

        if (sourceDeck is null)
        {
            ShowToast("Select a source deck first.");
            return;
        }

        if (CardsList.SelectedItem is not Flashcard card)
        {
            ShowToast("Select a card first.");
            return;
        }

        if (MoveDeckCombo.SelectedItem is not StudyDeck targetDeck)
        {
            ShowToast("Select target deck.");
            return;
        }

        if (targetDeck.Id == sourceDeck.Id)
        {
            ShowToast("This card is already in that deck.");
            return;
        }

        sourceDeck.Cards.Remove(card);
        targetDeck.Cards.Add(card);

        _activeDeckId = targetDeck.Id;
        _deckStore.ActiveDeckId = targetDeck.Id;
        _currentCardId = card.Id;

        SaveDecks();
        RefreshAll();
        UpdatePreview();

        ShowToast($"Moved card to {targetDeck.Name}.");
    }

    private void Previous_Click(object sender, RoutedEventArgs e) => NavigateCard(-1);
    private void Next_Click(object sender, RoutedEventArgs e) => NavigateCard(1);
    private void SaveCard_Click(object sender, RoutedEventArgs e) => SaveCard();
    private void DeleteCard_Click(object sender, RoutedEventArgs e) => DeleteCurrentCard();
    private void CopyCurrent_Click(object sender, RoutedEventArgs e) => CopyCurrent();
    private void CopyAll_Click(object sender, RoutedEventArgs e) => CopyAllDecksToClipboard();

    private void RefreshExport_Click(object sender, RoutedEventArgs e)
    {
        SaveCurrentEdits();
        ExportPreviewBox.Text = GetSelectedDeckAnkiText();
        SetStatus("Export preview refreshed.");
    }

    private void CopySelectedDeck_Click(object sender, RoutedEventArgs e)
    {
        string text = GetSelectedDeckAnkiText();

        if (string.IsNullOrWhiteSpace(text))
        {
            ShowToast("Selected deck has no cards.");
            return;
        }

        Clipboard.SetText(text);
        SetStatus("Selected deck copied.");
        ShowToast("Selected deck copied for Anki.");
    }

    private void CopyAllDecksToClipboard()
    {
        string text = GetAllDecksAnkiText();

        if (string.IsNullOrWhiteSpace(text))
        {
            ShowToast("No cards to copy.");
            return;
        }

        Clipboard.SetText(text);
        SetStatus("All decks copied.");
        ShowToast("All decks copied for Anki.");
    }

    private void ExportSelectedDeck_Click(object sender, RoutedEventArgs e)
    {
        SaveCurrentEdits();

        var deck = GetActiveDeck();

        if (deck is null || deck.Cards.Count == 0)
        {
            ShowToast("Selected deck has no cards.");
            return;
        }

        var sfd = new SaveFileDialog
        {
            Title = "Export selected deck",
            Filter = "Text file|*.txt",
            FileName = CleanFileName(deck.Name) + "_anki.txt"
        };

        if (sfd.ShowDialog() == true)
        {
            File.WriteAllText(sfd.FileName, GetDeckAnkiText(deck), Encoding.UTF8);
            ShowToast("Selected deck exported.");
        }
    }

    private void ExportAllDecks_Click(object sender, RoutedEventArgs e)
    {
        SaveCurrentEdits();

        string text = GetAllDecksAnkiText();

        if (string.IsNullOrWhiteSpace(text))
        {
            ShowToast("No cards to export.");
            return;
        }

        var sfd = new SaveFileDialog
        {
            Title = "Export all decks",
            Filter = "Text file|*.txt",
            FileName = "all_flashcards_anki.txt"
        };

        if (sfd.ShowDialog() == true)
        {
            File.WriteAllText(sfd.FileName, text, Encoding.UTF8);
            ShowToast("All decks exported.");
        }
    }

    private void StartStudySession(string deckId)
    {
        var deck = GetDeckById(deckId) ?? GetActiveDeck();

        if (deck is null || deck.Cards.Count == 0)
        {
            _studyQueue.Clear();
            _studyIndex = -1;
            _studySessionTotal = 0;
            StudyProgressText.Text = "No cards available.";
            StudyFrontText.Text = "Generate or import cards first.";
            StudyBackText.Text = "";
            StudyAnswerPanel.Visibility = Visibility.Collapsed;
            StudyCompletePanel.Visibility = Visibility.Collapsed;
            StudyHintText.Text = "";
            UpdateStudyProgress();
            UpdateStudyButtons();
            return;
        }

        _activeDeckId = deck.Id;
        _deckStore.ActiveDeckId = deck.Id;

        _studyQueue.Clear();

        var due = deck.Cards
            .Where(c => c.DueAt <= DateTime.UtcNow)
            .OrderBy(c => c.DueAt)
            .ToList();

        if (due.Count > 0)
            _studyQueue.AddRange(due);
        else
            _studyQueue.AddRange(deck.Cards.OrderBy(c => c.CreatedAt));

        _studyIndex = 0;
        _studySessionTotal = _studyQueue.Count;
        _answerShown = false;

        StudyCompletePanel.Visibility = Visibility.Collapsed;

        SaveDecks();
        RefreshAll();
        ShowStudyCard();
    }

    private void ShowStudyCard()
    {
        if (_studyIndex < 0 || _studyIndex >= _studyQueue.Count)
        {
            StudyProgressText.Text = "No cards.";
            StudyFrontText.Text = "No card selected.";
            StudyBackText.Text = "";
            StudyAnswerPanel.Visibility = Visibility.Collapsed;
            StudyHintText.Text = "";
            UpdateStudyProgress();
            UpdateStudyButtons();
            return;
        }

        var card = _studyQueue[_studyIndex];
        var deck = GetActiveDeck();

        int position = _studySessionTotal - _studyQueue.Count + 1;

        StudyProgressText.Text = deck is null
            ? $"Card {position} / {_studySessionTotal}"
            : $"{deck.Name} • Card {position} / {_studySessionTotal}";

        StudyFrontText.Text = card.Front;
        StudyBackText.Text = card.Back;
        StudyAnswerPanel.Visibility = _answerShown ? Visibility.Visible : Visibility.Collapsed;

        StudyHintText.Text = _answerShown
            ? "Choose how well you knew it."
            : "Try to answer before showing the back.";

        UpdateStudyProgress();
        UpdateStudyButtons();

        // Animate only when a fresh card front appears (start / next / previous),
        // not when the answer is simply revealed on the same card.
        if (!_answerShown)
            AnimateStudyCardIn();
    }

    // Subtle fade + horizontal slide when moving between study cards.
    private void AnimateStudyCardIn()
    {
        var slide = new TranslateTransform();
        StudyCardSurface.RenderTransform = slide;

        StudyCardSurface.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });

        slide.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(24, 0, TimeSpan.FromMilliseconds(280))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
    }

    // Gentle fade + downward slide when the answer is revealed.
    private void AnimateStudyAnswerIn()
    {
        var slide = new TranslateTransform();
        StudyAnswerPanel.RenderTransform = slide;

        StudyAnswerPanel.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });

        slide.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(240))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
    }

    private void UpdateStudyProgress()
    {
        double fraction = _studySessionTotal <= 0
            ? 0
            : (double)(_studySessionTotal - _studyQueue.Count) / _studySessionTotal;

        if (fraction < 0) fraction = 0;
        if (fraction > 1) fraction = 1;

        StudyProgFilledCol.Width = new GridLength(fraction, GridUnitType.Star);
        StudyProgEmptyCol.Width = new GridLength(1 - fraction, GridUnitType.Star);
    }

    private void UpdateStudyButtons()
    {
        bool hasCard = _studyIndex >= 0 && _studyIndex < _studyQueue.Count;
        bool canRate = hasCard && _answerShown;

        BtnShowAnswer.IsEnabled = hasCard && !_answerShown;
        BtnAgain.IsEnabled = canRate;
        BtnHard.IsEnabled = canRate;
        BtnGood.IsEnabled = canRate;
        BtnEasy.IsEnabled = canRate;
    }

    private void ShowAnswer_Click(object sender, RoutedEventArgs e)
    {
        if (_studyIndex < 0 || _studyIndex >= _studyQueue.Count)
            return;

        _answerShown = true;
        ShowStudyCard();
        AnimateStudyAnswerIn();
    }

    private void Again_Click(object sender, RoutedEventArgs e) => RateStudyCard("Again", TimeSpan.FromMinutes(10));
    private void Hard_Click(object sender, RoutedEventArgs e) => RateStudyCard("Hard", TimeSpan.FromDays(1));
    private void Good_Click(object sender, RoutedEventArgs e) => RateStudyCard("Good", TimeSpan.FromDays(3));
    private void Easy_Click(object sender, RoutedEventArgs e) => RateStudyCard("Easy", TimeSpan.FromDays(7));

    private void RateStudyCard(string rating, TimeSpan interval)
    {
        if (_studyIndex < 0 || _studyIndex >= _studyQueue.Count)
            return;

        var card = _studyQueue[_studyIndex];

        switch (rating)
        {
            case "Again":
                card.AgainCount++;
                break;
            case "Hard":
                card.HardCount++;
                break;
            case "Good":
                card.GoodCount++;
                _deckStore.Stats.SuccessfulReviews++;
                break;
            case "Easy":
                card.EasyCount++;
                _deckStore.Stats.SuccessfulReviews++;
                break;
        }

        card.DueAt = DateTime.UtcNow.Add(interval);
        card.Repetitions++;
        card.LastStudiedAt = DateTime.UtcNow;

        var deck = GetActiveDeck();

        if (deck is not null)
            deck.LastStudiedAt = DateTime.UtcNow;

        RegisterStudyToday();

        _deckStore.Stats.TotalReviews++;

        SaveDecks();

        _studyQueue.RemoveAt(_studyIndex);

        if (_studyQueue.Count == 0)
        {
            StudyProgressText.Text = "Session complete.";
            StudyFrontText.Text = "Great work. No more due cards in this session.";
            StudyBackText.Text = "";
            StudyAnswerPanel.Visibility = Visibility.Collapsed;
            StudyHintText.Text = "Go to Dashboard or Decks to continue.";
            _studyIndex = -1;

            StudyCompleteSubtitle.Text = deck is null
                ? $"You reviewed all {_studySessionTotal} cards in this session."
                : $"You reviewed all {_studySessionTotal} due cards in {deck.Name}.";
            StudyCompletePanel.Visibility = Visibility.Visible;

            UpdateStudyProgress();
            UpdateStudyButtons();
            RefreshAll();
            return;
        }

        if (_studyIndex >= _studyQueue.Count)
            _studyIndex = 0;

        _answerShown = false;
        ShowStudyCard();
        RefreshAll();
    }

    private void RegisterStudyToday()
    {
        DateTime today = DateTime.Today;
        DateTime last = _deckStore.Stats.LastStudyDate.ToLocalTime().Date;

        if (last != today)
        {
            if (last == today.AddDays(-1))
                _deckStore.Stats.CurrentStreak++;
            else
                _deckStore.Stats.CurrentStreak = 1;

            _deckStore.Stats.StudiedToday = 0;
            _deckStore.Stats.LastStudyDate = DateTime.UtcNow;
        }

        _deckStore.Stats.StudiedToday++;
    }

    private void ApplyCode_Click(object sender, RoutedEventArgs e)
    {
        if (_currentUser is null) return;

        string code = NormalizeCode(ApplyCodeBox.Text);
        var activation = GetActivationInfo(code);

        if (activation is null)
        {
            ShowToast("Invalid activation code.");
            return;
        }

        if (IsCodeUsed(code))
        {
            ShowToast("This activation code was already used.");
            return;
        }

        if (activation.Value.Lifetime)
        {
            _currentUser.SubscriptionExpiresAt = DateTime.MaxValue;
        }
        else
        {
            DateTime start = _currentUser.SubscriptionExpiresAt > DateTime.UtcNow
                ? _currentUser.SubscriptionExpiresAt
                : DateTime.UtcNow;

            _currentUser.SubscriptionExpiresAt = start.AddDays(activation.Value.Days);
        }

        _currentUser.Plan = activation.Value.Plan;

        _store.UsedCodes.Add(new UsedActivationCode
        {
            Code = code,
            UsedByEmail = _currentUser.Email,
            Plan = activation.Value.Plan,
            UsedAt = DateTime.UtcNow
        });

        SaveStore();
        RefreshAccountPage();
        UserSummaryText.Text = GetAccountSummary();

        ShowToast("Activation applied.");
    }

    private string BuildPrompt(string source)
    {
        return string.Join(Environment.NewLine, new[]
        {
            "You are an expert Anki flashcard creator for medical students.",
            "",
            "Create high-yield flashcards from the source material I provide.",
            "",
            "IMPORTANT OUTPUT RULES:",
            "- Return ONLY valid JSON.",
            "- Do not use markdown.",
            "- Do not use code fences.",
            "- Do not explain anything outside the JSON.",
            "- Use this exact JSON structure:",
            "[",
            "  {",
            "    \"front\": \"question or cloze text\",",
            "    \"back\": \"answer\",",
            "    \"tags\": \"Step1::Topic\"",
            "  }",
            "]",
            "",
            "FLASHCARD RULES:",
            "- One concept per card.",
            "- Make cards exam-focused and high-yield.",
            "- Avoid long paragraphs.",
            "- Use simple wording.",
            "- Do not copy long passages from the source.",
            "- If the mode is Cloze Deletion, put the cloze deletion in the front field.",
            "- Keep tags short and useful without spaces.",
            "",
            "OPTIONS:",
            "- Mode: " + SafeComboText(ModeCombo),
            "- Difficulty: " + SafeComboText(DifficultyCombo),
            "- Answer length: " + SafeComboText(AnswerLengthCombo),
            "- Number of cards: " + SafeComboText(CountCombo),
            "- Language: " + SafeComboText(LanguageCombo),
            "",
            "SOURCE MATERIAL:",
            source
        });
    }

    // Phase 8: flashcard generation goes through the OrbitLab backend proxy — the
    // desktop app holds no provider key and never contacts a provider directly.
    // User-facing errors never mention keys, tokens, endpoints, or providers.
    private async Task<string> CallAiAsync(string prompt)
    {
        string body = OrbitLabAiProxyClient.BuildChatBody(
            "You create JSON flashcards only. Return valid JSON only.",
            prompt, temperature: 0.2, maxTokens: 6000);

        var r = await _aiProxy.SendChatAsync(body, timeoutSeconds: 180, CancellationToken.None);

        switch (r.Outcome)
        {
            case AiProxyOutcome.NoSession:
                throw new Exception("Please sign in to your OrbitLab account to use AI features.");
            case AiProxyOutcome.Timeout:
                throw new Exception("AI took too long to respond. Please try again.");
            case AiProxyOutcome.Network:
                throw new Exception("Could not reach OrbitLab. Check your internet connection and try again.");
        }

        if (!r.IsSuccess)
            throw new Exception(r.Status switch
            {
                401 => "Your session has expired. Please log out and log in again.",
                403 => "Your OrbitLab subscription is not active for AI features.",
                429 => "You have reached today's AI limit. Please try again tomorrow.",
                >= 500 => "AI is temporarily unavailable. Please try again shortly.",
                _ => "AI could not complete the request. Please try again.",
            });

        string text = OrbitLabAiProxyClient.ExtractContent(r.Body);
        if (string.IsNullOrWhiteSpace(text))
            throw new Exception("AI returned no usable text. Please try again.");

        return text;
    }

    private static List<Flashcard> ParseFlashcards(string aiText)
    {
        string cleaned = aiText.Trim();

        cleaned = cleaned.Replace("```json", "", StringComparison.OrdinalIgnoreCase)
                         .Replace("```", "")
                         .Trim();

        int start = cleaned.IndexOf('[');
        int end = cleaned.LastIndexOf(']');

        if (start >= 0 && end > start)
            cleaned = cleaned.Substring(start, end - start + 1);

        try
        {
            using var doc = JsonDocument.Parse(cleaned);

            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (string possible in new[] { "cards", "flashcards", "data", "items" })
                {
                    if (doc.RootElement.TryGetProperty(possible, out var arr) &&
                        arr.ValueKind == JsonValueKind.Array)
                    {
                        return ParseJsonArray(arr);
                    }
                }

                return TryParseTabSeparated(aiText);
            }

            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                return ParseJsonArray(doc.RootElement);

            return TryParseTabSeparated(aiText);
        }
        catch
        {
            return TryParseTabSeparated(aiText);
        }
    }

    private static List<Flashcard> ParseJsonArray(JsonElement array)
    {
        var list = new List<Flashcard>();

        foreach (var el in array.EnumerateArray())
        {
            string front = GetPropertyText(el, "front", "Front", "question", "Question", "q");
            string back = GetPropertyText(el, "back", "Back", "answer", "Answer", "a");
            string tags = GetPropertyText(el, "tags", "Tags", "tag", "Tag");

            if (!string.IsNullOrWhiteSpace(front) && !string.IsNullOrWhiteSpace(back))
            {
                list.Add(new Flashcard
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Front = front.Trim(),
                    Back = back.Trim(),
                    Tags = string.IsNullOrWhiteSpace(tags) ? "AIFlashcards" : tags.Trim(),
                    CreatedAt = DateTime.UtcNow,
                    DueAt = DateTime.UtcNow
                });
            }
        }

        return list;
    }

    private static string GetPropertyText(JsonElement el, params string[] names)
    {
        if (el.ValueKind != JsonValueKind.Object)
            return "";

        foreach (var prop in el.EnumerateObject())
        {
            foreach (string name in names)
            {
                if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    if (prop.Value.ValueKind == JsonValueKind.String)
                        return prop.Value.GetString() ?? "";

                    if (prop.Value.ValueKind == JsonValueKind.Array)
                        return string.Join(" ", prop.Value.EnumerateArray().Select(x => x.ToString()));

                    return prop.Value.ToString();
                }
            }
        }

        return "";
    }

    private static List<Flashcard> TryParseTabSeparated(string text)
    {
        var list = new List<Flashcard>();

        var lines = text.Replace("\r\n", "\n")
                        .Replace("\r", "\n")
                        .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();

            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (line.StartsWith("Front", StringComparison.OrdinalIgnoreCase) &&
                line.Contains("Back", StringComparison.OrdinalIgnoreCase))
                continue;

            string[] parts = line.Split('\t');

            if (parts.Length >= 2)
            {
                list.Add(new Flashcard
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Front = parts[0].Trim(),
                    Back = parts[1].Trim(),
                    Tags = parts.Length >= 3 && !string.IsNullOrWhiteSpace(parts[2])
                        ? parts[2].Trim()
                        : "AIFlashcards",
                    CreatedAt = DateTime.UtcNow,
                    DueAt = DateTime.UtcNow
                });
            }
        }

        return list;
    }

    private void RefreshAll()
    {
        EnsureDefaultDeck();
        RefreshDeckCombos();
        RefreshDeckLists();
        RefreshCardLists();
        RefreshStats();
        RefreshStudyPage();
        RefreshAccountPage();
        RefreshExportPreview();
    }

    private void RefreshDeckCombos()
    {
        _suppressSelection = true;

        var decks = _deckStore.Decks.ToList();

        SetComboDecks(GenerateDeckCombo, decks);
        SetComboDecks(ImportDeckCombo, decks);
        SetComboDecks(StudyDeckCombo, decks);
        SetComboDecks(ExportDeckCombo, decks);
        SetComboDecks(MoveDeckCombo, decks);

        _suppressSelection = false;
    }

    private void SetComboDecks(ComboBox combo, List<StudyDeck> decks)
    {
        combo.ItemsSource = null;
        combo.ItemsSource = decks;

        int index = decks.FindIndex(d => d.Id == _activeDeckId);

        if (index < 0 && decks.Count > 0)
            index = 0;

        combo.SelectedIndex = index;
    }

    private void RefreshDeckLists()
    {
        _suppressSelection = true;

        DeckList.ItemsSource = null;
        DeckList.ItemsSource = _deckStore.Decks;

        int index = _deckStore.Decks.FindIndex(d => d.Id == _activeDeckId);

        if (index >= 0)
            DeckList.SelectedIndex = index;

        _suppressSelection = false;
    }

    private void RefreshCardLists()
    {
        _suppressSelection = true;

        var deck = GetActiveDeck();
        var filtered = GetFilteredCards(deck);

        ImportedList.ItemsSource = null;
        ImportedList.ItemsSource = deck?.Cards ?? new List<Flashcard>();

        CardsList.ItemsSource = null;
        CardsList.ItemsSource = filtered;

        ImportSummaryText.Text = deck is null
            ? "No deck selected."
            : $"{deck.Cards.Count} cards in {deck.Name}.";

        DeckSummaryText.Text = deck is null
            ? "No deck selected."
            : $"{deck.Name}: {deck.Cards.Count} cards • {deck.Cards.Count(c => c.DueAt <= DateTime.UtcNow)} due";

        SelectCurrentCardInLists();

        _suppressSelection = false;
    }

    private List<Flashcard> GetFilteredCards(StudyDeck? deck)
    {
        if (deck is null)
            return new List<Flashcard>();

        IEnumerable<Flashcard> query = deck.Cards;

        string search = SearchBox.Text.Trim();
        string tag = TagFilterBox.Text.Trim();

        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(c =>
                c.Front.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                c.Back.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                c.Tags.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(tag))
        {
            query = query.Where(c =>
                c.Tags.Contains(tag, StringComparison.OrdinalIgnoreCase));
        }

        if (DueOnlyCheck.IsChecked == true)
        {
            query = query.Where(c => c.DueAt <= DateTime.UtcNow);
        }

        return query.ToList();
    }

    private void SelectCurrentCardInLists()
    {
        var deck = GetActiveDeck();

        if (deck is null || string.IsNullOrWhiteSpace(_currentCardId))
            return;

        var card = deck.Cards.FirstOrDefault(c => c.Id == _currentCardId);

        if (card is null)
            return;

        ImportedList.SelectedItem = card;
        CardsList.SelectedItem = card;
    }

    private void RefreshStats()
    {
        int totalDecks = _deckStore.Decks.Count;
        int totalCards = _deckStore.Decks.Sum(d => d.Cards.Count);
        int due = _deckStore.Decks.Sum(d => d.Cards.Count(c => c.DueAt <= DateTime.UtcNow));
        int weak = _deckStore.Decks.Sum(d => d.Cards.Count(c => c.AgainCount > c.GoodCount + c.EasyCount && c.ReviewCount > 0));

        double accuracy = _deckStore.Stats.TotalReviews == 0
            ? 0
            : (_deckStore.Stats.SuccessfulReviews * 100.0 / _deckStore.Stats.TotalReviews);

        ResetStudiedTodayIfNeeded();

        StatsDecks.Text = totalDecks.ToString();
        StatsTotalCards.Text = totalCards.ToString();
        StatsDueCards.Text = due.ToString();
        StatsStudiedToday.Text = _deckStore.Stats.StudiedToday.ToString();
        StatsStreak.Text = _deckStore.Stats.CurrentStreak + " days";
        StatsAccuracy.Text = Math.Round(accuracy).ToString("0") + "%";
        StatsWeak.Text = weak.ToString();
    }

    private void ResetStudiedTodayIfNeeded()
    {
        if (_deckStore.Stats.LastStudyDate == DateTime.MinValue)
            return;

        DateTime today = DateTime.Today;
        DateTime last = _deckStore.Stats.LastStudyDate.ToLocalTime().Date;

        if (last != today)
        {
            _deckStore.Stats.StudiedToday = 0;
            SaveDecks();
        }
    }

    private void RefreshStudyPage()
    {
        if (_studyIndex >= 0 && _studyIndex < _studyQueue.Count)
            ShowStudyCard();
    }

    private void RefreshAccountPage()
    {
        AccountEmailText.Text = string.IsNullOrWhiteSpace(_currentUser?.Email) ? "—" : _currentUser!.Email;
        // Phase 3A UI/stub: show Commercial Beta entitlement wording, not the legacy
        // Monthly/expiry-date values. Account fields and persistence are unchanged.
        AccountPlanText.Text = Branding.CommercialBetaPlan;
        AccountExpiryText.Text = "Active";
    }

    private void RefreshExportPreview()
    {
        ExportPreviewBox.Text = GetSelectedDeckAnkiText();
    }

    private void UpdatePreview()
    {
        var card = FindCurrentCard();

        if (card is null)
        {
            CardCounterText.Text = "No card selected.";
            FrontBox.Text = "";
            BackBox.Text = "";
            TagsBox.Text = "";
            return;
        }

        var deck = GetActiveDeck();
        int index = deck?.Cards.FindIndex(c => c.Id == card.Id) ?? -1;
        int count = deck?.Cards.Count ?? 0;

        CardCounterText.Text = index >= 0
            ? $"Card {index + 1} / {count} • {deck?.Name}"
            : "Card selected";

        FrontBox.Text = card.Front;
        BackBox.Text = card.Back;
        TagsBox.Text = card.Tags;
    }

    private void NavigateCard(int direction)
    {
        var deck = GetActiveDeck();

        if (deck is null || deck.Cards.Count == 0)
        {
            ShowToast("No cards in selected deck.");
            return;
        }

        SaveCurrentEdits();

        int index = deck.Cards.FindIndex(c => c.Id == _currentCardId);

        if (index < 0)
            index = 0;
        else
            index += direction;

        if (index < 0)
            index = 0;

        if (index >= deck.Cards.Count)
            index = deck.Cards.Count - 1;

        _currentCardId = deck.Cards[index].Id;

        UpdatePreview();
        RefreshCardLists();
    }

    private void SaveCurrentEdits()
    {
        var card = FindCurrentCard();

        if (card is null)
            return;

        card.Front = FrontBox.Text.Trim();
        card.Back = BackBox.Text.Trim();
        card.Tags = TagsBox.Text.Trim();

        SaveDecks();
    }

    private void SaveCard()
    {
        SaveCurrentEdits();
        RefreshAll();
        SetStatus("Card saved.");
    }

    private void DeleteCurrentCard()
    {
        var deck = GetActiveDeck();
        var card = FindCurrentCard();

        if (deck is null || card is null)
        {
            ShowToast("No card selected.");
            return;
        }

        if (MessageBox.Show("Delete this card?", "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        int index = deck.Cards.FindIndex(c => c.Id == card.Id);
        deck.Cards.Remove(card);

        if (deck.Cards.Count == 0)
        {
            _currentCardId = "";
        }
        else
        {
            if (index >= deck.Cards.Count)
                index = deck.Cards.Count - 1;

            _currentCardId = deck.Cards[index].Id;
        }

        SaveDecks();
        RefreshAll();
        UpdatePreview();
    }

    private void CopyCurrent()
    {
        SaveCurrentEdits();

        var card = FindCurrentCard();

        if (card is null)
        {
            ShowToast("No card selected.");
            return;
        }

        Clipboard.SetText(ToAnkiLine(card));
        SetStatus("Current card copied.");
    }

    private string GetSelectedDeckAnkiText()
    {
        var deck = GetDeckById(GetSelectedDeckIdFromCombo(ExportDeckCombo)) ?? GetActiveDeck();

        if (deck is null)
            return "";

        return GetDeckAnkiText(deck);
    }

    private string GetDeckAnkiText(StudyDeck deck)
    {
        return string.Join(Environment.NewLine, deck.Cards.Select(ToAnkiLine));
    }

    private string GetAllDecksAnkiText()
    {
        var lines = new List<string>();

        foreach (var deck in _deckStore.Decks)
        {
            lines.AddRange(deck.Cards.Select(ToAnkiLine));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string ToAnkiLine(Flashcard card)
    {
        return CleanField(card.Front) + "\t" + CleanField(card.Back) + "\t" + CleanField(card.Tags);
    }

    private static string CleanField(string value)
    {
        return value.Replace("\t", " ")
                    .Replace("\r\n", "<br>")
                    .Replace("\n", "<br>")
                    .Replace("\r", "<br>")
                    .Trim();
    }

    private StudyDeck? GetActiveDeck()
    {
        return GetDeckById(_activeDeckId) ?? _deckStore.Decks.FirstOrDefault();
    }

    private StudyDeck? GetDeckById(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        return _deckStore.Decks.FirstOrDefault(d => d.Id == id);
    }

    private Flashcard? FindCurrentCard()
    {
        var deck = GetActiveDeck();

        if (deck is null)
            return null;

        if (string.IsNullOrWhiteSpace(_currentCardId))
        {
            var first = deck.Cards.FirstOrDefault();

            if (first is not null)
                _currentCardId = first.Id;

            return first;
        }

        return deck.Cards.FirstOrDefault(c => c.Id == _currentCardId);
    }

    private string GetSelectedDeckIdFromCombo(ComboBox combo)
    {
        return combo.SelectedItem is StudyDeck deck ? deck.Id : "";
    }

    private void EnsureDefaultDeck()
    {
        if (_deckStore.Decks.Count == 0)
        {
            var deck = new StudyDeck
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = "Default Deck",
                CreatedAt = DateTime.UtcNow
            };

            _deckStore.Decks.Add(deck);
            _deckStore.ActiveDeckId = deck.Id;
        }

        if (string.IsNullOrWhiteSpace(_deckStore.ActiveDeckId) ||
            _deckStore.Decks.All(d => d.Id != _deckStore.ActiveDeckId))
        {
            _deckStore.ActiveDeckId = _deckStore.Decks.First().Id;
        }

        _activeDeckId = _deckStore.ActiveDeckId;
    }

    private void LoadStore()
    {
        try
        {
            if (!File.Exists(StorePath))
            {
                _store = new LocalStore();
                return;
            }

            string json = File.ReadAllText(StorePath);
            _store = JsonSerializer.Deserialize<LocalStore>(json) ?? new LocalStore();
        }
        catch
        {
            _store = new LocalStore();
        }
    }

    private void SaveStore()
    {
        File.WriteAllText(StorePath, JsonSerializer.Serialize(_store, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
    }

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                _settings = new AppSettings();
                return;
            }

            string json = File.ReadAllText(SettingsPath);
            _settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();

            // Phase 8 migration: purge any legacy locally-saved provider credentials.
            // AI now runs entirely through the OrbitLab backend; the desktop client
            // must never hold a provider key, model, or base URL. Only these
            // provider fields are cleared — unrelated preferences are untouched.
            PurgeLegacyProviderCredentials();
        }
        catch
        {
            _settings = new AppSettings();
        }
    }

    // Deletes any leftover Z.ai/provider credentials from the on-disk settings.
    // Never logs, displays, or returns the old value. Safe to call repeatedly.
    private void PurgeLegacyProviderCredentials()
    {
        bool changed = !string.IsNullOrEmpty(_settings.ApiKey)
                       || !string.IsNullOrEmpty(_settings.BaseUrl)
                       || !string.IsNullOrEmpty(_settings.Model)
                       || !string.IsNullOrEmpty(_settings.ApiProvider);
        _settings.ApiKey = "";
        _settings.BaseUrl = "";
        _settings.Model = "";
        _settings.ApiProvider = "";
        if (changed)
        {
            try { SaveSettings(); } catch { /* purge is best-effort; never blocks startup */ }
        }
    }

    private void SaveSettings()
    {
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(_settings, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
    }

    private void LoadDecks()
    {
        try
        {
            if (!File.Exists(DecksPath))
            {
                _deckStore = new DeckStore();
                return;
            }

            string json = File.ReadAllText(DecksPath);
            _deckStore = JsonSerializer.Deserialize<DeckStore>(json) ?? new DeckStore();
        }
        catch
        {
            _deckStore = new DeckStore();
        }
    }

    private void SaveDecks()
    {
        _deckStore.ActiveDeckId = _activeDeckId;

        File.WriteAllText(DecksPath, JsonSerializer.Serialize(_deckStore, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
    }

    // =====================================================================
    // Research Lab (Phase 1) — local storage, project list, create form,
    // and the project-dashboard shell. No AI / statistics / manuscript
    // logic here yet; those arrive in later phases.
    // =====================================================================

    private void BuildResearchTabMap()
    {
        _researchTabs.Clear();
        _researchTabs.Add((SegOverview, TabOverview));
        _researchTabs.Add((SegAiRec, TabAiRec));
        _researchTabs.Add((SegProposal, TabProposal));
        _researchTabs.Add((SegDataExt, TabDataExt));
        _researchTabs.Add((SegStats, TabStats));
        _researchTabs.Add((SegResults, TabResults));
        _researchTabs.Add((SegManuscript, TabManuscript));
        _researchTabs.Add((SegNotes, TabNotes));
    }

    private void LoadResearch()
    {
        try
        {
            if (!File.Exists(ResearchPath))
            {
                _researchData = new ResearchLabData();
                _researchLoadedCleanly = true;   // a genuinely empty store is a clean state
                return;
            }

            string json = File.ReadAllText(ResearchPath);
            _researchData = JsonSerializer.Deserialize<ResearchLabData>(json) ?? new ResearchLabData();
            _researchData.Projects ??= new List<ResearchProject>();
            EnsureProjectUuids();
            _researchLoadedCleanly = true;   // Phase 10: gates draft reconciliation
        }
        catch
        {
            // Corrupt or unreadable file: fail safely with an empty list and
            // never touch the user's flashcard data. _researchLoadedCleanly stays
            // false so draft reconciliation never releases registrations for
            // projects that still exist on disk but failed to load.
            _researchData = new ResearchLabData();
            SetStatus("Research projects file could not be read — starting with an empty list.");
        }
    }

    // Phase 9: every project needs a stable opaque UUID (the server-side project identity
    // for entitlement accounting — never the title). This runs on EVERY load and repairs
    // only projects whose Id is empty, malformed, or a duplicate; valid unique ids are left
    // untouched. It is NOT gated by a marker file, so an old backup imported at any time is
    // always repaired and repeated launches never disturb valid ids. See ProjectIdNormalizer.
    private void EnsureProjectUuids()
    {
        if (ProjectIdNormalizer.NormalizeProjectIds(_researchData.Projects))
            SaveResearch();   // atomic (temp→replace) + last-good backup; never corrupts research content
    }

    private void SaveResearch()
    {
        try
        {
            // Phase 10: progress is always derived from the persisted artifacts (with the
            // stored value as a monotonic floor) — one hook here keeps every saved file
            // accurate no matter which workflow action triggered the save.
            foreach (var p in _researchData.Projects)
                ResearchProjectProgress.Touch(p);
            string json = JsonSerializer.Serialize(_researchData, new JsonSerializerOptions { WriteIndented = true });
            // Phase 4: atomic write (temp -> replace) + last-good backup so a crash mid-write
            // never corrupts the main store. Local-first only; no rows logged, no network.
            WriteJsonAtomic(ResearchPath, json, ResearchLastGoodPath);
            ScheduleAutosave();
        }
        catch (Exception ex)
        {
            SetSaveStatus(SaveState.Failed);
            ShowToast("Could not save research projects: " + ex.Message);
        }
    }

    // ===== Phase 4: local autosave + crash recovery (local-first; no backend/cloud/network) =====

    // Write JSON without risking the existing file: write a temp, keep a last-good copy of the
    // current file, then atomically swap temp -> destination. If the swap throws, the original
    // file is left intact (no corruption).
    private static void WriteJsonAtomic(string path, string json, string? lastGoodPath)
    {
        string dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        string tmp = path + ".tmp";

        File.WriteAllText(tmp, json, new System.Text.UTF8Encoding(false));   // 1) write to temp

        if (File.Exists(path))
        {
            if (lastGoodPath is not null)
            {
                try { File.Copy(path, lastGoodPath, overwrite: true); } catch { /* backup is best-effort */ }
            }
            File.Replace(tmp, path, null);                                   // 2) atomic swap temp -> main
        }
        else
        {
            File.Move(tmp, path);
        }
    }

    // Debounced: coalesce rapid changes into one autosave write ~1.2s after activity settles.
    private void ScheduleAutosave()
    {
        SetSaveStatus(SaveState.Saving);
        _autosaveTimer?.Stop();
        _autosaveTimer?.Start();
    }

    private void AutosaveTick(object? sender, EventArgs e)
    {
        _autosaveTimer?.Stop();
        try
        {
            string json = JsonSerializer.Serialize(_researchData, new JsonSerializerOptions { WriteIndented = true });
            WriteJsonAtomic(ResearchAutosavePath, json, null);
            SetSaveStatus(SaveState.Saved);
        }
        catch
        {
            SetSaveStatus(SaveState.Failed);
        }
    }

    private void SetSaveStatus(SaveState state)
    {
        if (SaveStatusText is null) return;   // shell not built yet
        switch (state)
        {
            case SaveState.Saving:
                SaveStatusText.Text = "Saving…";
                SaveStatusText.Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush");
                SaveStatusText.Visibility = Visibility.Visible;
                break;
            case SaveState.Saved:
                SaveStatusText.Text = "Saved locally";
                SaveStatusText.Foreground = (System.Windows.Media.Brush)FindResource("SuccessBrush");
                SaveStatusText.Visibility = Visibility.Visible;
                break;
            case SaveState.Failed:
                SaveStatusText.Text = "Save failed";
                SaveStatusText.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
                SaveStatusText.Visibility = Visibility.Visible;
                break;
            default:
                SaveStatusText.Visibility = Visibility.Collapsed;
                break;
        }
    }

    // On launch (post-login): an autosave file left behind means the previous session did not
    // close cleanly. If it is newer than the main store, offer to restore it. Never auto-overwrites.
    private void CheckAutosaveRecovery()
    {
        if (_autosaveRecoveryChecked) return;
        _autosaveRecoveryChecked = true;

        try
        {
            if (!File.Exists(ResearchAutosavePath))
                return;   // clean previous shutdown — nothing to recover

            DateTime autosaveTime = File.GetLastWriteTimeUtc(ResearchAutosavePath);
            DateTime mainTime = File.Exists(ResearchPath) ? File.GetLastWriteTimeUtc(ResearchPath) : DateTime.MinValue;

            string autoJson = File.ReadAllText(ResearchAutosavePath);
            string mainJson = File.Exists(ResearchPath) ? File.ReadAllText(ResearchPath) : "";

            // Only offer recovery when the autosave is genuinely newer AND different from the main
            // store — avoids spurious prompts when the two are identical.
            if (autosaveTime > mainTime && !string.Equals(autoJson, mainJson, StringComparison.Ordinal))
            {
                var choice = MessageBox.Show(
                    "We found a newer autosaved version of your local research data. Restore it?\n\n" +
                    "Yes — Restore the autosaved version.\n" +
                    "No — Keep your current saved version.",
                    Branding.ProductName, MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (choice == MessageBoxResult.Yes)
                {
                    WriteJsonAtomic(ResearchPath, autoJson, ResearchLastGoodPath);   // keeps a last-good of the old main
                    LoadResearch();
                    SetStatus("Restored autosaved research data.");
                }
            }

            // Either choice clears the marker so we do not prompt again.
            try { File.Delete(ResearchAutosavePath); } catch { }
        }
        catch
        {
            // Recovery must never block startup.
        }
    }

    // Clean shutdown: flush the main store atomically and clear the autosave marker so the next
    // launch does not offer a stale recovery. Never blocks close.
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        try
        {
            _autosaveTimer?.Stop();
            try
            {
                string json = JsonSerializer.Serialize(_researchData, new JsonSerializerOptions { WriteIndented = true });
                WriteJsonAtomic(ResearchPath, json, ResearchLastGoodPath);
            }
            catch { /* best-effort final save */ }

            try { if (File.Exists(ResearchAutosavePath)) File.Delete(ResearchAutosavePath); } catch { }
        }
        catch { /* never block close */ }

        base.OnClosing(e);
    }

    private async void ResearchPage_Click(object sender, RoutedEventArgs e) => await EnterResearchLabAsync();

    // Single entry point for opening Research Lab (sidebar + dashboard quick action).
    private async Task EnterResearchLabAsync()
    {
        CreateProjectOverlay.Visibility = Visibility.Collapsed;
        DeleteConfirmOverlay.Visibility = Visibility.Collapsed;
        RefreshResearchProjects();
        ShowPage(PageResearch);
        // Phase 9: once-per-account Telegram prompt on first entry for an active subscriber.
        await MaybeShowTelegramPromptAsync();
    }

    private void RefreshResearchProjects()
    {
        // Phase 10: every card shows the derived workflow progress, not a stale counter.
        foreach (var p in _researchData.Projects)
            ResearchProjectProgress.Touch(p);

        var projects = _researchData.Projects
            .OrderByDescending(p => p.UpdatedAt)
            .ToList();

        ResearchCardsHost.ItemsSource = null;
        ResearchCardsHost.ItemsSource = projects;

        bool any = projects.Count > 0;
        ResearchCardsHost.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        ResearchEmptyState.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OpenCreateProject_Click(object sender, RoutedEventArgs e)
    {
        // Phase 10: creating a project registers a server-side DRAFT that occupies one
        // plan position (released if the draft is deleted before its first analysis;
        // permanent once descriptive statistics complete). The registration itself
        // happens on Create — this just opens the form.
        if (!LicenseAllowsProtectedAction(out string gateMessage))
        {
            ShowToast(gateMessage);
            return;
        }

        ResetCreateProjectForm();
        CreateProjectOverlay.Visibility = Visibility.Visible;
        FadeIn(CreateProjectOverlay, 160);
        RpTitleBox.Focus();
    }

    private void ResetCreateProjectForm()
    {
        RpTitleBox.Text = "";
        RpSpecialtyBox.Text = "";
        RpAimBox.Text = "";
        RpPopulationBox.Text = "";
        RpSettingBox.Text = "";
        RpTimePeriodBox.Text = "";
        RpNotesBox.Text = "";

        RpStudyTypeCombo.SelectedIndex = 7; // Not sure
        RpDataCombo.SelectedIndex = 4;      // No data yet

        RpOutProposal.IsChecked = false;
        RpOutIntroduction.IsChecked = false;
        RpOutMethods.IsChecked = false;
        RpOutExtraction.IsChecked = false;
        RpOutStatistics.IsChecked = false;
        RpOutManuscript.IsChecked = false;
        RpOutAbstract.IsChecked = false;
        RpOutTables.IsChecked = false;

        RpValidationText.Text = "";
        RpValidationText.Visibility = Visibility.Collapsed;
    }

    private void CancelCreateProject_Click(object sender, RoutedEventArgs e)
    {
        CreateProjectOverlay.Visibility = Visibility.Collapsed;
    }

    private static string ComboText(ComboBox combo, string fallback)
        => (combo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? fallback;

    private List<string> CollectDesiredOutputs()
    {
        var outputs = new List<string>();
        if (RpOutProposal.IsChecked == true) outputs.Add("Proposal");
        if (RpOutIntroduction.IsChecked == true) outputs.Add("Introduction");
        if (RpOutMethods.IsChecked == true) outputs.Add("Methods");
        if (RpOutExtraction.IsChecked == true) outputs.Add("Data Extraction Sheet");
        if (RpOutStatistics.IsChecked == true) outputs.Add("Statistics");
        if (RpOutManuscript.IsChecked == true) outputs.Add("Manuscript");
        if (RpOutAbstract.IsChecked == true) outputs.Add("Abstract");
        if (RpOutTables.IsChecked == true) outputs.Add("Tables");
        return outputs;
    }

    private bool _createProjectBusy;   // double-click guard for the async registration

    private async void CreateProject_Click(object sender, RoutedEventArgs e)
    {
        if (_createProjectBusy) return;

        // Safety re-check of the active license.
        if (!LicenseAllowsProtectedAction(out string gateMessage))
        {
            CreateProjectOverlay.Visibility = Visibility.Collapsed;
            ShowToast(gateMessage);
            return;
        }

        string title = RpTitleBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            RpValidationText.Text = "Please enter a project title to continue.";
            RpValidationText.Visibility = Visibility.Visible;
            RpTitleBox.Focus();
            return;
        }

        // Phase 10: a new project must REGISTER a server-side draft (opaque UUID only —
        // the title and every other detail stay local) before it exists locally. The
        // draft occupies one plan position; the backend rejects registration when the
        // allowance is full, and an unreachable backend blocks creation entirely so an
        // unregistered local project can never appear.
        var project = new ResearchProject
        {
            Title = title,
            Specialty = RpSpecialtyBox.Text.Trim(),
            StudyType = ComboText(RpStudyTypeCombo, "Not sure"),
            Aim = RpAimBox.Text.Trim(),
            Population = RpPopulationBox.Text.Trim(),
            Setting = RpSettingBox.Text.Trim(),
            TimePeriod = RpTimePeriodBox.Text.Trim(),
            AvailableDataType = ComboText(RpDataCombo, "No data yet"),
            DesiredOutputs = CollectDesiredOutputs(),
            Notes = RpNotesBox.Text.Trim(),
            CurrentStage = "Project created",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _createProjectBusy = true;
        try
        {
            if (AppConfig.IsBackendConfigured)
            {
                if (_license is null || string.IsNullOrEmpty(_license.Token))
                {
                    ShowToast("Please log in to your OrbitLab account to create a project.");
                    return;
                }
                var reg = await LicenseApiClient.RegisterProjectAsync(_license.Token, project.Id);
                if (!reg.Ok)
                {
                    if (reg.Error?.Code == "project_limit_reached")
                    {
                        int limit = _overview?.Usage?.Limit ?? 1;
                        RpValidationText.Text = reg.Error.Message.Length > 0
                            ? reg.Error.Message
                            : $"Your plan allows {limit} project{(limit == 1 ? "" : "s")} for this subscription cycle. You are currently using {limit} of {limit}.";
                    }
                    else if (reg.Error?.Code == "not_entitled")
                    {
                        RpValidationText.Text = "Your OrbitLab subscription is not active. Please contact support or renew your Commercial Beta access.";
                    }
                    else
                    {
                        RpValidationText.Text = "OrbitLab needs to reach the server to create a new project. Check your connection and try again.";
                    }
                    RpValidationText.Visibility = Visibility.Visible;
                    return;   // no orphan local project is ever created
                }
                _ = RefreshAccountOverviewAsync();   // plan card reflects the new draft
            }

            _researchData.Projects.Add(project);
            SaveResearch();

            CreateProjectOverlay.Visibility = Visibility.Collapsed;
            RefreshResearchProjects();
            ShowToast($"Research project created: {project.Title}");
        }
        finally { _createProjectBusy = false; }
    }

    private void ContinueProject_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string id) return;

        var project = _researchData.Projects.FirstOrDefault(p => p.Id == id);
        if (project is null)
        {
            ShowToast("That project could not be found.");
            RefreshResearchProjects();
            return;
        }

        OpenProject(project);
    }

    private void OpenProject(ResearchProject project)
    {
        _openResearchId = project.Id;

        PopulateOverview(project);
        DashNotesBox.Text = project.Notes;

        PopulateAiSnapshot(project);
        RenderRecommendations(project.Recommendations);
        PopulatePlanEditor(project);
        PopulateProposalEditor(project);
        UpdateProposalPlanSource(project);
        UpdateOverviewProgress(project);
        UpdateGenerateRecButton(project);
        UpdateAiRecImportedBadge(project);
        UpdateResearchAiAvailability();
        LoadExtractionSheet(project);

        // Always land on Overview when a project opens.
        SwitchResearchTab(SegOverview);
        ShowPage(PageResearchDashboard);
    }

    private ResearchProject? CurrentResearchProject()
        => _researchData.Projects.FirstOrDefault(p => p.Id == _openResearchId);

    private void PopulateOverview(ResearchProject p)
    {
        DashTitleText.Text = p.Title;
        DashSubtitleText.Text = $"{p.SpecialtyDisplay}  •  {p.StudyTypeDisplay}";

        OvTitle.Text = p.Title;
        OvSpecialty.Text = p.SpecialtyDisplay;
        OvStudyType.Text = p.StudyTypeDisplay;
        OvAim.Text = string.IsNullOrWhiteSpace(p.Aim) ? "—" : p.Aim;
        OvPopulation.Text = string.IsNullOrWhiteSpace(p.Population) ? "—" : p.Population;
        OvSetting.Text = string.IsNullOrWhiteSpace(p.Setting) ? "—" : p.Setting;
        OvTimePeriod.Text = string.IsNullOrWhiteSpace(p.TimePeriod) ? "—" : p.TimePeriod;
        OvStage.Text = p.StageDisplay;
    }

    private void PopulateAiSnapshot(ResearchProject p)
    {
        AiSnapTitle.Text = string.IsNullOrWhiteSpace(p.Title) ? "—" : p.Title;
        AiSnapSpecialty.Text = p.SpecialtyDisplay;
        AiSnapStudyType.Text = p.StudyTypeDisplay;
        AiSnapAim.Text = string.IsNullOrWhiteSpace(p.Aim) ? "—" : p.Aim;
        AiSnapPopulation.Text = string.IsNullOrWhiteSpace(p.Population) ? "—" : p.Population;
        AiSnapSetting.Text = string.IsNullOrWhiteSpace(p.Setting) ? "—" : p.Setting;
        AiSnapData.Text = string.IsNullOrWhiteSpace(p.AvailableDataType) ? "—" : p.AvailableDataType;
    }

    private async void DeleteProject_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string id) return;

        var project = _researchData.Projects.FirstOrDefault(p => p.Id == id);
        if (project is null) return;

        // Phase 9: a project whose first-run finalization is still being recorded must not be
        // deleted until that record resolves (deleting it would strand an in-flight reservation).
        // An UNRECOVERABLE pending result (allowance full) never counted, so deletion proceeds as
        // a never-counted delete — the on-hold result is discarded when the project is removed.
        if (HasBlockingPending(project.Id))
        {
            await RetryPendingFinalizationsAsync();
            if (HasBlockingPending(project.Id))
            {
                ShowToast("OrbitLab is still finishing recording this project's first analysis. Please wait until you're back online, then try deleting it.");
                return;
            }
        }

        // Phase 9: the deletion warning depends on the project's server-authoritative
        // usage relationship to the CURRENT subscription cycle.
        //  * never_counted           → normal irreversible-deletion confirmation
        //  * counted_in_current_cycle → strong warning: deleting does NOT restore the slot
        //  * counted_in_previous_cycle→ deleting does not affect the current cycle
        // A project that never ran descriptive statistics locally could never have been
        // counted, so it is safe to treat as never_counted even offline.
        string title = "Delete project?";
        string message = "This project and its locally stored research data will be permanently deleted. This action cannot be undone.\n\n" +
                         "Because this project hasn't been counted toward your plan yet, deleting it frees its project position.";
        string button = "Delete project";

        bool locallyNeverRan = project.DescriptiveStatistics is null;
        if (!locallyNeverRan)
        {
            var state = await GetProjectServerStateAsync(project.Id);
            if (state is null)
            {
                // Could not verify impact — do not risk a false message. Ask to retry.
                ShowToast("OrbitLab couldn't verify how this project affects your plan. Check your connection and try again before deleting it.");
                return;
            }
            switch (state.State)
            {
                case "counted_in_current_cycle":
                    title = "Delete a counted project?";
                    message = "This project has already been counted toward your current plan's project allowance.\n\n" +
                              "Deleting it will permanently remove the project and its locally stored research data, but it will not restore the project slot during the current subscription cycle.\n\n" +
                              "You will regain a fresh allowance only when a new subscription cycle begins.";
                    button = "Delete anyway";
                    break;
                case "counted_in_previous_cycle":
                    title = "Delete project?";
                    message = "This project was counted during a previous subscription cycle.\n\n" +
                              "Deleting it will permanently remove the project and its locally stored research data. It will not affect the project allowance of your current subscription cycle.";
                    button = "Delete project";
                    break;
                    // never_counted → defaults above
            }
        }

        _pendingDeleteResearchId = id;
        DeleteConfirmTitle.Text = title;
        DeleteConfirmText.Text = message;
        DeleteConfirmButton.Content = button;
        DeleteConfirmOverlay.Visibility = Visibility.Visible;
        FadeIn(DeleteConfirmOverlay, 150);
    }

    private void CancelDelete_Click(object sender, RoutedEventArgs e)
    {
        _pendingDeleteResearchId = "";
        DeleteConfirmOverlay.Visibility = Visibility.Collapsed;
    }

    private async void ConfirmDelete_Click(object sender, RoutedEventArgs e)
    {
        DeleteConfirmOverlay.Visibility = Visibility.Collapsed;

        if (string.IsNullOrEmpty(_pendingDeleteResearchId)) return;

        var project = _researchData.Projects.FirstOrDefault(p => p.Id == _pendingDeleteResearchId);
        string deletedId = _pendingDeleteResearchId;
        _pendingDeleteResearchId = "";
        if (project is null) return;

        _researchData.Projects.Remove(project);
        DeleteDatasetCopy(project.Id);   // remove the stored statistics dataset copy
        DiscardPendingFinalization(deletedId);   // drop any on-hold result so a deleted project is never later counted
        SaveResearch();
        RefreshResearchProjects();
        ShowToast("Research project deleted.");

        // Phase 9: record local deletion as metadata (best-effort). The server ledger
        // row is RETAINED — deletion never restores the consumed allowance.
        // Phase 10: also release the draft registration. For a never-counted draft this
        // frees its plan position; for a counted project the draft was already converted
        // and the backend correctly refuses to release it (no slot is ever restored).
        if (_license is not null && AppConfig.IsBackendConfigured)
        {
            try { await LicenseApiClient.MarkProjectDeletedAsync(_license.Token, deletedId); } catch { }
            try { await LicenseApiClient.ReleaseDraftAsync(_license.Token, deletedId); } catch { }
            _ = RefreshAccountOverviewAsync();
        }
    }

    private void ResearchTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button)
            SwitchResearchTab(button);
    }

    private void SwitchResearchTab(Button active)
    {
        var normal = (Style)FindResource("SegTab");
        var activeStyle = (Style)FindResource("SegTabActive");

        foreach (var (button, panel) in _researchTabs)
        {
            bool isActive = ReferenceEquals(button, active);
            button.Style = isActive ? activeStyle : normal;
            panel.Visibility = isActive ? Visibility.Visible : Visibility.Collapsed;
        }

        // Keep the Proposal tab's plan-source status current whenever it opens.
        if (ReferenceEquals(active, SegProposal) && CurrentResearchProject() is { } proj)
            UpdateProposalPlanSource(proj);

        // Refresh the extraction tab's chips/target when it opens (the proposal
        // or plan may have changed since the project was first loaded).
        if (ReferenceEquals(active, SegDataExt) && CurrentResearchProject() is { } dp)
            RefreshExtractionChips(dp);

        // Statistics/Results recalculate readiness and staleness on every open,
        // so the dashboards always describe the CURRENT sheet and dataset.
        // Statistics always opens on its default Descriptive view.
        if (ReferenceEquals(active, SegStats)) { SetRecommendedView(false); RefreshStatisticsTab(); }
        if (ReferenceEquals(active, SegResults)) RefreshResultsTab();

        // Report Builder rebuilds its draft from the CURRENT project state every
        // time the Manuscript tab opens, so it always reflects the latest data,
        // descriptive stats, and computed results.
        if (ReferenceEquals(active, SegManuscript)) RefreshManuscriptTab();
    }

    // =====================================================================
    // Report Builder (Manuscript tab) — deterministic, local, aggregate-only.
    // Assembles project metadata, extraction variables, dataset summary,
    // descriptive statistics, and computed results (plus current-session
    // manuscript narratives where a live typed result is available) into a
    // TXT/Markdown draft via the headless ResearchLabReportBuilder. No AI, no
    // network, no new statistics — it only arranges already-computed values.
    // =====================================================================
    private ResearchLabReportBuilderResult? _lastReport;

    // Include/exclude selection state for the computed-results checklist. Session-
    // and project-scoped: selections persist while the app stays open on the same
    // project; opening a different project resets to "all included". A new result
    // is included by default unless the user has explicitly cleared all.
    private readonly System.Collections.ObjectModel.ObservableCollection<ReportResultChoice> _reportChoices = new();
    private string? _reportChoicesProjectId;
    private bool _reportClearedAll;

    // Display/selection model for one computed result in the Manuscript checklist.
    // No participant data — only the aggregate display fields already on the row.
    public sealed class ReportResultChoice : System.ComponentModel.INotifyPropertyChanged
    {
        public string Id { get; set; } = "";
        private bool _isIncluded = true;
        public bool IsIncluded { get => _isIncluded; set { if (_isIncluded != value) { _isIncluded = value; OnPc(nameof(IsIncluded)); } } }
        private string _title = "";
        public string Title { get => _title; set { if (_title != value) { _title = value; OnPc(nameof(Title)); } } }
        private string _detail = "";
        public string Detail { get => _detail; set { if (_detail != value) { _detail = value; OnPc(nameof(Detail)); } } }
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        private void OnPc(string n) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(n));
    }

    // Builds the report for the open project, supplying manuscript narratives for
    // any current-session computed rows that still carry a live typed result
    // (keyed by row Id, which equals the saved result Id via ToSaved/FromSaved).
    // Rows reloaded from disk after a restart have no LiveResult, so those results
    // fall back to the Re-run prompt inside the composer.
    private ResearchLabReportBuilderResult? BuildCurrentReport()
    {
        var p = CurrentResearchProject();
        if (p is null) return null;

        var narratives = new Dictionary<string, ResearchLabNarrativeResult>();
        if (_computedResultsProjectId == p.Id)
        {
            foreach (var row in _computedRows)
                if (row.LiveResult is not null)
                    narratives[row.Id] = ResearchLabNarrativeGenerator.Generate(row.LiveResult, row.IsStale);
        }

        var options = new ResearchLabReportBuilderOptions
        {
            CurrentFingerprint = CurrentStatisticsFingerprint(p),
            Style = RptAppendixCheck.IsChecked == true
                ? ReportStyle.FullTechnicalAppendix
                : ReportStyle.ManuscriptSummary,
            // Only the checked results appear. SyncReportChoices always runs before
            // this (in RefreshManuscriptTab), so _reportChoices reflects the project.
            IncludedComputedResultIds = _reportChoices.Where(c => c.IsIncluded).Select(c => c.Id).ToHashSet(StringComparer.Ordinal)
        };
        return ResearchLabReportBuilder.Build(p, narratives, options);
    }

    private void RptAppendix_Changed(object sender, RoutedEventArgs e)
    {
        // Toggling the appendix option just re-renders the preview from current data.
        if (CurrentResearchProject() is null) return;
        RefreshManuscriptTab();
    }

    // Sets the preview text and resets the scroll/caret to the very top so a
    // rebuilt report never appears mid-scroll. Does not steal focus.
    private void SetReportPreview(string text)
    {
        RptPreview.Text = text;
        RptPreview.CaretIndex = 0;
        RptPreview.ScrollToHome();
        // A later layout pass can restore the previous offset; scroll home again
        // once layout settles (Background priority — after render, no focus change).
        Dispatcher.BeginInvoke(new Action(() => RptPreview.ScrollToHome()),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    // Reconciles the checklist with the project's computed results, preserving the
    // user's prior include/exclude choices by result Id. New results are included by
    // default unless the user has cleared all. Never mutates the project.
    private void SyncReportChoices(ResearchProject p)
    {
        var saved = p.ComputedResults ?? new List<SavedComputedResult>();
        if (_reportChoicesProjectId != p.Id)
        {
            _reportChoices.Clear();
            _reportChoicesProjectId = p.Id;
            _reportClearedAll = false;
        }

        string curFp = CurrentStatisticsFingerprint(p);
        var liveIds = _computedResultsProjectId == p.Id
            ? _computedRows.Where(r => r.LiveResult is not null).Select(r => r.Id).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

        bool idsChanged = !saved.Select(s => s.Id).SequenceEqual(_reportChoices.Select(c => c.Id));
        if (idsChanged)
        {
            var prior = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var c in _reportChoices) prior[c.Id] = c.IsIncluded;
            _reportChoices.Clear();
            foreach (var s in saved)
            {
                bool included = prior.TryGetValue(s.Id, out var was) ? was : !_reportClearedAll;
                _reportChoices.Add(new ReportResultChoice
                {
                    Id = s.Id,
                    Title = ReportChoiceTitle(s),
                    Detail = ReportChoiceDetail(s, IsResultStale(s, curFp), !liveIds.Contains(s.Id)),
                    IsIncluded = included
                });
            }
        }
        else
        {
            // Same results — refresh the display detail only (stale/re-run may change).
            foreach (var c in _reportChoices)
            {
                var s = saved.First(x => x.Id == c.Id);
                c.Title = ReportChoiceTitle(s);
                c.Detail = ReportChoiceDetail(s, IsResultStale(s, curFp), !liveIds.Contains(s.Id));
            }
        }

        if (!ReferenceEquals(RptResultsList.ItemsSource, _reportChoices))
            RptResultsList.ItemsSource = _reportChoices;
        bool any = _reportChoices.Count > 0;
        RptResultsEmpty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        RptResultsList.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
    }

    private static bool IsResultStale(SavedComputedResult s, string currentFingerprint)
        => string.IsNullOrEmpty(s.AnalysisFingerprint)
           || !string.Equals(s.AnalysisFingerprint, currentFingerprint, StringComparison.Ordinal);

    private static string ReportChoiceTitle(SavedComputedResult s)
        => string.IsNullOrWhiteSpace(s.TestName)
            ? (string.IsNullOrWhiteSpace(s.Variables) ? "Result" : s.Variables.Trim())
            : s.TestName.Trim();

    private static string ReportChoiceDetail(SavedComputedResult s, bool stale, bool rerunNeeded)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(s.Variables)) parts.Add(s.Variables.Trim());
        if (!string.IsNullOrWhiteSpace(s.ValidNDisplay)) parts.Add(s.ValidNDisplay.Trim());
        if (!string.IsNullOrWhiteSpace(s.EffectDisplay)) parts.Add(s.EffectDisplay.Trim());
        if (!string.IsNullOrWhiteSpace(s.PValueDisplay)) parts.Add(s.PValueDisplay.Trim());
        if (!string.IsNullOrWhiteSpace(s.SignificanceText)) parts.Add(s.SignificanceText.Trim());
        if (stale) parts.Add("stale");
        if (rerunNeeded) parts.Add("re-run needed for manuscript text");
        return string.Join("  ·  ", parts);
    }

    private void RefreshManuscriptTab()
    {
        var p = CurrentResearchProject();
        if (p is null)
        {
            _lastReport = null;
            _reportChoices.Clear();
            RptResultsEmpty.Visibility = Visibility.Visible;
            RptResultsList.Visibility = Visibility.Collapsed;
            RptInclSummary.Text = "";
            RptSummary.Text = "";
            RptStaleBanner.Visibility = Visibility.Collapsed;
            SetReportPreview("Open a research project to build a report.");
            return;
        }

        SyncReportChoices(p);
        var report = BuildCurrentReport();
        _lastReport = report;
        if (report is null) { SetReportPreview("Open a research project to build a report."); return; }

        SetReportPreview(report.TextReport);
        RptSummary.Text =
            $"{report.IncludedResultCount} result(s) in report · "
            + $"{report.RerunNeededCount} need a Re-run for manuscript text · "
            + $"{report.StaleResultCount} stale.";

        int total = _reportChoices.Count;
        int incl = _reportChoices.Count(c => c.IsIncluded);
        RptInclSummary.Text = total == 0
            ? "No computed results yet."
            : $"{total} computed result(s) · {incl} included";

        bool stale = report.StaleResultCount > 0;
        RptStaleBanner.Visibility = stale ? Visibility.Visible : Visibility.Collapsed;
        if (stale)
            RptStaleText.Text =
                "Some included content may be stale because the dataset or extraction sheet "
                + "changed after it was computed. Re-run the affected analyses before using this draft.";
    }

    private void ReportChoice_Toggled(object sender, RoutedEventArgs e)
    {
        // The TwoWay binding already updated the model; a manual selection means new
        // results should again default to included. Rebuild the preview to match.
        if (CurrentResearchProject() is null) return;
        _reportClearedAll = false;
        RefreshManuscriptTab();
    }

    private void RptIncludeAll_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentResearchProject() is null) return;
        _reportClearedAll = false;
        foreach (var c in _reportChoices) c.IsIncluded = true;
        RefreshManuscriptTab();
    }

    private void RptClearAll_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentResearchProject() is null) return;
        _reportClearedAll = true;
        foreach (var c in _reportChoices) c.IsIncluded = false;
        RefreshManuscriptTab();
    }

    private void RptRebuild_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentResearchProject() is null) { ShowToast("Open a project first."); return; }
        RefreshManuscriptTab();
        ShowToast("Report rebuilt from current project data (deterministic, no AI).");
    }

    private void RptCopy_Click(object sender, RoutedEventArgs e)
    {
        if (_lastReport is null || string.IsNullOrWhiteSpace(_lastReport.TextReport)) { ShowToast("Build a report first."); return; }
        try { Clipboard.SetText(_lastReport.TextReport); ShowToast("Report copied to the clipboard."); }
        catch { ShowToast("The report could not be copied. Please try again."); }
    }

    private void RptExportTxt_Click(object sender, RoutedEventArgs e)
    {
        if (_lastReport is null || string.IsNullOrWhiteSpace(_lastReport.TextReport)) { ShowToast("Build a report first."); return; }
        SaveStatExport("research_report.txt", "Text files (*.txt)|*.txt", _lastReport.TextReport, "Report exported");
    }

    private void RptExportMd_Click(object sender, RoutedEventArgs e)
    {
        if (_lastReport is null || string.IsNullOrWhiteSpace(_lastReport.MarkdownReport)) { ShowToast("Build a report first."); return; }
        SaveStatExport("research_report.md", "Markdown files (*.md)|*.md|Text files (*.txt)|*.txt", _lastReport.MarkdownReport, "Report exported");
    }

    private void RptExportDocx_Click(object sender, RoutedEventArgs e)
    {
        if (_lastReport is null || string.IsNullOrWhiteSpace(_lastReport.TextReport)) { ShowToast("Build a report first."); return; }
        var report = _lastReport;
        SaveReportFile("research_report.docx", "Word document (*.docx)|*.docx",
            path => ResearchLabDocxExporter.Export(report, path), "Report exported");
    }

    private void RptExportPdf_Click(object sender, RoutedEventArgs e)
    {
        if (_lastReport is null || string.IsNullOrWhiteSpace(_lastReport.TextReport)) { ShowToast("Build a report first."); return; }
        var report = _lastReport;
        SaveReportFile("research_report.pdf", "PDF document (*.pdf)|*.pdf",
            path => ResearchLabPdfExporter.Export(report, path), "Report exported");
    }

    // Binary-safe variant of SaveStatExport for DOCX/PDF: the writer owns the file,
    // so we only supply the path and let the exporter produce the bytes.
    private void SaveReportFile(string defaultName, string filter, Action<string> write, string successVerb)
    {
        try
        {
            var sfd = new SaveFileDialog { FileName = defaultName, Filter = filter, AddExtension = true };
            if (sfd.ShowDialog() != true) return;
            write(sfd.FileName);
            ShowToast($"{successVerb} to {Path.GetFileName(sfd.FileName)}.");
        }
        catch (IOException)
        {
            ShowToast("The file could not be saved. It may be open in another program — close it and try again.");
        }
        catch (UnauthorizedAccessException)
        {
            ShowToast("The file could not be saved to that location. Choose a different folder and try again.");
        }
        catch
        {
            ShowToast("The export could not be completed. Please try a different location.");
        }
    }

    private void SaveDashNotes_Click(object sender, RoutedEventArgs e)
    {
        var project = _researchData.Projects.FirstOrDefault(p => p.Id == _openResearchId);
        if (project is null)
        {
            ShowToast("Open a project before saving notes.");
            return;
        }

        project.Notes = DashNotesBox.Text.Trim();
        project.UpdatedAt = DateTime.UtcNow;
        SaveResearch();
        ShowToast("Notes saved.");
    }

    private void BackToResearch_Click(object sender, RoutedEventArgs e)
    {
        RefreshResearchProjects();
        ShowPage(PageResearch);
    }

    // =====================================================================
    // Research Lab (Phase 2B) — in-app Research AI.
    //
    // The app calls a provider-neutral service that talks to a configurable
    // backend endpoint (or a development mock). There is NO copy/paste, no
    // provider branding, and no provider API keys anywhere in the app.
    // =====================================================================

    // ---- AI Recommendations: generate in-app ------------------------------

    private void GenerateRecommendations_Click(object sender, RoutedEventArgs e)
    {
        var p = CurrentResearchProject();
        if (p is null) { ShowToast("Open a project first."); return; }

        // Safe regenerate: if recommendations already exist, confirm first so a
        // stray click never quietly replaces the current AI suggestions.
        if (HasExistingRecommendations(p))
        {
            ShowRlConfirm(
                "Regenerate recommendations?",
                "Regenerating recommendations may replace the current AI suggestions. Your accepted research plan will not be overwritten unless you accept the new recommendations.",
                "Regenerate",
                () => _ = RunGenerateRecommendations(p));
            return;
        }

        // Workflow rule: if the student already has a proposal, they should
        // import it first so recommendations are extracted from their existing
        // work. Only prompt on first-time generation with nothing imported yet.
        if (!HasImportedProposal(p))
        {
            ShowRlConfirm(
                "Already have a proposal?",
                "Import it first so Research AI can extract recommendations from your existing work. If you don't have one, continue to generate recommendations from your project details.",
                okLabel: "Import Existing Proposal",
                onConfirm: OpenImportProposalOverlay,
                onCancel: () => _ = RunGenerateRecommendations(p),
                cancelLabel: "Continue without proposal");
            return;
        }

        _ = RunGenerateRecommendations(p);
    }

    private static bool HasImportedProposal(ResearchProject p)
        => p.ProposalImported || !string.IsNullOrWhiteSpace(p.ImportedProposalText);

    private async Task RunGenerateRecommendations(ResearchProject p)
    {
        if (!_researchAi.IsConfigured)
        {
            ShowResearchAiNotConfigured();
            ShowToast("Research AI service is not configured yet.");
            return;
        }

        // Captured before the call so a failed REGENERATION can be told apart
        // from a failed first-time generation, and so we know whether an
        // accepted plan is at stake — the old recommendations are never cleared
        // here, so this is purely about which message/banner to show.
        bool hadExisting = HasExistingRecommendations(p);
        bool wasAccepted = p.Recommendations?.AcceptedIntoPlan == true;

        AiRecError.Visibility = Visibility.Collapsed;
        AiRecNotConfigured.Visibility = Visibility.Collapsed;
        AiRecRegenFailedBanner.Visibility = Visibility.Collapsed;
        GenerateRecBtn.IsEnabled = false;
        var ct = BeginAiWork("Generating AI Recommendations",
            "Preparing project details", "Compacting imported proposal", "Sending request",
            "Waiting for Research AI", "Validating response", "Preparing review");

        try
        {
            AiWorkAdvance();   // project details prepared
            AiWorkAdvance();   // proposal compaction happens inside the prompt builder
            AiWorkAdvance();   // request sent → waiting
            var rec = await _researchAi.GenerateRecommendationsAsync(p, ct);
            AiWorkAdvance();   // response received + parsed → validating
            p.Recommendations = rec;
            // Fresh recommendations now reflect the current details, so clear the
            // "details changed" nudge.
            p.DetailsChangedSinceRecommendations = false;
            p.UpdatedAt = DateTime.UtcNow;
            SaveResearch();
            AiWorkAdvance();   // validated → preparing review

            RenderRecommendations(rec);
            PopulatePlanEditor(p);
            UpdateOverviewProgress(p);
            UpdateProposalPlanSource(p);
            UpdateGenerateRecButton(p);
            UpdateAiRecImportedBadge(p);
            await CompleteAiWorkAsync();
            ShowToast("Recommendations generated successfully.");
        }
        catch (OperationCanceledException)
        {
            ShowToast("Cancelled. Your existing content was kept.");
        }
        catch (ResearchAiNotConfiguredException)
        {
            ShowResearchAiNotConfigured();
            ShowToast("Research AI service is not configured yet.");
        }
        catch (ResearchAiException ex)
        {
            HandleRecGenerationFailure(hadExisting, wasAccepted, ex);
        }
        catch (Exception)
        {
            HandleRecGenerationFailure(hadExisting, wasAccepted, null);
        }
        finally
        {
            EndAiWork();
            GenerateRecBtn.IsEnabled = true;
        }
    }

    // Routes a failed generate/regenerate call to the right banner. A failed
    // FIRST-TIME generation has nothing to lose, so it's a plain error. A failed
    // REGENERATION with existing recommendations must never look destructive —
    // the old recommendations are still rendered below (they were never
    // overwritten), so the message says so explicitly instead of showing a red
    // "could not generate" banner underneath valid, still-visible content. The
    // specific reason (timeout with elapsed seconds, unauthorized, etc.) comes
    // straight from the service layer instead of a generic hardcoded string.
    private void HandleRecGenerationFailure(bool hadExisting, bool wasAccepted, ResearchAiException? ex)
    {
        _lastAiDiagnostics = ex?.Diagnostics;
        string reason = ex?.Message ?? "Research AI could not generate recommendations. Check your Research AI settings and try again.";

        if (hadExisting)
        {
            string message = reason + " Your previous recommendations were kept.";
            if (wasAccepted) message += " Your accepted research plan was not changed.";
            ShowAiRecRegenFailed(message);
            ShowToast("Could not regenerate recommendations. Your previous recommendations were kept.");
        }
        else
        {
            ShowAiRecError(reason);
        }
    }

    private void ShowResearchAiNotConfigured()
    {
        AiRecNotConfigured.Visibility = Visibility.Visible;
        AiRecError.Visibility = Visibility.Collapsed;
        AiRecRegenFailedBanner.Visibility = Visibility.Collapsed;
    }

    private void ShowAiRecError(string message)
    {
        AiRecErrorText.Text = message;
        AiRecError.Visibility = Visibility.Visible;
        AiRecNotConfigured.Visibility = Visibility.Collapsed;
        AiRecRegenFailedBanner.Visibility = Visibility.Collapsed;
    }

    private void ShowAiRecRegenFailed(string message)
    {
        AiRecRegenFailedText.Text = message;
        AiRecRegenFailedBanner.Visibility = Visibility.Visible;
        AiRecError.Visibility = Visibility.Collapsed;
        AiRecNotConfigured.Visibility = Visibility.Collapsed;
    }

    // Retry from the non-destructive "kept" banner re-runs the regeneration
    // directly — the user already confirmed once via the "Regenerate
    // recommendations?" dialog to get into this failed state, so asking again
    // here would be redundant friction.
    private void RetryRegenerateRecommendations_Click(object sender, RoutedEventArgs e)
    {
        var p = CurrentResearchProject();
        if (p is null) { ShowToast("Open a project first."); return; }
        _ = RunGenerateRecommendations(p);
    }

    private void UpdateGenerateRecButton(ResearchProject p)
    {
        bool has = p.Recommendations is not null &&
                   (p.Recommendations.HasStructuredContent || !string.IsNullOrWhiteSpace(p.Recommendations.RawAiText));
        GenerateRecBtnText.Text = has ? "Regenerate AI Recommendations" : "Generate AI Recommendations";

        // Accepted / Needs-review status. Once accepted, the Accept row is
        // replaced by a status banner; editing project details afterwards flips
        // the banner to "Needs review" until recommendations are reviewed or
        // regenerated (regenerating produces a fresh, not-yet-accepted set).
        bool accepted = p.Recommendations?.AcceptedIntoPlan == true;
        bool changed = p.DetailsChangedSinceRecommendations;
        AiRecAcceptRow.Visibility = has && !accepted ? Visibility.Visible : Visibility.Collapsed;
        AiRecAcceptedBanner.Visibility = accepted && !changed ? Visibility.Visible : Visibility.Collapsed;
        AiRecNeedsReviewBanner.Visibility = accepted && changed ? Visibility.Visible : Visibility.Collapsed;
    }

    private static bool HasExistingRecommendations(ResearchProject p)
        => p.Recommendations is not null &&
           (p.Recommendations.HasStructuredContent || !string.IsNullOrWhiteSpace(p.Recommendations.RawAiText));

    private void UpdateAiRecImportedBadge(ResearchProject p)
        => AiRecImportedBadge.Visibility = HasImportedProposal(p) ? Visibility.Visible : Visibility.Collapsed;

    private void AcceptRecommendations_Click(object sender, RoutedEventArgs e)
    {
        var p = CurrentResearchProject();
        if (p is null) { ShowToast("Open a project first."); return; }

        var rec = p.Recommendations;
        if (rec is null || (!rec.HasStructuredContent && string.IsNullOrWhiteSpace(rec.RawAiText)))
        {
            ShowToast("Generate AI recommendations first.");
            return;
        }

        rec.AcceptedIntoPlan = true;
        p.DetailsChangedSinceRecommendations = false;

        // Build the editable plan only if one doesn't already exist — never
        // overwrite the student's own edits.
        p.Plan ??= BuildPlanFromRecommendations(p, rec);

        p.CurrentStage = "Research plan ready";
        p.UpdatedAt = DateTime.UtcNow;   // progress derives from artifacts in SaveResearch
        SaveResearch();

        PopulateOverview(p);
        PopulatePlanEditor(p);
        UpdateOverviewProgress(p);
        UpdateProposalPlanSource(p);
        UpdateGenerateRecButton(p);   // flips the Accept row into the Accepted banner
        ShowToast("Recommendations accepted. Your research plan is ready.");
    }

    // ---- Research plan ----------------------------------------------------

    private static ResearchPlan BuildPlanFromRecommendations(ResearchProject p, ResearchRecommendations r)
    {
        return new ResearchPlan
        {
            FinalTitle = FirstNonEmpty(r.RefinedResearchTitle, p.Title),
            ResearchQuestion = r.ResearchQuestion,
            StudyDesign = FirstNonEmpty(r.RecommendedStudyDesign, p.StudyType),
            Aim = FirstNonEmpty(p.Aim, r.PrimaryObjective),
            PrimaryObjective = r.PrimaryObjective,
            SecondaryObjectives = JoinLines(r.SecondaryObjectives),
            Population = p.Population,
            Setting = p.Setting,
            InclusionCriteria = JoinLines(r.InclusionCriteria),
            ExclusionCriteria = JoinLines(r.ExclusionCriteria),
            MainVariables = JoinLines(r.SuggestedVariables
                .Select(v => v.HeaderDisplay + (string.IsNullOrWhiteSpace(v.Role) ? "" : " (" + v.Role + ")"))
                .ToList()),
            SuggestedAnalyses = JoinLines(r.SuggestedAnalyses.Select(a => a.HeaderDisplay).ToList()),
            NextSteps = JoinLines(r.NextSteps),
            UpdatedAt = DateTime.UtcNow
        };
    }

    private void PopulatePlanEditor(ResearchProject p)
    {
        bool accepted = p.Recommendations?.AcceptedIntoPlan == true;
        PlanEmptyHint.Visibility = accepted ? Visibility.Collapsed : Visibility.Visible;
        PlanEditor.Visibility = accepted ? Visibility.Visible : Visibility.Collapsed;
        if (!accepted) return;

        var plan = p.Plan ??= BuildPlanFromRecommendations(p, p.Recommendations!);

        PlanTitle.Text = plan.FinalTitle;
        PlanQuestion.Text = plan.ResearchQuestion;
        PlanDesign.Text = plan.StudyDesign;
        PlanAim.Text = plan.Aim;
        PlanPrimaryObj.Text = plan.PrimaryObjective;
        PlanSecondaryObj.Text = plan.SecondaryObjectives;
        PlanPopulation.Text = plan.Population;
        PlanSetting.Text = plan.Setting;
        PlanInclusion.Text = plan.InclusionCriteria;
        PlanExclusion.Text = plan.ExclusionCriteria;
        PlanVariables.Text = plan.MainVariables;
        PlanAnalyses.Text = plan.SuggestedAnalyses;
        PlanNextSteps.Text = plan.NextSteps;
    }

    private void SaveResearchPlan_Click(object sender, RoutedEventArgs e)
    {
        var p = CurrentResearchProject();
        if (p is null) { ShowToast("Open a project first."); return; }

        p.Plan ??= new ResearchPlan();
        p.Plan.FinalTitle = PlanTitle.Text.Trim();
        p.Plan.ResearchQuestion = PlanQuestion.Text.Trim();
        p.Plan.StudyDesign = PlanDesign.Text.Trim();
        p.Plan.Aim = PlanAim.Text.Trim();
        p.Plan.PrimaryObjective = PlanPrimaryObj.Text.Trim();
        p.Plan.SecondaryObjectives = PlanSecondaryObj.Text.Trim();
        p.Plan.Population = PlanPopulation.Text.Trim();
        p.Plan.Setting = PlanSetting.Text.Trim();
        p.Plan.InclusionCriteria = PlanInclusion.Text.Trim();
        p.Plan.ExclusionCriteria = PlanExclusion.Text.Trim();
        p.Plan.MainVariables = PlanVariables.Text.Trim();
        p.Plan.SuggestedAnalyses = PlanAnalyses.Text.Trim();
        p.Plan.NextSteps = PlanNextSteps.Text.Trim();
        p.Plan.UpdatedAt = DateTime.UtcNow;
        p.UpdatedAt = DateTime.UtcNow;
        SaveResearch();
        ShowToast("Research plan saved.");
    }

    // ---- Proposal: generate in-app ----------------------------------------

    // Primary path: the proposal should be built from the accepted research
    // plan. If the student has not accepted recommendations yet, we do NOT
    // silently produce a weak draft from raw details — we surface the plan-source
    // status and point them at the AI Recommendations tab instead.
    private async void GenerateProposalDraft_Click(object sender, RoutedEventArgs e)
    {
        var p = CurrentResearchProject();
        if (p is null) { ShowToast("Open a project first."); return; }

        // One-time generation: a new draft requires deleting the current one first.
        if (HasProposalDraftContent(p))
        {
            UpdateProposalPlanSource(p);
            ShowToast("Proposal draft already generated. Delete the draft first to generate a new one.");
            return;
        }

        if (!HasAcceptedResearchPlan(p))
        {
            UpdateProposalPlanSource(p);
            ShowToast("Generate and accept AI recommendations first for the best proposal draft.");
            return;
        }

        await GenerateProposalCore(p);
    }

    // Explicit secondary opt-in: build a basic proposal from project details even
    // without an accepted plan. Only reachable from the labelled button in the
    // "no accepted research plan" status card, and always behind a strong warning
    // so the student knowingly takes responsibility for the lower-quality path.
    private void GenerateBasicProposal_Click(object sender, RoutedEventArgs e)
    {
        var p = CurrentResearchProject();
        if (p is null) { ShowToast("Open a project first."); return; }

        if (HasProposalDraftContent(p))
        {
            UpdateProposalPlanSource(p);
            ShowToast("Proposal draft already generated. Delete the draft first to generate a new one.");
            return;
        }

        ShowRlConfirm("Generate without accepted research plan?",
            "You are generating a proposal without an accepted AI research plan. The draft may be incomplete or inaccurate. "
            + "You are responsible for reviewing and correcting the proposal before use.",
            "Continue Anyway",
            onConfirm: () => _ = GenerateProposalCore(p));
    }

    private void GoToAiRecommendations_Click(object sender, RoutedEventArgs e)
        => SwitchResearchTab(SegAiRec);

    private async Task GenerateProposalCore(ResearchProject p)
    {
        if (!_researchAi.IsConfigured)
        {
            ShowProposalNotConfigured();
            ShowToast("Research AI service is not configured yet.");
            return;
        }

        ProposalError.Visibility = Visibility.Collapsed;
        ProposalNotConfigured.Visibility = Visibility.Collapsed;
        GenerateProposalBtn.IsEnabled = false;
        var ct = BeginAiWork("Drafting Proposal",
            "Preparing research plan", "Building draft request", "Sending request",
            "Waiting for Research AI", "Validating response", "Preparing proposal editor");

        try
        {
            // Pass the accepted recommendations so the draft is built from the
            // accepted research plan (title, question, design, objectives,
            // variables, analyses, criteria, ethics, limitations, next steps).
            var accepted = HasAcceptedResearchPlan(p) ? p.Recommendations : null;
            AiWorkAdvance();   // plan prepared
            AiWorkAdvance();   // draft request built
            AiWorkAdvance();   // request sent → waiting
            var draft = await _researchAi.GenerateProposalDraftAsync(p, accepted, ct);
            AiWorkAdvance();   // response received + parsed → validating
            p.ProposalDraft = draft;
            p.CurrentStage = "Proposal drafted";
            p.UpdatedAt = DateTime.UtcNow;
            SaveResearch();
            AiWorkAdvance();   // validated → preparing editor

            PopulateProposalEditor(p);
            PopulateOverview(p);
            UpdateOverviewProgress(p);
            await CompleteAiWorkAsync();
            ShowToast("Proposal draft generated successfully.");
        }
        catch (OperationCanceledException)
        {
            ShowToast("Cancelled. Your existing content was kept.");
        }
        catch (ResearchAiNotConfiguredException)
        {
            ShowProposalNotConfigured();
            ShowToast("Research AI service is not configured yet.");
        }
        catch (ResearchAiException ex)
        {
            _lastAiDiagnostics = ex.Diagnostics;
            // Existing content is never at risk here: the one-time-generation
            // guard means GenerateProposalCore only runs when no draft exists
            // yet, so there is nothing to protect — just show the real reason.
            ShowProposalError(ex.Message);
        }
        catch (Exception)
        {
            ShowProposalError("Research AI could not generate the proposal draft. Check your Research AI settings and try again.");
        }
        finally
        {
            EndAiWork();
            // Restore the button to the correct state for the current plan status
            // (enabled only when an accepted research plan exists).
            UpdateProposalPlanSource(p);
        }
    }

    private static bool HasAcceptedResearchPlan(ResearchProject p)
        => p.Recommendations?.AcceptedIntoPlan == true;

    // True when a draft with real content exists (an empty shell created by
    // saving blank fields doesn't count, so generation isn't locked out by it).
    private static bool HasProposalDraftContent(ResearchProject p)
    {
        var d = p.ProposalDraft;
        if (d is null) return false;
        return !string.IsNullOrWhiteSpace(d.Title) || !string.IsNullOrWhiteSpace(d.Background)
            || !string.IsNullOrWhiteSpace(d.Aim) || !string.IsNullOrWhiteSpace(d.Objectives)
            || !string.IsNullOrWhiteSpace(d.Methods) || !string.IsNullOrWhiteSpace(d.StudyDesign)
            || !string.IsNullOrWhiteSpace(d.StatisticalAnalysisPlan) || !string.IsNullOrWhiteSpace(d.RawText);
    }

    // Shows the correct "Research Plan Source" status card in the Proposal tab
    // and toggles the generate buttons. Generation is ONE-TIME: once a draft
    // exists it must be deleted before a new one can be generated, so AI usage
    // is never wasted on accidental re-generation.
    private void UpdateProposalPlanSource(ResearchProject p)
    {
        bool accepted = HasAcceptedResearchPlan(p);
        bool hasDraft = HasProposalDraftContent(p);

        ProposalPlanSourceAccepted.Visibility = accepted ? Visibility.Visible : Visibility.Collapsed;
        ProposalPlanSourceMissing.Visibility = accepted ? Visibility.Collapsed : Visibility.Visible;
        ProposalDraftExistsCard.Visibility = hasDraft ? Visibility.Visible : Visibility.Collapsed;

        GenerateProposalBtn.IsEnabled = accepted && !hasDraft;
        GenerateProposalBtnText.Text = hasDraft
            ? "Proposal draft already generated"
            : accepted ? "Generate Proposal Draft" : "Accept a research plan first";
        GenerateBasicProposalBtn.IsEnabled = !hasDraft;
    }

    private void DeleteProposalDraft_Click(object sender, RoutedEventArgs e)
    {
        var p = CurrentResearchProject();
        if (p is null) { ShowToast("Open a project first."); return; }
        if (p.ProposalDraft is null) { ShowToast("There is no proposal draft to delete."); return; }

        ShowRlConfirm("Delete proposal draft?",
            "This will delete the current proposal draft. You can then generate a new draft. Continue?",
            "Delete Draft",
            onConfirm: () =>
            {
                p.ProposalDraft = null;
                p.UpdatedAt = DateTime.UtcNow;
                SaveResearch();
                PopulateProposalEditor(p);
                UpdateProposalPlanSource(p);
                PopulateOverview(p);
                UpdateOverviewProgress(p);
                ShowToast("Proposal draft deleted. You can generate a new one.");
            });
    }

    private void ShowProposalNotConfigured()
    {
        ProposalNotConfigured.Visibility = Visibility.Visible;
        ProposalError.Visibility = Visibility.Collapsed;
    }

    private void ShowProposalError(string message)
    {
        ProposalErrorText.Text = message;
        ProposalError.Visibility = Visibility.Visible;
        ProposalNotConfigured.Visibility = Visibility.Collapsed;
    }

    private void SaveProposal_Click(object sender, RoutedEventArgs e)
    {
        var p = CurrentResearchProject();
        if (p is null) { ShowToast("Open a project first."); return; }

        var d = p.ProposalDraft ??= new ResearchProposalDraft { SourceMode = ResearchSourceMode.Unknown };
        d.Title = PropTitle.Text.Trim();
        d.Background = PropBackground.Text.Trim();
        d.Rationale = PropRationale.Text.Trim();
        d.Aim = PropAim.Text.Trim();
        d.Objectives = PropObjectives.Text.Trim();
        d.Methods = PropMethods.Text.Trim();
        d.StudyDesign = PropStudyDesign.Text.Trim();
        d.Setting = PropSetting.Text.Trim();
        d.Population = PropPopulation.Text.Trim();
        d.InclusionCriteria = PropInclusion.Text.Trim();
        d.ExclusionCriteria = PropExclusion.Text.Trim();
        d.Variables = PropVariables.Text.Trim();
        d.DataCollection = PropDataCollection.Text.Trim();
        d.StatisticalAnalysisPlan = PropStatsPlan.Text.Trim();
        d.Ethics = PropEthics.Text.Trim();
        d.Timeline = PropTimeline.Text.Trim();
        d.Limitations = PropLimitations.Text.Trim();
        d.UpdatedAt = DateTime.UtcNow;

        p.CurrentStage = "Proposal drafted";
        p.UpdatedAt = DateTime.UtcNow;
        SaveResearch();

        PopulateOverview(p);
        UpdateOverviewProgress(p);
        ShowToast("Proposal draft saved.");
    }

    private void PopulateProposalEditor(ResearchProject p)
    {
        var d = p.ProposalDraft;
        var boxes = new[]
        {
            PropTitle, PropBackground, PropRationale, PropAim, PropObjectives, PropMethods,
            PropStudyDesign, PropSetting, PropPopulation, PropInclusion, PropExclusion,
            PropVariables, PropDataCollection, PropStatsPlan, PropEthics, PropTimeline, PropLimitations
        };

        if (d is null)
        {
            foreach (var box in boxes) box.Text = "";
            PropTemplateBadge.Visibility = Visibility.Collapsed;
            PropRawCard.Visibility = Visibility.Collapsed;
            PropRawText.Text = "";
            return;
        }

        PropTitle.Text = d.Title;
        PropBackground.Text = d.Background;
        PropRationale.Text = d.Rationale;
        PropAim.Text = d.Aim;
        PropObjectives.Text = d.Objectives;
        PropMethods.Text = d.Methods;
        PropStudyDesign.Text = d.StudyDesign;
        PropSetting.Text = d.Setting;
        PropPopulation.Text = d.Population;
        PropInclusion.Text = d.InclusionCriteria;
        PropExclusion.Text = d.ExclusionCriteria;
        PropVariables.Text = d.Variables;
        PropDataCollection.Text = d.DataCollection;
        PropStatsPlan.Text = d.StatisticalAnalysisPlan;
        PropEthics.Text = d.Ethics;
        PropTimeline.Text = d.Timeline;
        PropLimitations.Text = d.Limitations;

        PropTemplateBadge.Visibility = d.IsTemplateGenerated ? Visibility.Visible : Visibility.Collapsed;
        bool hasRaw = !string.IsNullOrWhiteSpace(d.RawText);
        PropRawCard.Visibility = hasRaw ? Visibility.Visible : Visibility.Collapsed;
        PropRawText.Text = hasRaw ? d.RawText : "";
    }

    // ---- Recommendations viewer rendering ---------------------------------

    private void RenderRecommendations(ResearchRecommendations? rec)
    {
        bool has = rec is not null && (rec.HasStructuredContent || !string.IsNullOrWhiteSpace(rec.RawAiText));
        AiRecEmptyHint.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        AiRecViewer.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        if (!has || rec is null) return;

        AiRecSourceBadge.Text = rec.SourceLabel;

        SetTextSection(RecRefinedTitle, RecTitleSec, rec.RefinedResearchTitle);
        SetTextSection(RecStudyDesign, RecStudyDesignSec, rec.RecommendedStudyDesign);
        SetTextSection(RecQuestion, RecQuestionSec, rec.ResearchQuestion);
        SetTextSection(RecPrimaryObjective, RecPrimarySec, rec.PrimaryObjective);

        SetListSection(RecSecondaryList, RecSecondarySec, rec.SecondaryObjectives);
        // Variable creation/coding/type suggestions belong to Data Extraction /
        // Magic Fix, never AI Recommendations — showing them here duplicates
        // (and can conflict with) that workflow. The underlying data is kept
        // untouched (Data Extraction's "plan mentions X" conflict cards still
        // read rec.SuggestedVariables directly) — only this tab's rendering of
        // it is suppressed.
        RecVariablesList.ItemsSource = null;
        RecVariablesSec.Visibility = Visibility.Collapsed;
        SetListSection(RecAnalysesList, RecAnalysesSec, rec.SuggestedAnalyses);
        SetListSection(RecInclusionList, RecInclusionSec, rec.InclusionCriteria);
        SetListSection(RecExclusionList, RecExclusionSec, rec.ExclusionCriteria);
        SetListSection(RecDataCollectionList, RecDataCollectionSec, rec.DataCollectionSuggestions);
        SetListSection(RecBiasList, RecBiasSec, rec.BiasAndLimitations);
        SetListSection(RecEthicsList, RecEthicsSec, rec.EthicsNotes);
        SetListSection(RecNextStepsList, RecNextStepsSec, rec.NextSteps);

        bool showRaw = !rec.HasStructuredContent && !string.IsNullOrWhiteSpace(rec.RawAiText);
        RecRawCard.Visibility = showRaw ? Visibility.Visible : Visibility.Collapsed;
        RecRawText.Text = showRaw ? rec.RawAiText : "";
    }

    private static void SetTextSection(TextBlock target, UIElement section, string value)
    {
        target.Text = value ?? "";
        section.Visibility = string.IsNullOrWhiteSpace(value) ? Visibility.Collapsed : Visibility.Visible;
    }

    private static void SetListSection(ItemsControl control, UIElement section, System.Collections.IList items)
    {
        control.ItemsSource = null;
        control.ItemsSource = items;
        section.Visibility = items is { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- Overview progress (checklist + status + next step) ---------------

    private void UpdateOverviewProgress(ResearchProject p)
    {
        var rec = p.Recommendations;
        bool hasRec = rec is not null && (rec.HasStructuredContent || !string.IsNullOrWhiteSpace(rec.RawAiText));
        bool planAccepted = rec?.AcceptedIntoPlan == true;
        bool hasProposal = p.ProposalDraft is not null;

        SetChecklistRow(ChkCreatedIcon, ChkCreatedText, true, "Project created");
        SetChecklistRow(ChkRecIcon, ChkRecText, hasRec, hasRec ? "AI recommendations — complete" : "AI recommendations — pending");
        SetChecklistRow(ChkPlanIcon, ChkPlanText, planAccepted, planAccepted ? "Research plan — accepted" : "Research plan — pending");
        SetChecklistRow(ChkProposalIcon, ChkProposalText, hasProposal, hasProposal ? "Proposal draft — created" : "Proposal draft — pending");
        SetChecklistRow(ChkDataIcon, ChkDataText, false, "Data extraction — pending");
        SetChecklistRow(ChkStatsIcon, ChkStatsText, false, "Statistics — pending");
        SetChecklistRow(ChkManuscriptIcon, ChkManuscriptText, false, "Manuscript — pending");

        SetStatusText(OvRecStatus, hasRec, hasRec ? "Complete" : "Not started");
        SetStatusText(OvPlanStatus, planAccepted, planAccepted ? "Accepted" : "Not started");
        SetStatusText(OvProposalStatus, hasProposal, hasProposal ? "Created" : "Not started");

        string next;
        if (!hasRec) next = "Next: generate or import AI recommendations.";
        else if (!planAccepted) next = "Next: review and accept the research plan.";
        else if (!hasProposal) next = "Next: generate a proposal draft.";
        else next = "Next: build your data extraction sheet in Phase 3.";
        DashNextStepText.Text = next;

        // Phase 2E/D — details-changed nudge, plan readiness, and extraction gate.
        OvDetailsChangedWarning.Visibility =
            (p.DetailsChangedSinceRecommendations && hasRec) ? Visibility.Visible : Visibility.Collapsed;
        UpdateReadiness(p);
        UpdateReadyForExtraction(p, planAccepted, hasProposal);
    }

    // ---- Research Plan Readiness (Phase 2E) --------------------------------

    private void UpdateReadiness(ResearchProject p)
    {
        var rec = p.Recommendations;
        bool accepted = HasAcceptedResearchPlan(p);

        // A field counts as ready if the recommendations OR the accepted/edited
        // plan carry it. Never fabricated — purely reflects stored content.
        bool title = !string.IsNullOrWhiteSpace(p.Title)
            || !string.IsNullOrWhiteSpace(rec?.RefinedResearchTitle)
            || !string.IsNullOrWhiteSpace(p.Plan?.FinalTitle);
        bool design = !string.IsNullOrWhiteSpace(rec?.RecommendedStudyDesign)
            || !string.IsNullOrWhiteSpace(p.Plan?.StudyDesign)
            || (!string.IsNullOrWhiteSpace(p.StudyType) && !string.Equals(p.StudyType.Trim(), "Not sure", StringComparison.OrdinalIgnoreCase));
        bool question = !string.IsNullOrWhiteSpace(rec?.ResearchQuestion)
            || !string.IsNullOrWhiteSpace(p.Plan?.ResearchQuestion);
        bool objectives = !string.IsNullOrWhiteSpace(rec?.PrimaryObjective)
            || (rec?.SecondaryObjectives.Count > 0)
            || !string.IsNullOrWhiteSpace(p.Plan?.PrimaryObjective);
        bool variables = (rec?.SuggestedVariables.Count > 0)
            || !string.IsNullOrWhiteSpace(p.Plan?.MainVariables);
        bool analyses = (rec?.SuggestedAnalyses.Count > 0)
            || !string.IsNullOrWhiteSpace(p.Plan?.SuggestedAnalyses);
        bool criteria = (rec?.InclusionCriteria.Count > 0) || (rec?.ExclusionCriteria.Count > 0)
            || !string.IsNullOrWhiteSpace(p.Plan?.InclusionCriteria) || !string.IsNullOrWhiteSpace(p.Plan?.ExclusionCriteria);
        bool ethics = rec?.EthicsNotes.Count > 0;
        bool proposal = p.ProposalDraft is not null;

        SetChecklistRow(RdyTitleIcon, RdyTitleText, title, "Project title defined");
        SetChecklistRow(RdyDesignIcon, RdyDesignText, design, "Study design selected");
        SetChecklistRow(RdyQuestionIcon, RdyQuestionText, question, "Research question generated");
        SetChecklistRow(RdyObjectivesIcon, RdyObjectivesText, objectives, "Objectives generated");
        SetChecklistRow(RdyVariablesIcon, RdyVariablesText, variables, "Variables suggested");
        SetChecklistRow(RdyAnalysesIcon, RdyAnalysesText, analyses, "Analyses suggested");
        SetChecklistRow(RdyCriteriaIcon, RdyCriteriaText, criteria, "Inclusion/exclusion criteria added");
        SetChecklistRow(RdyEthicsIcon, RdyEthicsText, ethics, "Ethics notes added");
        SetChecklistRow(RdyAcceptedIcon, RdyAcceptedText, accepted, "Recommendations accepted");
        SetChecklistRow(RdyProposalIcon, RdyProposalText, proposal, "Proposal draft created");
    }

    // ---- Ready for Data Extraction (Phase 2D/E) ---------------------------

    private void UpdateReadyForExtraction(ResearchProject p, bool planAccepted, bool hasProposal)
    {
        bool ready = planAccepted && hasProposal;

        var solid = ready ? (Brush)FindResource("SuccessBrush") : (Brush)FindResource("WarningBrush");
        var soft = ready ? (Brush)FindResource("SuccessSoftBrush") : (Brush)FindResource("WarningSoftBrush");

        OvReadyCard.Background = soft;
        OvReadyIcon.Background = solid;
        OvReadyTitle.Foreground = solid;
        OvReadyDetail.Foreground = solid;

        OvReadyIcon.Child = ready
            ? new System.Windows.Shapes.Path
            {
                Data = (Geometry)FindResource("IconCheck"),
                Fill = Brushes.White,
                Width = 13, Height = 13, Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
            : null;

        if (ready)
        {
            OvReadyTitle.Text = "Ready for Data Extraction";
            OvReadyDetail.Text = "Next: build your data extraction sheet in Phase 3.";
        }
        else
        {
            OvReadyTitle.Text = "Not ready for data extraction yet";
            var missing = new List<string>();
            if (!planAccepted)
            {
                if (!HasExistingRecommendations(p)) missing.Add("Generate AI recommendations");
                missing.Add("Accept research plan");
            }
            if (!hasProposal) missing.Add("Generate proposal draft");
            OvReadyDetail.Text = "Still needed: " + string.Join("  •  ", missing);
        }
    }

    private void SetStatusText(TextBlock target, bool done, string text)
    {
        target.Text = text;
        target.Foreground = (Brush)FindResource(done ? "SuccessBrush" : "MutedBrush");
    }

    private void SetChecklistRow(Border icon, TextBlock text, bool done, string label)
    {
        text.Text = label;

        if (done)
        {
            icon.Background = (Brush)FindResource("SuccessBrush");
            icon.BorderThickness = new Thickness(0);
            icon.Child = new System.Windows.Shapes.Path
            {
                Data = (Geometry)FindResource("IconCheck"),
                Fill = Brushes.White,
                Width = 11,
                Height = 11,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            text.Foreground = (Brush)FindResource("TextBrush");
            text.FontWeight = FontWeights.SemiBold;
        }
        else
        {
            icon.Background = Brushes.Transparent;
            icon.BorderBrush = (Brush)FindResource("BorderBrushSoft");
            icon.BorderThickness = new Thickness(1.6);
            icon.Child = null;
            text.Foreground = (Brush)FindResource("MutedBrush");
            text.FontWeight = FontWeights.Normal;
        }
    }

    // ---- Research AI configuration ----------------------------------------

    private void LoadResearchAiConfig()
    {
        try
        {
            if (!File.Exists(ResearchAiConfigPath))
            {
                _researchAiOptions = new ResearchAiOptions();
                return;
            }

            string json = File.ReadAllText(ResearchAiConfigPath);
            _researchAiOptions = JsonSerializer.Deserialize<ResearchAiOptions>(json) ?? new ResearchAiOptions();

            // Phase 8 migration: drop any obsolete provider keys (endpoint base URL,
            // direct/dev-provider flags) from the on-disk research config by
            // re-writing it with only the current fields. Extra JSON props are
            // ignored on read, so re-saving cleans the file.
            try { SaveResearchAiConfig(); } catch { /* best-effort */ }
        }
        catch
        {
            // Never let a bad config file block the app.
            _researchAiOptions = new ResearchAiOptions();
        }
    }

    private void SaveResearchAiConfig()
    {
        try
        {
            File.WriteAllText(ResearchAiConfigPath, JsonSerializer.Serialize(_researchAiOptions, new JsonSerializerOptions
            {
                WriteIndented = true
            }));
        }
        catch (Exception ex)
        {
            ShowToast("Could not save Research AI settings: " + ex.Message);
        }
    }

    // Shows the last failure's developer-safe diagnostics (never any secrets).
    private void ShowAiDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        string details = string.IsNullOrWhiteSpace(_lastAiDiagnostics)
            ? (ResearchAiDiagnostics.LastEntry ?? "No diagnostic details are available.")
            : _lastAiDiagnostics!;
        MessageBox.Show(this, details + "\n\nA full log is saved to research_ai.log in the app data folder. No API keys or request content are ever logged.",
            "Research AI — details", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // Shows/hides the "not configured" panels for the open project's AI tabs.
    private void UpdateResearchAiAvailability()
    {
        bool configured = _researchAi.IsConfigured;
        AiRecNotConfigured.Visibility = configured ? Visibility.Collapsed : Visibility.Visible;
        ProposalNotConfigured.Visibility = configured ? Visibility.Collapsed : Visibility.Visible;
        AiRecError.Visibility = Visibility.Collapsed;
        AiRecRegenFailedBanner.Visibility = Visibility.Collapsed;
        ProposalError.Visibility = Visibility.Collapsed;
    }

    // =====================================================================
    // Research Lab (Phase 2E) — Edit Project Details
    // =====================================================================

    private void OpenEditProject_Click(object sender, RoutedEventArgs e)
    {
        var p = CurrentResearchProject();
        if (p is null) { ShowToast("Open a project first."); return; }

        PopulateEditProjectForm(p);
        EpValidationText.Visibility = Visibility.Collapsed;
        EpRecsWarning.Visibility = HasExistingRecommendations(p) ? Visibility.Visible : Visibility.Collapsed;
        EditProjectOverlay.Visibility = Visibility.Visible;
        FadeIn(EditProjectOverlay, 160);
        EpTitleBox.Focus();
    }

    private void PopulateEditProjectForm(ResearchProject p)
    {
        EpTitleBox.Text = p.Title;
        EpSpecialtyBox.Text = p.Specialty;
        EpAimBox.Text = p.Aim;
        EpPopulationBox.Text = p.Population;
        EpSettingBox.Text = p.Setting;
        EpTimePeriodBox.Text = p.TimePeriod;
        EpNotesBox.Text = p.Notes;

        SelectComboByText(EpStudyTypeCombo, p.StudyType, 7);   // fallback "Not sure"
        SelectComboByText(EpDataCombo, p.AvailableDataType, 4); // fallback "No data yet"

        var outs = p.DesiredOutputs ?? new List<string>();
        EpOutProposal.IsChecked = outs.Contains("Proposal");
        EpOutIntroduction.IsChecked = outs.Contains("Introduction");
        EpOutMethods.IsChecked = outs.Contains("Methods");
        EpOutExtraction.IsChecked = outs.Contains("Data Extraction Sheet");
        EpOutStatistics.IsChecked = outs.Contains("Statistics");
        EpOutManuscript.IsChecked = outs.Contains("Manuscript");
        EpOutAbstract.IsChecked = outs.Contains("Abstract");
        EpOutTables.IsChecked = outs.Contains("Tables");
    }

    private static void SelectComboByText(ComboBox combo, string text, int fallbackIndex)
    {
        string want = (text ?? "").Trim();
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem item &&
                string.Equals(item.Content?.ToString()?.Trim(), want, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedIndex = i;
                return;
            }
        }
        combo.SelectedIndex = fallbackIndex;
    }

    private List<string> CollectEditDesiredOutputs()
    {
        var outputs = new List<string>();
        if (EpOutProposal.IsChecked == true) outputs.Add("Proposal");
        if (EpOutIntroduction.IsChecked == true) outputs.Add("Introduction");
        if (EpOutMethods.IsChecked == true) outputs.Add("Methods");
        if (EpOutExtraction.IsChecked == true) outputs.Add("Data Extraction Sheet");
        if (EpOutStatistics.IsChecked == true) outputs.Add("Statistics");
        if (EpOutManuscript.IsChecked == true) outputs.Add("Manuscript");
        if (EpOutAbstract.IsChecked == true) outputs.Add("Abstract");
        if (EpOutTables.IsChecked == true) outputs.Add("Tables");
        return outputs;
    }

    private void CancelEditProject_Click(object sender, RoutedEventArgs e)
        => EditProjectOverlay.Visibility = Visibility.Collapsed;

    private void SaveEditProject_Click(object sender, RoutedEventArgs e)
    {
        var p = CurrentResearchProject();
        if (p is null) { EditProjectOverlay.Visibility = Visibility.Collapsed; return; }

        string title = EpTitleBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            EpValidationText.Text = "Please enter a project title to continue.";
            EpValidationText.Visibility = Visibility.Visible;
            EpTitleBox.Focus();
            return;
        }

        // Detect whether any detail that feeds the research plan actually changed,
        // so we can gently suggest regenerating recommendations afterwards.
        bool changed =
            !StringEquals(p.Title, title)
            || !StringEquals(p.Specialty, EpSpecialtyBox.Text)
            || !StringEquals(p.StudyType, ComboText(EpStudyTypeCombo, "Not sure"))
            || !StringEquals(p.Aim, EpAimBox.Text)
            || !StringEquals(p.Population, EpPopulationBox.Text)
            || !StringEquals(p.Setting, EpSettingBox.Text)
            || !StringEquals(p.TimePeriod, EpTimePeriodBox.Text)
            || !StringEquals(p.AvailableDataType, ComboText(EpDataCombo, "No data yet"));

        // If the student has ALREADY accepted a research plan, a details change
        // would silently desync it. Require a strong confirmation and mark the
        // accepted recommendations as "Needs review" (the plan itself is kept).
        bool willNeedReview = changed && p.Recommendations?.AcceptedIntoPlan == true;
        if (willNeedReview)
        {
            ShowRlConfirm(
                "Changing accepted project details?",
                "Changing project details will mark your accepted recommendations as “Needs review”. Your recommendations and plan are kept, but they may no longer match the new details. Continue?",
                okLabel: "Continue",
                onConfirm: () => ApplyEditProject(p, title, changed));
            return;
        }

        ApplyEditProject(p, title, changed);
    }

    private void ApplyEditProject(ResearchProject p, string title, bool changed)
    {
        p.Title = title;
        p.Specialty = EpSpecialtyBox.Text.Trim();
        p.StudyType = ComboText(EpStudyTypeCombo, "Not sure");
        p.Aim = EpAimBox.Text.Trim();
        p.Population = EpPopulationBox.Text.Trim();
        p.Setting = EpSettingBox.Text.Trim();
        p.TimePeriod = EpTimePeriodBox.Text.Trim();
        p.AvailableDataType = ComboText(EpDataCombo, "No data yet");
        p.DesiredOutputs = CollectEditDesiredOutputs();
        p.Notes = EpNotesBox.Text.Trim();
        p.UpdatedAt = DateTime.UtcNow;

        // Existing recommendations / proposal are deliberately NOT deleted.
        if (changed && HasExistingRecommendations(p))
            p.DetailsChangedSinceRecommendations = true;

        SaveResearch();

        EditProjectOverlay.Visibility = Visibility.Collapsed;

        // Refresh every view that shows project details.
        PopulateOverview(p);
        PopulateAiSnapshot(p);
        DashNotesBox.Text = p.Notes;
        DashTitleText.Text = p.Title;
        DashSubtitleText.Text = $"{p.SpecialtyDisplay}  •  {p.StudyTypeDisplay}";
        UpdateOverviewProgress(p);
        UpdateGenerateRecButton(p);   // accepted plan → "Needs review" banner

        ShowToast(changed && HasExistingRecommendations(p)
            ? "Project details updated. Consider regenerating AI recommendations."
            : "Project details updated.");
    }

    private static bool StringEquals(string a, string b)
        => string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.Ordinal);

    // =====================================================================
    // Research Lab (Phase 2F) — Import Existing Proposal
    // =====================================================================

    private void OpenImportProposal_Click(object sender, RoutedEventArgs e)
        => OpenImportProposalOverlay();

    private void OpenImportProposalOverlay()
    {
        var p = CurrentResearchProject();
        if (p is null) { ShowToast("Open a project first."); return; }

        // Anti-spam guard: a proposal is imported once. Re-importing (and spending
        // another extraction) requires an explicit confirmation to delete the
        // current imported proposal first — no silent re-extraction.
        if (HasImportedProposal(p))
        {
            ShowRlConfirm(
                "Proposal already imported",
                "You have already imported a proposal for this project. To import a different one, the current imported proposal must be deleted first. "
                + "Your recommendations, plan, and proposal draft are kept. Delete the imported proposal and import a new one?",
                okLabel: "Delete & Re-import",
                onConfirm: () => { DeleteImportedProposal(p); ShowImportProposalOverlayCore(); },
                cancelLabel: "Keep current");
            return;
        }

        ShowImportProposalOverlayCore();
    }

    private void ShowImportProposalOverlayCore()
    {
        ImportPasteBox.Text = "";
        ImportFileNameText.Text = "No file chosen";
        _importedFileText = "";
        _importedFileError = "";
        ImportValidationText.Visibility = Visibility.Collapsed;
        ImportValidationAiActions.Visibility = Visibility.Collapsed;
        ImportProposalOverlay.Visibility = Visibility.Visible;
        FadeIn(ImportProposalOverlay, 160);
        ImportPasteBox.Focus();
    }

    // Clears the imported-proposal marker/text so the student can import a fresh
    // one. Deliberately does NOT touch recommendations, the accepted plan, or the
    // proposal draft — only the "imported proposal" state.
    private void DeleteImportedProposal(ResearchProject p)
    {
        p.ProposalImported = false;
        p.ImportedProposalText = "";
        _lastImportText = "";
        _lastAiProposalInput = "";
        p.UpdatedAt = DateTime.UtcNow;
        SaveResearch();
        UpdateAiRecImportedBadge(p);
    }

    private void CancelImportProposal_Click(object sender, RoutedEventArgs e)
        => ImportProposalOverlay.Visibility = Visibility.Collapsed;

    private void ChooseProposalFile_Click(object sender, RoutedEventArgs e)
    {
        var ofd = new OpenFileDialog
        {
            Title = "Choose a proposal file",
            Filter = "Text and documents (*.txt;*.md;*.docx)|*.txt;*.md;*.docx|Text files (*.txt;*.md)|*.txt;*.md|Word document (*.docx)|*.docx|All files (*.*)|*.*",
            CheckFileExists = true
        };
        if (ofd.ShowDialog() != true) return;

        if (!TryLoadProposalFile(ofd.FileName, out string text, out string error))
        {
            // Keep any pasted text intact — it takes precedence at analyze time.
            _importedFileText = "";
            _importedFileError = error;
            ImportFileNameText.Text = Path.GetFileName(ofd.FileName) + " — could not be read";
            ShowImportValidation(error);
            return;
        }

        // Remember the file text separately. Only fill the paste box when it is
        // empty, so a non-empty paste is never clobbered by a chosen file.
        _importedFileText = text;
        _importedFileError = "";
        if (string.IsNullOrWhiteSpace(ImportPasteBox.Text))
            ImportPasteBox.Text = text;

        // Show only the file name, never the full path.
        ImportFileNameText.Text = Path.GetFileName(ofd.FileName);
        ImportValidationText.Visibility = Visibility.Collapsed;
    }

    // Reads .txt/.md as UTF-8 and .docx via its word/document.xml. PDFs and
    // images are intentionally deferred to a later OCR/vision update. Never logs
    // or prints the file contents.
    private bool TryLoadProposalFile(string path, out string text, out string error)
    {
        text = "";
        error = "";
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) { error = "That file could not be found."; return false; }
            if (info.Length == 0) { error = "That file is empty."; return false; }
            if (info.Length > ImportProposalMaxFileBytes)
            {
                error = "That file is too large. Please use a file under 5 MB or paste the text.";
                return false;
            }

            string ext = Path.GetExtension(path).ToLowerInvariant();
            switch (ext)
            {
                case ".txt":
                case ".md":
                    text = File.ReadAllText(path);
                    break;
                case ".docx":
                    // Word files vary a lot; if this simple reader can't get clean
                    // text, fail gracefully and ask the student to paste instead.
                    try
                    {
                        text = ExtractDocxText(path);
                    }
                    catch
                    {
                        error = "Could not read this Word file. Please paste the proposal text instead.";
                        return false;
                    }
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        error = "Could not read this Word file. Please paste the proposal text instead.";
                        return false;
                    }
                    break;
                case ".pdf":
                    error = "PDF import will require OCR/text support in a later update. Please paste the proposal text or use a .txt, .md, or .docx file.";
                    return false;
                case ".png":
                case ".jpg":
                case ".jpeg":
                case ".bmp":
                case ".tif":
                case ".tiff":
                    error = "Image and scanned-PDF extraction will require OCR/vision support in a later update. Please paste the proposal text instead.";
                    return false;
                default:
                    error = "Unsupported file type. Please use .txt, .md, or .docx, or paste the text.";
                    return false;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                error = "No readable text was found in that file. Please paste the proposal text instead.";
                text = "";
                return false;
            }

            return true;
        }
        catch
        {
            error = "That file could not be read. Please paste the proposal text instead.";
            text = "";
            return false;
        }
    }

    // Minimal, dependency-free .docx text extraction: unzip, read the main
    // document part, turn paragraphs/tabs/breaks into whitespace, strip the
    // remaining XML tags, and decode entities.
    private static string ExtractDocxText(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var entry = archive.GetEntry("word/document.xml");
        if (entry is null) return "";

        string xml;
        using (var reader = new StreamReader(entry.Open(), Encoding.UTF8))
            xml = reader.ReadToEnd();

        // Floating images/shapes/textboxes (letterhead logos, page borders, etc.)
        // are anchored with DrawingML/VML metadata whose ELEMENT TEXT (not
        // attributes) includes plain words like "left"/"right"/"center" and raw
        // EMU coordinate numbers. Because Word XML has no whitespace between
        // adjacent elements, stripping tags alone turns e.g.
        // "<wp:align>left</wp:align><wp:posOffset>-8685070</wp:posOffset>" into
        // the literal garbage "left-8685070" in the extracted text. Drop these
        // blocks entirely before stripping tags — they never contain body text.
        xml = Regex.Replace(xml, "<w:drawing>.*?</w:drawing>", "", RegexOptions.Singleline);
        xml = Regex.Replace(xml, "<mc:AlternateContent>.*?</mc:AlternateContent>", "", RegexOptions.Singleline);
        xml = Regex.Replace(xml, "<w:pict>.*?</w:pict>", "", RegexOptions.Singleline);

        // Paragraph and line breaks -> newlines; tabs -> spaces.
        xml = Regex.Replace(xml, "</w:p>", "\n", RegexOptions.IgnoreCase);
        xml = Regex.Replace(xml, "<w:br[^>]*/>", "\n", RegexOptions.IgnoreCase);
        xml = Regex.Replace(xml, "<w:tab[^>]*/>", "\t", RegexOptions.IgnoreCase);

        // Drop every remaining tag, then decode XML entities.
        string text = Regex.Replace(xml, "<[^>]+>", "");
        text = System.Net.WebUtility.HtmlDecode(text);

        // Collapse excessive blank lines, then run the shared layout-artifact
        // cleanup as a safety net for anything the block removal above missed.
        text = Regex.Replace(text, "\n{3,}", "\n\n");
        return CleanProposalText(text);
    }

    // Removes leftover Word/layout artifacts and normalizes whitespace before
    // ANY proposal text (pasted or extracted from a file) is sent to Research
    // AI. Centralized here so both import paths get the same safety net.
    private static string CleanProposalText(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        string t = raw;

        // Leaked drawing-anchor tokens: an alignment word immediately followed by
        // a large EMU/twip coordinate number with no separator, e.g.
        // "left868507", "right-4765680", "center4585400".
        t = Regex.Replace(t, @"\b(left|right|center|top|bottom|inline)-?\d{3,}\b", " ", RegexOptions.IgnoreCase);

        // Bare oversized coordinate-looking numbers left isolated on their own.
        t = Regex.Replace(t, @"(?<![\w.,])-?\d{6,}(?![\w.,])", " ");

        // Collapse whitespace: repeated spaces/tabs, trailing spaces before a
        // newline, and more than one blank line in a row.
        t = Regex.Replace(t, @"[ \t]{2,}", " ");
        t = Regex.Replace(t, @"[ \t]+\n", "\n");
        t = Regex.Replace(t, @"(\r?\n){3,}", "\n\n");

        return t.Trim();
    }

    // Result of building the compact AI input from a full proposal. Only carries
    // sizes/flags — never any proposal text — so it is safe to log or display.
    private struct ProposalCompaction
    {
        public int OriginalChars;
        public int AiChars;
        public bool ReferencesRemoved;
        public bool Truncated;
        public string ToSummary()
            => $"origChars={OriginalChars} aiChars={AiChars} refsRemoved={ReferencesRemoved} truncated={Truncated}";
    }

    // Matches a line that is ONLY a References/Bibliography-style heading. Allows
    // an optional leading number ("7. References") and trailing colon. Kept strict
    // (whole-line) so a sentence merely mentioning "references" is not treated as
    // the start of the reference list.
    private static readonly Regex ReferencesHeadingRegex = new(
        @"^\s*(?:\d+[\.\)]\s*)?(references|reference list|bibliography|works cited|literature cited|citations)\s*:?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // True for citation-style lines that are just a DOI or mostly a URL. These are
    // reference leftovers and must never be sent to the AI for extraction.
    private static bool IsLinkOrDoiHeavyLine(string trimmedLine)
    {
        if (trimmedLine.Length == 0) return false;
        if (trimmedLine.IndexOf("doi.org", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        if (Regex.IsMatch(trimmedLine, @"\bdoi\s*:", RegexOptions.IgnoreCase)) return true;

        var urls = Regex.Matches(trimmedLine, @"https?://\S+|www\.\S+", RegexOptions.IgnoreCase);
        if (urls.Count == 0) return false;
        int urlChars = 0;
        foreach (Match m in urls) urlChars += m.Value.Length;
        int nonSpace = trimmedLine.Count(c => !char.IsWhiteSpace(c));
        return nonSpace > 0 && (double)urlChars / nonSpace >= 0.5;
    }

    // Builds the compact, reference-free copy of the proposal that is sent to
    // Research AI. This is the core timeout fix: the model receives only the
    // essential proposal content, never the long References/Bibliography/DOI list
    // or the raw full document. The caller keeps the full original for the user.
    private static string BuildAiProposalInput(string cleanedFullText, out ProposalCompaction info)
    {
        info = new ProposalCompaction { OriginalChars = cleanedFullText?.Length ?? 0 };
        if (string.IsNullOrWhiteSpace(cleanedFullText)) { info.AiChars = 0; return ""; }

        var lines = cleanedFullText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var kept = new List<string>(lines.Length);
        bool refsRemoved = false;

        foreach (var line in lines)
        {
            // Stop at the first References/Bibliography heading — everything after
            // it (author lists, journals, DOIs) is dropped from the AI input.
            if (ReferencesHeadingRegex.IsMatch(line)) { refsRemoved = true; break; }

            string trimmed = line.Trim();
            if (trimmed.Length == 0) { kept.Add(""); continue; }

            // Drop stray DOI/URL-heavy citation lines that appear in the body.
            if (IsLinkOrDoiHeavyLine(trimmed)) { refsRemoved = true; continue; }

            kept.Add(line);
        }

        string result = Regex.Replace(string.Join("\n", kept), @"(\r?\n){3,}", "\n\n").Trim();
        info.ReferencesRemoved = refsRemoved;

        // Size protection. References are the main bloat, so this rarely triggers
        // once they are removed. When it does, keep the HEAD (title/abstract/
        // background/objectives/methods) and the TAIL (analysis plan/ethics/
        // limitations usually sit near the end), dropping the middle — which
        // honours "prefer methods/objectives/variables/analysis over background".
        if (result.Length > AiProposalInputMaxChars)
        {
            int headBudget = (int)(AiProposalInputMaxChars * 0.75);
            int tailBudget = AiProposalInputMaxChars - headBudget;

            string head = result.Substring(0, headBudget);
            int hNl = head.LastIndexOf('\n');
            if (hNl > headBudget / 2) head = head.Substring(0, hNl);

            string tail = result.Substring(result.Length - tailBudget);
            int tNl = tail.IndexOf('\n');
            if (tNl >= 0 && tNl < tailBudget / 2) tail = tail.Substring(tNl + 1);

            result = head.TrimEnd() + "\n\n[... middle section trimmed to fit the AI limit ...]\n\n" + tail.TrimStart();
            info.Truncated = true;
        }

        info.AiChars = result.Length;
        return result;
    }

    // showAiActions=true adds "Open Research AI Settings"/"Open details" next to
    // the message — only for an actual AI call failure, not a plain client-side
    // validation issue (empty text, too long, not configured).
    private void ShowImportValidation(string message, bool showAiActions = false)
    {
        ImportValidationText.Text = message;
        ImportValidationText.Visibility = Visibility.Visible;
        ImportValidationAiActions.Visibility = showAiActions ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void AnalyzeProposal_Click(object sender, RoutedEventArgs e)
    {
        var p = CurrentResearchProject();
        if (p is null) { ImportProposalOverlay.Visibility = Visibility.Collapsed; return; }

        // Prefer pasted text; fall back to a chosen file's text only when nothing
        // was pasted. A non-empty paste is always used, even if a file is selected
        // (and even if that file failed to read).
        string pasted = (ImportPasteBox.Text ?? "").Trim();
        string text = pasted.Length > 0 ? pasted : (_importedFileText ?? "").Trim();
        // Safety-net cleanup regardless of source: .docx text is already cleaned
        // in ExtractDocxText, but pasted text (e.g. copied from a PDF viewer or
        // another tool) can carry the same kind of layout artifacts.
        text = CleanProposalText(text);

        if (text.Length == 0)
        {
            // Empty overall — point at the most helpful reason.
            ShowImportValidation(!string.IsNullOrEmpty(_importedFileError)
                ? _importedFileError
                : "Proposal text is empty. Paste your proposal text, or choose a readable .txt, .md, or .docx file.");
            return;
        }
        if (text.Length < ImportProposalMinChars)
        {
            ShowImportValidation("Please paste or upload a longer proposal (at least a few sentences).");
            return;
        }
        if (text.Length > ImportProposalMaxChars)
        {
            ShowImportValidation($"That proposal is too long ({text.Length:N0} characters). Please trim it to under {ImportProposalMaxChars:N0} characters.");
            return;
        }
        if (!_researchAi.IsConfigured)
        {
            ShowImportValidation("Research AI is not configured yet. Add an endpoint or enable a development provider in Settings.");
            return;
        }

        // Build the compact, reference-free copy that actually goes to the AI. The
        // full `text` above is kept for the user; only this slimmed copy is sent,
        // which is the core timeout fix.
        string aiInput = BuildAiProposalInput(text, out ProposalCompaction info);

        // If compaction stripped almost everything (a proposal that was nearly all
        // references/links), fall back to a size-capped copy of the full text so
        // we still send meaningful content rather than an empty prompt.
        if (aiInput.Length < ImportProposalMinChars && text.Length >= ImportProposalMinChars)
        {
            aiInput = text.Length > AiProposalInputMaxChars ? text.Substring(0, AiProposalInputMaxChars) : text;
            info.ReferencesRemoved = false;
            info.AiChars = aiInput.Length;
        }

        // Preserve the ORIGINAL full text for storage/display. A review-screen
        // retry re-enters this method with the compact text already in the box;
        // detect that so the stored original is not overwritten by its own
        // compacted copy. The compact copy is what we send and reuse on retry.
        bool reanalyzingCompact = _lastAiProposalInput.Length > 0
            && string.Equals(text, _lastAiProposalInput, StringComparison.Ordinal);
        if (!reanalyzingCompact) _lastImportText = text;
        _lastAiProposalInput = aiInput;

        // Safe, key-free diagnostics: original vs cleaned counts + refs-removed
        // flag. Written to research_ai.log and kept for the "Open details" action.
        // Never contains any proposal text.
        _lastProposalCompactionSummary = "proposalInput: " + info.ToSummary();
        _lastAiDiagnostics = _lastProposalCompactionSummary;
        ResearchAiDiagnostics.Log("ImportProposalInput", "app", 0, 0, outcome: info.ToSummary());

        ImportValidationText.Visibility = Visibility.Collapsed;
        AnalyzeProposalBtn.IsEnabled = false;
        var ct = BeginAiWork("Analyzing Imported Proposal",
            "Preparing proposal", "Removing references", "Sending request",
            "Waiting for Research AI", "Validating JSON", "Preparing import review");

        try
        {
            AiWorkAdvance();   // proposal cleaned (done above, before the modal)
            AiWorkAdvance();   // references removed (compaction above)
            AiWorkAdvance();   // request sent → waiting
            var result = await _researchAi.ExtractProposalAsync(aiInput, p, ct);
            AiWorkAdvance();   // response received + JSON parsed → validating
            _currentExtraction = result;
            AiWorkAdvance();   // validated → preparing review

            ImportProposalOverlay.Visibility = Visibility.Collapsed;
            ShowReviewScreen(result);
            await CompleteAiWorkAsync();
            if (info.ReferencesRemoved)
                ShowToast("References were excluded from AI analysis to reduce processing time.");
        }
        catch (OperationCanceledException)
        {
            ShowToast("Cancelled. Your existing content was kept.");
        }
        catch (ResearchAiNotConfiguredException)
        {
            ShowImportValidation("Research AI is not configured yet. Add an endpoint or enable a development provider in Settings.");
        }
        catch (ResearchAiException ex)
        {
            // Nothing was applied yet, so any previously imported proposal, plan,
            // or extraction sheet is untouched. Show a specific, key-free reason;
            // give the friendlier import-specific guidance on a timeout.
            _lastAiDiagnostics = _lastProposalCompactionSummary + "\n" + (ex.Diagnostics ?? "");
            string msg = ex.IsTimeout
                ? "Research AI did not finish within the allowed time. Your content was kept. You can retry or continue manually. Try again with a shorter proposal text or remove references."
                : ex.Message;
            if (info.ReferencesRemoved)
                msg += "\nReferences were excluded from AI analysis to reduce processing time.";
            ShowImportValidation(msg, showAiActions: true);
        }
        catch (Exception)
        {
            ShowImportValidation("Research AI could not complete this action. Your previous content was kept.", showAiActions: true);
        }
        finally
        {
            EndAiWork();
            AnalyzeProposalBtn.IsEnabled = true;
        }
    }

    // =====================================================================
    // Research Lab (Phase 2G/H) — Review & Apply extracted details
    // =====================================================================

    private void ShowReviewScreen(ProposalExtractionResult r)
    {
        // Editable core fields (prefilled from the extraction).
        RvTitle.Text = r.ExtractedTitle;
        RvSpecialty.Text = r.ExtractedSpecialty;
        RvStudyDesign.Text = r.ExtractedStudyDesign;
        RvPopulation.Text = r.ExtractedPopulation;
        RvSetting.Text = r.ExtractedSetting;
        RvTimePeriod.Text = r.ExtractedTimePeriod;
        RvResearchQuestion.Text = r.ExtractedResearchQuestion;
        RvAim.Text = r.ExtractedAim;
        RvPrimaryObjective.Text = r.ExtractedPrimaryObjective;

        RvSecondaryObjectives.Text = r.ExtractedSecondaryObjectives.Count > 0
            ? "Secondary objectives:\n" + BulletLines(r.ExtractedSecondaryObjectives)
            : "Secondary objectives: none found.";

        var sec = r.ExtractedProposalSections;
        RvMethods.Text = FirstFilled(
            (sec.Methods, "Methods"),
            (sec.StudyDesign, "Study design"),
            (r.ExtractedStudyDesign, "Study design"));

        RvCriteria.Text = JoinReview(
            ("Inclusion criteria", r.ExtractedInclusionCriteria),
            ("Exclusion criteria", r.ExtractedExclusionCriteria));
        if (string.IsNullOrWhiteSpace(RvCriteria.Text)) RvCriteria.Text = "No inclusion/exclusion criteria found.";

        RvVariablesAnalyses.Text = BuildVariablesAnalysesReview(r);
        if (string.IsNullOrWhiteSpace(RvVariablesAnalyses.Text)) RvVariablesAnalyses.Text = "No variables or analyses found.";

        RvEthicsLimitations.Text = JoinReview(
            ("Ethics", r.ExtractedEthics),
            ("Limitations", r.ExtractedLimitations));
        if (!string.IsNullOrWhiteSpace(r.ExtractedTimeline))
            RvEthicsLimitations.Text = (RvEthicsLimitations.Text + "\nTimeline: " + r.ExtractedTimeline).Trim();
        if (string.IsNullOrWhiteSpace(RvEthicsLimitations.Text)) RvEthicsLimitations.Text = "No ethics or limitations found.";

        RvProposalSections.Text = BuildProposalSectionsReview(sec);
        if (string.IsNullOrWhiteSpace(RvProposalSections.Text)) RvProposalSections.Text = "No full proposal sections were extracted.";

        bool hasMissing = r.MissingOrWeakSections.Count > 0;
        RvMissingCard.Visibility = hasMissing ? Visibility.Visible : Visibility.Collapsed;
        RvMissing.Text = hasMissing ? BulletLines(r.MissingOrWeakSections) : "";

        RvConfidence.Text = string.IsNullOrWhiteSpace(r.ConfidenceSummary)
            ? "Review every field before applying. AI extraction can miss or misread details."
            : r.ConfidenceSummary;
        RvWarnings.Text = r.Warnings.Count > 0 ? BulletLines(r.Warnings) : "";

        ReviewExtractionOverlay.Visibility = Visibility.Visible;
        FadeIn(ReviewExtractionOverlay, 160);
    }

    private void CancelReview_Click(object sender, RoutedEventArgs e)
        => ReviewExtractionOverlay.Visibility = Visibility.Collapsed;

    private void ReanalyzeExtraction_Click(object sender, RoutedEventArgs e)
    {
        // Reopen the import dialog for a quick retry. Reuse the compact,
        // reference-free input (never the huge raw document) so the retry is fast
        // and cannot re-trigger the timeout; fall back to the last full text only
        // if no compact copy exists yet.
        ReviewExtractionOverlay.Visibility = Visibility.Collapsed;
        ImportPasteBox.Text = _lastAiProposalInput.Length > 0 ? _lastAiProposalInput : _lastImportText;
        ImportFileNameText.Text = "No file chosen";
        ImportValidationText.Visibility = Visibility.Collapsed;
        ImportValidationAiActions.Visibility = Visibility.Collapsed;
        ImportProposalOverlay.Visibility = Visibility.Visible;
        FadeIn(ImportProposalOverlay, 140);
    }

    private void ApplyExtraction_Click(object sender, RoutedEventArgs e)
    {
        var p = CurrentResearchProject();
        if (p is null || _currentExtraction is null)
        {
            ReviewExtractionOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        // If the project already carries a plan or proposal, confirm before we
        // overwrite any of those fields.
        if (HasExistingRecommendations(p) || p.ProposalDraft is not null)
        {
            ShowRlConfirm(
                "Apply extracted details?",
                "This project already has a research plan or proposal draft. Applying extracted details may replace some fields. Continue?",
                "Apply",
                () => AskAcceptThenApply(p));
            return;
        }

        AskAcceptThenApply(p);
    }

    // Ask whether to mark the extracted plan as the accepted research plan, then
    // apply either way.
    private void AskAcceptThenApply(ResearchProject p)
    {
        ShowRlConfirm(
            "Mark as accepted research plan?",
            "Do you want to mark this extracted research plan as your accepted plan? You can still edit it afterwards. Choose Cancel to apply the details without accepting the plan yet.",
            "Mark as accepted",
            onConfirm: () => ApplyExtractionCore(p, markAccepted: true),
            onCancel: () => ApplyExtractionCore(p, markAccepted: false));
    }

    private void ApplyExtractionCore(ResearchProject p, bool markAccepted)
    {
        var r = _currentExtraction;
        if (r is null) { ReviewExtractionOverlay.Visibility = Visibility.Collapsed; return; }

        // --- Project details (use the possibly-edited review fields) ---
        if (!string.IsNullOrWhiteSpace(RvTitle.Text)) p.Title = RvTitle.Text.Trim();
        if (!string.IsNullOrWhiteSpace(RvSpecialty.Text)) p.Specialty = RvSpecialty.Text.Trim();
        if (!string.IsNullOrWhiteSpace(RvAim.Text)) p.Aim = RvAim.Text.Trim();
        if (!string.IsNullOrWhiteSpace(RvPopulation.Text)) p.Population = RvPopulation.Text.Trim();
        if (!string.IsNullOrWhiteSpace(RvSetting.Text)) p.Setting = RvSetting.Text.Trim();
        if (!string.IsNullOrWhiteSpace(RvTimePeriod.Text)) p.TimePeriod = RvTimePeriod.Text.Trim();

        // --- Recommendations from the extracted plan ---
        var rec = new ResearchRecommendations
        {
            SourceMode = ResearchSourceMode.AiGenerated,
            RefinedResearchTitle = RvTitle.Text.Trim(),
            RecommendedStudyDesign = RvStudyDesign.Text.Trim(),
            ResearchQuestion = RvResearchQuestion.Text.Trim(),
            PrimaryObjective = RvPrimaryObjective.Text.Trim(),
            SecondaryObjectives = new List<string>(r.ExtractedSecondaryObjectives),
            SuggestedVariables = new List<ResearchVariableSuggestion>(r.ExtractedVariables),
            SuggestedAnalyses = new List<ResearchAnalysisSuggestion>(r.ExtractedSuggestedAnalyses),
            InclusionCriteria = new List<string>(r.ExtractedInclusionCriteria),
            ExclusionCriteria = new List<string>(r.ExtractedExclusionCriteria),
            DataCollectionSuggestions = new List<string>(r.ExtractedDataCollection),
            EthicsNotes = new List<string>(r.ExtractedEthics),
            BiasAndLimitations = new List<string>(r.ExtractedLimitations),
            AcceptedIntoPlan = markAccepted
        };

        // Only replace recommendations if the extraction actually produced plan
        // content; otherwise keep whatever the project already had.
        if (rec.HasStructuredContent)
            p.Recommendations = rec;

        if (markAccepted && p.Recommendations is not null && p.Recommendations.HasStructuredContent)
        {
            p.Recommendations.AcceptedIntoPlan = true;
            // Rebuild the editable plan from the freshly imported recommendations.
            p.Plan = BuildPlanFromRecommendations(p, p.Recommendations);
        }

        // --- Proposal draft from the extracted sections ---
        var sec = r.ExtractedProposalSections;
        if (sec.HasAnyContent || !string.IsNullOrWhiteSpace(RvTitle.Text))
        {
            var d = p.ProposalDraft ??= new ResearchProposalDraft();
            d.SourceMode = ResearchSourceMode.AiGenerated;
            d.IsTemplateGenerated = false;
            d.Title = FirstNonEmpty(RvTitle.Text, sec.Aim, p.Title);
            d.Background = sec.Background;
            d.Rationale = sec.Rationale;
            d.Aim = FirstNonEmpty(sec.Aim, RvAim.Text);
            d.Objectives = sec.Objectives;
            d.Methods = sec.Methods;
            d.StudyDesign = FirstNonEmpty(sec.StudyDesign, RvStudyDesign.Text);
            d.Setting = FirstNonEmpty(sec.Setting, RvSetting.Text);
            d.Population = FirstNonEmpty(sec.Population, RvPopulation.Text);
            d.InclusionCriteria = sec.InclusionCriteria;
            d.ExclusionCriteria = sec.ExclusionCriteria;
            d.Variables = sec.Variables;
            d.DataCollection = sec.DataCollection;
            d.StatisticalAnalysisPlan = sec.StatisticalAnalysisPlan;
            d.Ethics = sec.Ethics;
            d.Timeline = FirstNonEmpty(sec.Timeline, r.ExtractedTimeline);
            d.Limitations = sec.Limitations;
            d.UpdatedAt = DateTime.UtcNow;
        }

        // --- Mark as imported so the workflow (import-first prompt, recs badge,
        //     recommendations-from-proposal) behaves correctly ---
        p.ProposalImported = true;
        if (!string.IsNullOrWhiteSpace(_lastImportText))
            p.ImportedProposalText = _lastImportText.Length > ImportProposalMaxChars
                ? _lastImportText.Substring(0, ImportProposalMaxChars)
                : _lastImportText;

        // --- Stage (progress derives from artifacts in SaveResearch) ---
        p.DetailsChangedSinceRecommendations = false;
        if (p.ProposalDraft is not null)
            p.CurrentStage = markAccepted ? "Proposal imported" : "Proposal draft imported";
        else if (p.Recommendations is not null)
            p.CurrentStage = markAccepted ? "Research plan ready" : "Recommendations imported";
        p.UpdatedAt = DateTime.UtcNow;
        SaveResearch();

        ReviewExtractionOverlay.Visibility = Visibility.Collapsed;
        _currentExtraction = null;

        // --- Refresh every affected view (Part H integration) ---
        PopulateOverview(p);
        PopulateAiSnapshot(p);
        DashTitleText.Text = p.Title;
        DashSubtitleText.Text = $"{p.SpecialtyDisplay}  •  {p.StudyTypeDisplay}";
        RenderRecommendations(p.Recommendations);
        PopulatePlanEditor(p);
        PopulateProposalEditor(p);
        UpdateProposalPlanSource(p);
        UpdateOverviewProgress(p);
        UpdateGenerateRecButton(p);
        UpdateAiRecImportedBadge(p);
        DashNextStepText.Text = "Next: review the extracted plan, then prepare your data extraction sheet.";

        ShowToast("Extracted details applied. Review everything before you continue.");
    }

    // ---- Review formatting helpers ----------------------------------------

    private static string BulletLines(IEnumerable<string> items)
        => string.Join("\n", items.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => "•  " + s.Trim()));

    private static string FirstFilled(params (string Value, string Label)[] options)
    {
        foreach (var (value, _) in options)
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        return "No study design or methods found.";
    }

    private static string JoinReview(params (string Label, List<string> Items)[] groups)
    {
        var sb = new StringBuilder();
        foreach (var (label, items) in groups)
        {
            if (items is null || items.Count == 0) continue;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(label).Append(":\n").Append(BulletLines(items));
        }
        return sb.ToString().Trim();
    }

    private static string BuildVariablesAnalysesReview(ProposalExtractionResult r)
    {
        var sb = new StringBuilder();
        if (r.ExtractedVariables.Count > 0)
        {
            sb.Append("Variables:\n");
            foreach (var v in r.ExtractedVariables)
            {
                string meta = v.MetaDisplay == "—" ? "" : " (" + v.MetaDisplay + ")";
                sb.Append("•  ").Append(v.HeaderDisplay).Append(meta).Append('\n');
            }
        }
        if (r.ExtractedSuggestedAnalyses.Count > 0)
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append("Analyses:\n");
            foreach (var a in r.ExtractedSuggestedAnalyses)
                sb.Append("•  ").Append(a.HeaderDisplay).Append('\n');
        }
        return sb.ToString().Trim();
    }

    private static string BuildProposalSectionsReview(ExtractedProposalSections s)
    {
        var sb = new StringBuilder();
        void Add(string label, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            if (sb.Length > 0) sb.Append("\n\n");
            sb.Append(label).Append(":  ").Append(value.Trim());
        }
        Add("Background", s.Background);
        Add("Rationale", s.Rationale);
        Add("Aim", s.Aim);
        Add("Objectives", s.Objectives);
        Add("Methods", s.Methods);
        Add("Study design", s.StudyDesign);
        Add("Setting", s.Setting);
        Add("Population", s.Population);
        Add("Inclusion criteria", s.InclusionCriteria);
        Add("Exclusion criteria", s.ExclusionCriteria);
        Add("Variables", s.Variables);
        Add("Data collection", s.DataCollection);
        Add("Statistical analysis plan", s.StatisticalAnalysisPlan);
        Add("Ethics", s.Ethics);
        Add("Timeline", s.Timeline);
        Add("Limitations", s.Limitations);
        return sb.ToString();
    }

    // =====================================================================
    // Research Lab (Phase 3) — CSV sample import + local inference
    //
    // Only a privacy-safe SUMMARY is ever built: headers, inferred types, a few
    // example values, unique/missing counts, and the total row count. The full
    // CSV is never stored on the project and never sent to the AI. No statistics
    // are computed — this is column profiling only, to help build the sheet.
    // =====================================================================

    private bool TryLoadCsvSample(string path, out CsvSampleSummary summary, out string error)
    {
        summary = new CsvSampleSummary();
        error = "";
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) { error = "That file could not be found."; return false; }
            if (info.Length == 0) { error = "That CSV file is empty."; return false; }
            if (info.Length > CsvMaxFileBytes) { error = "That CSV is too large. Please use a file under 10 MB, or a smaller sample export."; return false; }

            string[]? headers = null;
            var sampleRows = new List<string[]>();
            int totalDataRows = 0;

            foreach (string line in File.ReadLines(path))
            {
                if (headers is null)
                {
                    // Skip leading blank lines before the header.
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    headers = ParseCsvLine(line);
                    continue;
                }
                if (string.IsNullOrWhiteSpace(line)) continue;
                totalDataRows++;
                if (sampleRows.Count < CsvMaxSampleRows)
                    sampleRows.Add(ParseCsvLine(line));
            }

            if (headers is null || headers.Length == 0) { error = "No column headers were found in that CSV."; return false; }

            summary.FileName = Path.GetFileName(path);
            summary.TotalRows = totalDataRows;
            summary.SampledRows = sampleRows.Count;
            summary.Columns = BuildCsvColumnSummaries(headers, sampleRows);
            return true;
        }
        catch
        {
            error = "That CSV could not be read. Please check the file and try again, or paste your questions instead.";
            return false;
        }
    }

    // Minimal RFC-4180-ish splitter: handles quoted fields, embedded commas, and
    // doubled quotes. Does not handle newlines inside quotes (rare in exports).
    private static string[] ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuotes = false;
                }
                else sb.Append(c);
            }
            else
            {
                if (c == '"') inQuotes = true;
                else if (c == ',') { fields.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(c);
            }
        }
        fields.Add(sb.ToString());
        return fields.Select(f => f.Trim()).ToArray();
    }

    private static List<CsvColumnSummary> BuildCsvColumnSummaries(string[] headers, List<string[]> rows)
    {
        var cols = new List<CsvColumnSummary>();
        for (int c = 0; c < headers.Length; c++)
        {
            string name = string.IsNullOrWhiteSpace(headers[c]) ? $"column_{c + 1}" : headers[c].Trim();
            var values = new List<string>();
            int missing = 0;
            foreach (var row in rows)
            {
                string val = c < row.Length ? row[c].Trim() : "";
                if (IsMissingToken(val)) { missing++; continue; }
                values.Add(val);
            }

            var distinct = values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            int sampled = rows.Count == 0 ? 1 : rows.Count;
            var summary = new CsvColumnSummary
            {
                Name = name,
                MissingCount = missing,
                MissingPercent = (int)Math.Round(100.0 * missing / sampled),
                UniqueCount = distinct.Count,
                SampleValues = distinct.Take(5).ToList()
            };
            summary.InferredType = InferColumnType(values, distinct, out bool likelyCategorical);
            summary.IsLikelyCategorical = likelyCategorical;
            cols.Add(summary);
        }
        return cols;
    }

    // Delegates to the one shared missing-value list so the import summary and
    // the statistics engine can never disagree about what counts as missing.
    // This previously kept its own list, which omitted "unknown" and
    // "not available" and so reported a different N than the analysis used.
    private static bool IsMissingToken(string v) => StatisticsMissingTokens.IsMissing(v);

    private static string InferColumnType(List<string> values, List<string> distinct, out bool likelyCategorical)
    {
        likelyCategorical = false;
        if (values.Count == 0) return "Empty";

        bool allNumeric = values.All(v => double.TryParse(v, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out _));
        bool allDate = values.All(v => DateTime.TryParse(v, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _));

        if (distinct.Count == 2) { likelyCategorical = true; return "Binary"; }
        if (allNumeric) return "Numeric";
        if (allDate) return "Date";
        if (distinct.Count <= 10) { likelyCategorical = true; return "Categorical"; }
        return "Text";
    }

    // Builds the safe text summary that is sent to the Research AI (never the raw
    // data). Kept here so the privacy boundary is easy to audit.
    private static string DescribeCsvForUser(CsvSampleSummary s)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{s.FileName} — {s.CountSummary} (first {s.SampledRows} rows sampled).");
        foreach (var c in s.Columns)
            sb.AppendLine("• " + c.Display);
        return sb.ToString().Trim();
    }

    // =====================================================================
    // Research Lab (Phase 3) — local validation engine (no statistics)
    // =====================================================================

    private static readonly Regex ValidNameRegex = new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    private ExtractionValidationReport ValidateExtractionSheet(ResearchProject p)
    {
        var report = new ExtractionValidationReport();
        var vars = p.Variables ?? new List<ResearchVariable>();

        // Warnings/suggestions the student has resolved or ignored are dropped by
        // stable key, so they do not reappear here (errors are never suppressed —
        // they block progress). Keys match ConflictKey so a decision made in the
        // Resolve Conflicts window also clears the matching Validation warning.
        void Warn(string kind, string identity, string message)
        {
            if (!_ignoredConflictKeys.Contains(MakeConflictKey(kind, identity)))
                report.Warnings.Add(message);
        }
        void Suggest(string kind, string identity, string message)
        {
            if (!_ignoredConflictKeys.Contains(MakeConflictKey(kind, identity)))
                report.Suggestions.Add(message);
        }

        if (vars.Count == 0)
        {
            report.Errors.Add("The extraction sheet is empty. Add variables, generate them with Research AI, or import from your proposal.");
            return report;
        }

        // Duplicate names (case-insensitive, non-empty).
        foreach (var grp in vars.Where(v => !string.IsNullOrWhiteSpace(v.VariableName))
                                 .GroupBy(v => v.VariableName.Trim().ToLowerInvariant())
                                 .Where(g => g.Count() > 1))
            report.Errors.Add($"Duplicate variable name '{grp.First().VariableName.Trim()}' is used {grp.Count()} times. Names must be unique.");

        int index = 0;
        foreach (var v in vars)
        {
            index++;
            string name = (v.VariableName ?? "").Trim();
            string label = name.Length == 0 ? $"Row {index}" : $"'{name}'";

            if (name.Length == 0)
                report.Errors.Add($"Row {index} has no variable name.");
            else if (!ValidNameRegex.IsMatch(name))
                report.Errors.Add($"Variable name {label} is invalid — use letters, numbers and underscores only, with no spaces or symbols (e.g. sleep_hours).");

            if (string.IsNullOrWhiteSpace(v.VariableType))
                report.Errors.Add($"Variable {label} has no type.");
            else if (string.Equals(v.VariableType.Trim(), "Unknown", StringComparison.OrdinalIgnoreCase))
                Warn("Meta", name, $"Variable {label} has an unknown type — set it to a specific type.");

            if (string.IsNullOrWhiteSpace(v.Role))
                report.Errors.Add($"Variable {label} has no role.");
            else if (string.Equals(v.Role.Trim(), "Unknown", StringComparison.OrdinalIgnoreCase))
                Warn("Meta", name, $"Variable {label} has an unknown role — mark it (outcome, exposure, demographic, …).");

            bool needsCoding = v.VariableType is not null &&
                (v.VariableType.Equals("Categorical", StringComparison.OrdinalIgnoreCase)
                 || v.VariableType.Equals("Binary", StringComparison.OrdinalIgnoreCase)
                 || v.VariableType.Equals("Ordinal", StringComparison.OrdinalIgnoreCase));
            if (needsCoding && string.IsNullOrWhiteSpace(v.Coding) && string.IsNullOrWhiteSpace(v.ValueLabels))
                Warn("Coding", name, $"Variable {label} is {(v.VariableType ?? "categorical").ToLowerInvariant()} but has no value labels (e.g. 0 = No, 1 = Yes).");
        }

        // Outcome / demographic coverage.
        if (!vars.Any(v => v.Role.Equals("Outcome", StringComparison.OrdinalIgnoreCase)))
            Warn("Outcome", "", "No primary outcome variable is marked. Mark the variable that answers your research question as 'Outcome'.");
        if (!vars.Any(v => v.Role.Equals("Demographic", StringComparison.OrdinalIgnoreCase)))
            Warn("Demographic", "", "No demographic variables are present. Most studies collect basic demographics (age, sex, …).");
        if (!vars.Any(v => v.Role.Equals("Identifier", StringComparison.OrdinalIgnoreCase) || v.VariableType.Equals("ID", StringComparison.OrdinalIgnoreCase)))
            Suggest("Identifier", "", "Consider adding an anonymised participant ID so each record can be tracked without direct identifiers.");

        // CSV cross-checks via robust matching (name / question label / alias /
        // fuzzy). Google system columns (Timestamp, …) are ignored, not flagged.
        if (p.CsvSampleSummary is { Columns.Count: > 0 } csvSummary)
        {
            var match = MatchCsvToSheet(vars, csvSummary);
            foreach (var col in match.CsvOnly)
                Warn("CsvOnly", col.Name, $"Your CSV has a column '{col.Name}' but it is not in the extraction sheet.");
            foreach (var v in match.SheetOnly)
                Warn("SheetOnly", v.VariableName, $"Variable '{v.VariableName.Trim()}' is in the sheet but not found as a column in the uploaded CSV.");
        }

        // Proposal-implied variables not represented (concrete suggestion).
        if (p.Recommendations is { SuggestedVariables.Count: > 0 })
        {
            var sheetTokens = vars.SelectMany(v => new[] { v.VariableName, v.QuestionLabel })
                                  .Where(s => !string.IsNullOrWhiteSpace(s))
                                  .Select(s => s.Trim().ToLowerInvariant()).ToList();
            foreach (var rv in p.Recommendations.SuggestedVariables)
            {
                string key = rv.HeaderDisplay.Trim().ToLowerInvariant();
                if (key.Length < 3) continue;
                bool present = sheetTokens.Any(t => t.Contains(key) || key.Contains(t));
                if (!present)
                    Suggest("PlanVar", rv.HeaderDisplay, $"The research plan mentions '{rv.HeaderDisplay.Trim()}', but no matching variable was found in the sheet.");
            }
        }

        // Sample count vs target — a plain comparison, never a calculation. An
        // INCOMPLETE sample (fewer CSV rows than the proposal target) is a HARD
        // blocker for Statistics, so it is an ERROR that cannot be dismissed or
        // ignored. If no target is known, it is advisory only (never blocks).
        int? target = p.TargetSampleSize;
        if (target is null && TryExtractTargetSampleSize(p, out int detected)) target = detected;
        if (p.CsvSampleSummary is { TotalRows: > 0 } csv)
        {
            if (target is > 0)
            {
                if (csv.TotalRows < target)
                {
                    report.SampleSizeIncomplete = true;
                    report.Errors.Add($"Sample size is incomplete: your uploaded CSV contains {csv.TotalRows} samples, "
                        + $"but the proposal target sample size is {target}. Please upload a complete CSV file with all "
                        + $"required samples ({target - csv.TotalRows} more) before running Statistics.");
                }
            }
            else
            {
                // No target found — advisory warning only, never a blocker.
                Warn("SampleNoTarget", "", "No target sample size was found. Confirm your sample size before running statistics.");
            }
        }

        return report;
    }

    // The stable key of the sample-incomplete blocker. Research AI is never
    // allowed to "ignore" or "mark resolved" this — more real data is required.
    private static string SampleIncompleteKey => MakeConflictKey("SampleIncomplete", "");

    // Best-effort, conservative detection of a stated target sample size in the
    // imported proposal / plan. Never computes a sample size — only reads a number
    // the student already wrote near a sample-size keyword.
    private bool TryExtractTargetSampleSize(ResearchProject p, out int target)
    {
        target = 0;
        var sources = new List<string?>
        {
            p.ImportedProposalText,
            p.ProposalDraft?.StatisticalAnalysisPlan,
            p.ProposalDraft?.Methods,
            p.ProposalDraft?.DataCollection,
            p.Plan?.MainVariables,
            p.Notes
        };
        string text = string.Join("\n", sources.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (string.IsNullOrWhiteSpace(text)) return false;

        var patterns = new[]
        {
            @"sample\s*size\s*(?:of|:|=|is|was|will\s*be)?\s*(?:approximately\s*|about\s*|~\s*)?(\d{2,7})",
            @"target\s*(?:sample|of|:)?\s*(?:approximately\s*|about\s*|~\s*)?(\d{2,7})",
            @"\bn\s*[=:]\s*(\d{2,7})",
            @"(\d{2,7})\s*(?:participants|patients|students|subjects|respondents|cases|records)"
        };
        foreach (var pat in patterns)
        {
            var m = Regex.Match(text, pat, RegexOptions.IgnoreCase);
            if (m.Success && int.TryParse(m.Groups[1].Value, out int n) && n is >= 10 and <= 1_000_000)
            {
                target = n;
                return true;
            }
        }
        return false;
    }

    // =====================================================================
    // Research Lab (Phase 3) — Google Form link (best-effort, safe, no OAuth)
    // =====================================================================

    private static bool IsGoogleFormUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return false;
        string host = uri.Host.ToLowerInvariant();
        return host is "docs.google.com" or "forms.gle" or "www.google.com" || host.EndsWith(".google.com");
    }

    // Attempts to read the visible question titles from a PUBLIC Google Form the
    // user pasted. Only follows the user-provided link, only reads (GET), never
    // logs in, never scrapes private forms, and never sends any data. It parses
    // the public FB_PUBLIC_LOAD_DATA_ array as JSON and extracts ONLY the clean
    // question titles — raw JSON/script text is never returned. Any failure
    // returns an empty list so the caller can show the friendly fallback.
    private static async Task<List<string>> TryFetchGoogleFormQuestionsAsync(string url, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; ResearchLab/1.0)");
            string html = await http.GetStringAsync(url, ct);
            return ExtractGoogleFormQuestions(html);
        }
        catch
        {
            return new List<string>();
        }
    }

    // Structured extraction of question titles from the public form payload.
    // FB_PUBLIC_LOAD_DATA_ is a JSON array; the item list lives at [1][1], and
    // each item's title is item[1]. We navigate that structure and return only
    // those title strings — never the raw array text.
    private static List<string> ExtractGoogleFormQuestions(string html)
    {
        var questions = new List<string>();
        try
        {
            int idx = html.IndexOf("FB_PUBLIC_LOAD_DATA_", StringComparison.Ordinal);
            if (idx < 0) return questions;
            int arrStart = html.IndexOf('[', idx);
            if (arrStart < 0) return questions;

            // Find the matching closing bracket for the top-level array.
            int depth = 0; bool inStr = false, esc = false; int arrEnd = -1;
            for (int i = arrStart; i < html.Length; i++)
            {
                char c = html[i];
                if (inStr) { if (esc) esc = false; else if (c == '\\') esc = true; else if (c == '"') inStr = false; continue; }
                if (c == '"') inStr = true;
                else if (c == '[') depth++;
                else if (c == ']') { depth--; if (depth == 0) { arrEnd = i; break; } }
            }
            if (arrEnd < 0) return questions;

            string json = html.Substring(arrStart, arrEnd - arrStart + 1);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() < 2) return questions;
            var section = root[1];
            if (section.ValueKind != JsonValueKind.Array || section.GetArrayLength() < 2) return questions;
            var items = section[1];
            if (items.ValueKind != JsonValueKind.Array) return questions;

            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() < 2) continue;
                var titleEl = item[1];
                if (titleEl.ValueKind != JsonValueKind.String) continue;
                string title = System.Net.WebUtility.HtmlDecode(titleEl.GetString() ?? "").Trim();
                // Only keep clean, human question titles.
                if (title.Length < 2 || title.Length > 200) continue;
                if (!title.Any(char.IsLetter)) continue;
                if (title.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;
                if (title.Contains('{') || title.Contains('[') || title.Contains("function")) continue;   // never leak raw script
                if (!questions.Contains(title)) questions.Add(title);
                if (questions.Count >= 80) break;
            }
        }
        catch
        {
            return new List<string>();   // any parse issue → friendly fallback, no raw text
        }
        return questions;
    }

    // Attaches each original question line (as typed / read from the form) as a
    // source-column alias on the variable it produced, matching on the AI's
    // questionLabel first, then a conservative fuzzy fallback. Aliases are the
    // exact text a Google Forms CSV export uses as its column header, so this is
    // what makes CSV-to-variable matching reliable. Never removes or renames a
    // variable; only enriches matching metadata.
    private static void AttachSourceAliases(IList<ResearchVariable> vars, string sourceQuestionsText)
    {
        if (vars is null || vars.Count == 0 || string.IsNullOrWhiteSpace(sourceQuestionsText)) return;

        var lines = sourceQuestionsText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length >= 2 && l.Any(char.IsLetter))
            .Distinct()
            .ToList();

        foreach (var line in lines)
        {
            string ln = NormalizeLabel(line);
            if (ln.Length == 0) continue;

            var v = vars.FirstOrDefault(x => NormalizeLabel(x.QuestionLabel) == ln)
                 ?? vars.FirstOrDefault(x => NormalizeLabel(x.VariableName) == ln)
                 ?? vars.FirstOrDefault(x => VariableMatchesColumnFuzzy(x, ln));
            if (v is null) continue;

            if (!v.SourceColumnAliases.Any(a => NormalizeLabel(a) == ln))
                v.SourceColumnAliases.Add(line);
        }
    }

    // =====================================================================
    // Research Lab (Phase 3) — Data Extraction Sheet: load, edit, persist
    // =====================================================================

    // Loads the open project's variables into the live grid and refreshes every
    // part of the tab. The grid holds the same ResearchVariable instances, so
    // in-cell edits mutate them directly; CommitGrid copies the order back.
    private void LoadExtractionSheet(ResearchProject p)
    {
        p.Variables ??= new List<ResearchVariable>();

        // One-time cleanup: drop fully-blank rows (no name AND no label) left over
        // from the earlier add bug. Named variables are never removed. Persist only
        // if something was actually cleaned so we don't rewrite files needlessly.
        int before = p.Variables.Count;
        p.Variables = p.Variables
            .Where(v => v is not null && (!string.IsNullOrWhiteSpace(v.VariableName) || !string.IsNullOrWhiteSpace(v.QuestionLabel)))
            .ToList();
        bool cleaned = p.Variables.Count != before;

        _extractionVariables.Clear();
        foreach (var v in p.Variables)
            _extractionVariables.Add(v);

        if (DxGrid.ItemsSource is null)
            DxGrid.ItemsSource = _extractionVariables;

        if (cleaned)
        {
            try { SaveResearch(); } catch { /* non-fatal */ }
        }

        // Undo/redo history is per project and per session.
        _dxUndo.Clear();
        _dxRedo.Clear();
        UpdateUndoRedoButtons();

        // Restore resolved/ignored conflict decisions so previously dismissed
        // warnings stay dismissed across sessions (per project).
        p.IgnoredConflictKeys ??= new List<string>();
        _ignoredConflictKeys.Clear();
        foreach (var k in p.IgnoredConflictKeys) _ignoredConflictKeys.Add(k);

        RefreshExtractionSummary();
        RefreshExtractionChips(p);
        UpdateExtractionStatus(p);
        RenderValidation(p.ExtractionValidationReport);
    }

    private void CommitGrid(ResearchProject p) => p.Variables = _extractionVariables.ToList();

    // Copies the grid into the project, stamps the status/time, saves, refreshes.
    private void PersistExtraction(ResearchProject p)
    {
        CommitGrid(p);
        p.ExtractionSheetUpdatedAt = DateTime.UtcNow;
        p.UpdatedAt = DateTime.UtcNow;
        UpdateExtractionStatus(p);
        SaveResearch();
        RefreshExtractionSummary();
        RefreshExtractionChips(p);
    }

    private void RefreshExtractionSummary()
    {
        var vars = _extractionVariables;
        int n = vars.Count;
        DxVarCountText.Text = n == 1 ? "1 variable" : $"{n} variables";
        DxEmptyState.Visibility = n == 0 ? Visibility.Visible : Visibility.Collapsed;

        // Anti-AI-waste lock: Generate by AI is disabled once a sheet exists —
        // Clear Sheet (confirmed) re-enables it. A failed generation leaves the
        // sheet empty, so Retry stays possible.
        DxGenerateAiBtn.IsEnabled = n == 0;
        DxGenerateAiBtn.ToolTip = n == 0
            ? "Let Research AI build the sheet from your research plan, proposal variable sections, and questions"
            : "AI extraction sheet already generated. Clear the sheet first to generate a new one.";

        int RoleCount(params string[] roles) => vars.Count(v => roles.Any(r => string.Equals(v.Role, r, StringComparison.OrdinalIgnoreCase)));

        DxSumTotal.Text = n.ToString();
        DxSumOutcome.Text = RoleCount("Outcome").ToString();
        DxSumExposure.Text = RoleCount("Exposure", "Predictor").ToString();
        DxSumConfounder.Text = RoleCount("Confounder").ToString();
        DxSumDemographic.Text = RoleCount("Demographic").ToString();
        DxSumIdentifier.Text = (RoleCount("Identifier") + vars.Count(v => string.Equals(v.VariableType, "ID", StringComparison.OrdinalIgnoreCase) && !string.Equals(v.Role, "Identifier", StringComparison.OrdinalIgnoreCase))).ToString();
        DxSumRequired.Text = vars.Count(v => v.IsRequired).ToString();
    }

    // Populates the Data Samples card from the project's CSV summary, Google Form
    // status, and any target sample size. (Kept the old name — many call sites.)
    private void RefreshExtractionChips(ResearchProject p)
    {
        DxSamplesFormBadge.Visibility = string.IsNullOrWhiteSpace(p.GoogleFormUrl) ? Visibility.Collapsed : Visibility.Visible;

        int? target = p.TargetSampleSize;
        if (target is null && TryExtractTargetSampleSize(p, out int detected)) { target = detected; p.TargetSampleSize = detected; }

        var csv = p.CsvSampleSummary;
        bool hasCsv = csv is { Columns.Count: > 0 };
        DxSamplesEmpty.Visibility = hasCsv ? Visibility.Collapsed : Visibility.Visible;
        DxSamplesInfo.Visibility = hasCsv ? Visibility.Visible : Visibility.Collapsed;
        DxViewCsvBtn.IsEnabled = hasCsv;
        DxClearSampleBtn.IsEnabled = hasCsv;

        if (hasCsv)
        {
            // Robust matching (name / question label / source-column alias / fuzzy),
            // with Google system columns excluded from the CSV-only count.
            var match = MatchCsvToSheet(_extractionVariables.ToList(), csv!);
            int matched = match.Matched.Count;
            int csvOnly = match.CsvOnly.Count;
            int sheetOnly = match.SheetOnly.Count;

            DxSampFile.Text = csv!.FileName;
            DxSampRows.Text = csv.TotalRows.ToString();
            DxSampCols.Text = csv.Columns.Count.ToString();
            DxSampMatched.Text = matched.ToString();
            DxSampCsvOnly.Text = csvOnly.ToString();
            DxSampSheetOnly.Text = sheetOnly.ToString();
            DxSampTarget.Text = target is > 0 ? target.ToString() : "—";

            if (target is > 0 && csv.TotalRows > 0 && csv.TotalRows < target)
            {
                // Incomplete sample — a hard blocker for Statistics. Show status,
                // the exact required message, and a re-upload action.
                int remaining = target.Value - csv.TotalRows;
                DxSampRemaining.Text = remaining.ToString();
                DxSampStatus.Text = "Incomplete sample";
                DxSampStatus.Foreground = (Brush)FindResource("DangerBrush");
                DxSampWarnText.Text = $"Your uploaded CSV contains {csv.TotalRows} samples, but the proposal target sample size is {target}. "
                    + "Please upload a complete CSV file with all required samples before running Statistics.";
                DxUploadCompleteBtn.Visibility = Visibility.Visible;
                DxSampWarn.Visibility = Visibility.Visible;
            }
            else if (target is > 0)
            {
                DxSampRemaining.Text = "0";
                DxSampStatus.Text = "Complete";
                DxSampStatus.Foreground = (Brush)FindResource("SuccessBrush");
                DxUploadCompleteBtn.Visibility = Visibility.Collapsed;
                DxSampWarn.Visibility = Visibility.Collapsed;
            }
            else
            {
                // No target found — advisory only, never a blocker.
                DxSampRemaining.Text = "—";
                DxSampStatus.Text = "No target set";
                DxSampStatus.Foreground = (Brush)FindResource("MutedBrush");
                DxSampWarnText.Text = "No target sample size was found. Confirm your sample size before running statistics.";
                DxUploadCompleteBtn.Visibility = Visibility.Collapsed;
                DxSampWarn.Visibility = Visibility.Visible;
            }
        }
    }

    private void UpdateExtractionStatus(ResearchProject p)
    {
        int n = _extractionVariables.Count;
        string status;
        if (n == 0) status = "Not started";
        else if (p.ExtractionValidationReport is { } r) status = r.IsReady ? "Ready" : "Needs review";
        else status = "Draft";

        p.ExtractionSheetStatus = status;
        DxStatusChipText.Text = status;

        var (fg, bg) = status switch
        {
            "Ready" => ("SuccessBrush", "SuccessSoftBrush"),
            "Needs review" => ("WarningBrush", "WarningSoftBrush"),
            "Draft" => ("PrimaryBrush", "PrimarySoftBrush"),
            _ => ("MutedBrush", "SoftCardBrush")
        };
        DxStatusChipText.Foreground = (Brush)FindResource(fg);
        DxStatusChip.Background = (Brush)FindResource(bg);
    }

    // ---- Add / edit / duplicate / delete / reorder ------------------------

    private static ResearchVariable? RowVar(object sender) => (sender as FrameworkElement)?.DataContext as ResearchVariable;

    // Logs the details (no keys) and shows a friendly toast. Combined with the
    // App-level DispatcherUnhandledException handler, no Data Extraction action
    // can ever hard-close the app.
    private void HandleDxError(string action, Exception ex)
    {
        try
        {
            System.IO.Directory.CreateDirectory(dataDir);
            System.IO.File.AppendAllText(System.IO.Path.Combine(dataDir, "crash.log"), $"[{DateTime.Now:u}] Data Extraction — {action}: {ex}\n\n");
        }
        catch { /* logging must never itself throw */ }
        HideLoading();
        ShowToast($"Couldn't {action}. Please try again.");
    }

    // Turns free text into a safe machine name (letters/numbers/underscores,
    // no leading digit). Empty => caller treats as invalid.
    private static string SanitizeVarName(string input)
    {
        string s = (input ?? "").Trim();
        s = Regex.Replace(s, @"\s+", "_");
        s = Regex.Replace(s, @"[^A-Za-z0-9_]", "");
        s = Regex.Replace(s, @"_+", "_").Trim('_');
        if (s.Length > 0 && char.IsDigit(s[0])) s = "_" + s;
        return s;
    }

    // Comparison key for matching sheet variables against CSV columns: both sides
    // are sanitized and lower-cased, so "Sleep Hours" (CSV) matches sleep_hours.
    private static string NormalizeKey(string s) => SanitizeVarName(s).ToLowerInvariant();

    // =====================================================================
    // Research Lab (Phase 3) — robust CSV column ↔ variable matching
    //
    // A Google Forms CSV export uses the FULL question text as each column
    // header (e.g. "What is your age?"), while the sheet stores a short machine
    // name ("age") plus the question wording in QuestionLabel / an alias. Naive
    // name-only matching therefore reported 0 matches. These helpers match on
    // name, question label, and stored source-column aliases, with a fuzzy
    // fallback, and treat Google's system columns (Timestamp, Username, …) as
    // metadata rather than real conflicts.
    // =====================================================================

    // Google Forms / Sheets export system columns — never a "missing variable".
    // System / dataset-tracking columns that are ignored by default (never
    // analyzed). Stored in NormalizeLabel form (punctuation dropped, lowercased):
    // e.g. "Sample_ID" → "sample id", "Response ID" → "response id".
    private static readonly HashSet<string> GoogleMetaColumns = new(StringComparer.Ordinal)
    {
        "timestamp", "username", "email address", "email", "score",
        "sample id", "sample type", "response id", "submission id", "form id"
    };

    private static bool IsGoogleMetadataColumn(string name)
        => GoogleMetaColumns.Contains(NormalizeLabel(name));

    // Full normalized comparison form for a human label/question/header:
    // lowercase, unicode quotes/dashes normalized, "(optional)" and required
    // asterisks removed, punctuation dropped, whitespace collapsed.
    private static string NormalizeLabel(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        string t = s.Trim().ToLowerInvariant();
        t = t.Replace('‘', '\'').Replace('’', '\'')
             .Replace('“', '"').Replace('”', '"')
             .Replace('–', '-').Replace('—', '-').Replace(' ', ' ');
        t = Regex.Replace(t, @"\(\s*optional\s*\)", " ");
        t = Regex.Replace(t, @"[^\p{L}\p{Nd}]+", " ");   // drop all punctuation/symbols
        t = Regex.Replace(t, @"\s+", " ").Trim();
        return t;
    }

    // Simplified alphanumeric-only form ("What is your age?" -> "whatisyourage").
    private static string AlphaNumKey(string s)
        => string.IsNullOrEmpty(s) ? "" : Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9]", "");

    // Common filler words stripped before fuzzy token-set comparison so
    // "What is your age?" and "Age" compare on their content token {age}.
    private static readonly HashSet<string> MatchStopwords = new(StringComparer.Ordinal)
    {
        "what","is","are","your","the","a","an","of","to","do","does","you","please",
        "how","many","much","in","on","for","and","or","was","were","did","have","has",
        "this","that","which","select","choose","enter","specify","optional","any","with","at","by"
    };

    private static List<string> ContentTokens(string norm)
        => norm.Split(' ', StringSplitOptions.RemoveEmptyEntries)
               .Where(t => !MatchStopwords.Contains(t)).ToList();

    // Conservative fuzzy match on content tokens. Single-content-token sides must
    // be equal ({age} == {age}); multi-token sides need strong overlap or subset.
    private static bool TokenSetSimilar(string aNorm, string bNorm)
    {
        var sa = ContentTokens(aNorm).ToHashSet();
        var sb = ContentTokens(bNorm).ToHashSet();
        if (sa.Count == 0 || sb.Count == 0) return false;
        int inter = sa.Count(sb.Contains);
        if (inter == 0) return false;
        if (sa.Count == 1 || sb.Count == 1) return sa.SetEquals(sb);
        int union = sa.Count + sb.Count - inter;
        if ((double)inter / union >= 0.6) return true;
        if (sa.IsSubsetOf(sb) || sb.IsSubsetOf(sa)) return inter >= 2;
        return false;
    }

    private static IEnumerable<string> VariableMatchTexts(ResearchVariable v)
    {
        yield return v.VariableName;
        yield return v.QuestionLabel;
        foreach (var a in v.SourceColumnAliases) yield return a;
    }

    // Exact match: machine name, full-normalized name/label/alias, or alphanumeric.
    private static bool VariableMatchesColumnExact(ResearchVariable v, string colNorm, string colKey, string colAlnum)
    {
        if (colKey.Length > 0 && NormalizeKey(v.VariableName) == colKey) return true;
        if (colNorm.Length > 0)
        {
            if (NormalizeLabel(v.VariableName) == colNorm) return true;
            if (NormalizeLabel(v.QuestionLabel) == colNorm) return true;
            foreach (var a in v.SourceColumnAliases)
                if (NormalizeLabel(a) == colNorm) return true;
        }
        if (colAlnum.Length >= 3)
            foreach (var t in VariableMatchTexts(v))
                if (AlphaNumKey(t) == colAlnum) return true;
        return false;
    }

    private static bool VariableMatchesColumnFuzzy(ResearchVariable v, string colNorm)
    {
        if (colNorm.Length == 0) return false;
        foreach (var t in VariableMatchTexts(v))
        {
            string tn = NormalizeLabel(t);
            if (tn.Length > 0 && TokenSetSimilar(tn, colNorm)) return true;
        }
        return false;
    }

    // Convenience: does this variable correspond to the given CSV column name?
    private static bool VariableMatchesColumn(ResearchVariable v, string columnName)
    {
        string colNorm = NormalizeLabel(columnName);
        return VariableMatchesColumnExact(v, colNorm, NormalizeKey(columnName), AlphaNumKey(columnName))
               || VariableMatchesColumnFuzzy(v, colNorm);
    }

    // Full match picture between the sheet variables and an uploaded CSV sample.
    private sealed class CsvSheetMatch
    {
        public readonly List<CsvColumnSummary> Matched = new();
        public readonly List<CsvColumnSummary> CsvOnly = new();     // non-metadata, no variable
        public readonly List<CsvColumnSummary> Metadata = new();    // Timestamp/Username/…
        public readonly List<ResearchVariable> SheetOnly = new();   // variable with no column
        public readonly Dictionary<CsvColumnSummary, ResearchVariable> ColumnMatch = new();
    }

    private static CsvSheetMatch MatchCsvToSheet(IReadOnlyList<ResearchVariable> vars, CsvSampleSummary csv)
    {
        var result = new CsvSheetMatch();
        var named = vars.Where(v => !string.IsNullOrWhiteSpace(v.VariableName)).ToList();
        var matchedVars = new HashSet<ResearchVariable>();

        foreach (var col in csv.Columns)
        {
            if (string.IsNullOrWhiteSpace(col.Name)) continue;

            string colNorm = NormalizeLabel(col.Name);
            string colKey = NormalizeKey(col.Name);
            string colAlnum = AlphaNumKey(col.Name);

            // Try to match a sheet variable FIRST so a variable the student
            // actually defined (even if it shares a metadata-like name) is never
            // silently dropped. Only unclaimed system/tracking columns are
            // treated as ignored metadata.
            var m = named.FirstOrDefault(v => VariableMatchesColumnExact(v, colNorm, colKey, colAlnum))
                    ?? named.FirstOrDefault(v => VariableMatchesColumnFuzzy(v, colNorm));
            if (m is not null)
            {
                result.Matched.Add(col);
                result.ColumnMatch[col] = m;
                matchedVars.Add(m);
            }
            else if (IsGoogleMetadataColumn(col.Name)) result.Metadata.Add(col);
            else result.CsvOnly.Add(col);
        }

        foreach (var v in named)
            if (!matchedVars.Contains(v))
                result.SheetOnly.Add(v);

        return result;
    }

    // Stable identity key for a conflict/warning so a "resolve" or "ignore"
    // decision persists and is not re-raised on the next Validate run (unless the
    // underlying variable/column actually changes). Deliberately excludes the
    // human-readable message (row numbers/counts vary) — only kind + identity.
    private static string MakeConflictKey(string kind, string identity)
        => kind + "|" + NormalizeKey(identity);

    // Add Variable opens the premium editor in "add" mode (no blank rows are
    // created in the grid until the user saves a named variable).
    private void AddVariable_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (CurrentResearchProject() is null) { ShowToast("Open a project first."); return; }
            OpenVariableEditor(null);
        }
        catch (Exception ex) { HandleDxError("add a variable", ex); }
    }

    private void EditVariable_Click(object sender, RoutedEventArgs e)
    {
        try { if (RowVar(sender) is { } v) OpenVariableEditor(v); }
        catch (Exception ex) { HandleDxError("open the variable editor", ex); }
    }

    // existing == null -> add mode; otherwise edit that variable.
    private void OpenVariableEditor(ResearchVariable? existing)
    {
        _editingVariable = existing;

        VeType.ItemsSource = ResearchVariableOptions.VariableTypes;
        VeLevel.ItemsSource = ResearchVariableOptions.MeasurementLevels;
        VeRole.ItemsSource = ResearchVariableOptions.Roles;
        VeSource.ItemsSource = ResearchVariableOptions.Sources;
        VeValidationBorder.Visibility = Visibility.Collapsed;

        VeHeaderText.Text = existing is null ? "Add variable" : "Edit variable";
        VeName.Text = existing?.VariableName ?? "";
        VeLabel.Text = existing?.QuestionLabel ?? "";
        VeType.SelectedItem = existing != null && ResearchVariableOptions.VariableTypes.Contains(existing.VariableType) ? existing.VariableType : "Unknown";
        VeLevel.SelectedItem = existing != null && ResearchVariableOptions.MeasurementLevels.Contains(existing.MeasurementLevel) ? existing.MeasurementLevel : "NotApplicable";
        VeRole.SelectedItem = existing != null && ResearchVariableOptions.Roles.Contains(existing.Role) ? existing.Role : "Unknown";
        VeSource.SelectedItem = existing != null && ResearchVariableOptions.Sources.Contains(existing.Source) ? existing.Source : "Manual";
        VeCoding.Text = existing?.Coding ?? "";
        VeValueLabels.Text = existing?.ValueLabels ?? "";
        VeMissing.Text = existing?.MissingValueRule ?? "";
        VeNotes.Text = existing?.Notes ?? "";
        VeRequired.IsChecked = existing?.IsRequired ?? false;

        VariableEditOverlay.Visibility = Visibility.Visible;
        FadeIn(VariableEditOverlay, 150);
        VeName.Focus();
    }

    private void DuplicateVariable_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var p = CurrentResearchProject();
            if (p is null || RowVar(sender) is not { } v) return;
            PushDxUndo();
            int idx = _extractionVariables.IndexOf(v);
            var copy = v.Clone();
            copy.VariableName = MakeUniqueName(string.IsNullOrWhiteSpace(copy.VariableName) ? "variable" : copy.VariableName + "_copy");
            _extractionVariables.Insert(idx < 0 ? _extractionVariables.Count : idx + 1, copy);
            p.ExtractionValidationReport = null;
            RenderValidation(null);
            PersistExtraction(p);
            ShowToast("Variable duplicated.");
        }
        catch (Exception ex) { HandleDxError("duplicate the variable", ex); }
    }

    // Ensures a name is not already used (appends _2, _3, ... if needed).
    private string MakeUniqueName(string baseName)
    {
        string name = SanitizeVarName(baseName);
        if (name.Length == 0) name = "variable";
        var used = _extractionVariables.Select(v => v.VariableName.Trim().ToLowerInvariant()).ToHashSet();
        if (!used.Contains(name.ToLowerInvariant())) return name;
        for (int i = 2; i < 1000; i++)
        {
            string candidate = $"{name}_{i}";
            if (!used.Contains(candidate.ToLowerInvariant())) return candidate;
        }
        return name;
    }

    private void DeleteVariable_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var p = CurrentResearchProject();
            if (p is null || RowVar(sender) is not { } v) return;
            string label = string.IsNullOrWhiteSpace(v.VariableName) ? "this variable" : $"“{v.VariableName.Trim()}”";
            ShowRlConfirm("Delete variable?",
                $"Remove {label} from the extraction sheet? This cannot be undone.",
                "Delete",
                onConfirm: () =>
                {
                    try
                    {
                        PushDxUndo();
                        _extractionVariables.Remove(v);
                        p.ExtractionValidationReport = null;
                        RenderValidation(null);
                        PersistExtraction(p);
                        ShowToast("Variable removed.");
                    }
                    catch (Exception ex) { HandleDxError("delete the variable", ex); }
                });
        }
        catch (Exception ex) { HandleDxError("delete the variable", ex); }
    }

    private void MoveVariableUp_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var p = CurrentResearchProject();
            if (p is null || RowVar(sender) is not { } v) return;
            int idx = _extractionVariables.IndexOf(v);
            if (idx > 0) { PushDxUndo(); _extractionVariables.Move(idx, idx - 1); PersistExtraction(p); }
        }
        catch (Exception ex) { HandleDxError("reorder the variable", ex); }
    }

    private void MoveVariableDown_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var p = CurrentResearchProject();
            if (p is null || RowVar(sender) is not { } v) return;
            int idx = _extractionVariables.IndexOf(v);
            if (idx >= 0 && idx < _extractionVariables.Count - 1) { PushDxUndo(); _extractionVariables.Move(idx, idx + 1); PersistExtraction(p); }
        }
        catch (Exception ex) { HandleDxError("reorder the variable", ex); }
    }

    private void CancelEditVariable_Click(object sender, RoutedEventArgs e)
    {
        VariableEditOverlay.Visibility = Visibility.Collapsed;
        _editingVariable = null;
    }

    private void ShowVeValidation(string msg)
    {
        VeValidation.Text = msg;
        VeValidationBorder.Visibility = Visibility.Visible;
    }

    private void SaveEditVariable_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var p = CurrentResearchProject();
            if (p is null) { VariableEditOverlay.Visibility = Visibility.Collapsed; return; }

            string rawName = (VeName.Text ?? "").Trim();
            if (rawName.Length == 0) { ShowVeValidation("Variable name is required."); return; }

            string name = SanitizeVarName(rawName);
            if (name.Length == 0) { ShowVeValidation("Use letters, numbers, or underscores (e.g. sleep_hours)."); return; }
            bool changedName = !string.Equals(name, rawName, StringComparison.Ordinal);

            bool duplicate = _extractionVariables.Any(x => !ReferenceEquals(x, _editingVariable)
                && string.Equals(x.VariableName.Trim(), name, StringComparison.OrdinalIgnoreCase));
            if (duplicate)
            {
                if (changedName) VeName.Text = name;
                ShowVeValidation($"A variable named “{name}” already exists. Choose a different name.");
                return;
            }

            PushDxUndo();
            bool isNew = _editingVariable is null;
            var v = _editingVariable ?? new ResearchVariable();

            v.VariableName = name;
            v.QuestionLabel = (VeLabel.Text ?? "").Trim();
            v.VariableType = VeType.SelectedItem as string ?? "Unknown";
            v.MeasurementLevel = VeLevel.SelectedItem as string ?? "NotApplicable";
            v.Role = VeRole.SelectedItem as string ?? "Unknown";
            v.Source = VeSource.SelectedItem as string ?? "Manual";
            v.Coding = (VeCoding.Text ?? "").Trim();
            v.ValueLabels = (VeValueLabels.Text ?? "").Trim();
            v.MissingValueRule = (VeMissing.Text ?? "").Trim();
            v.Notes = (VeNotes.Text ?? "").Trim();
            v.IsRequired = VeRequired.IsChecked == true;

            if (isNew) _extractionVariables.Add(v);

            VariableEditOverlay.Visibility = Visibility.Collapsed;
            _editingVariable = null;

            DxGrid.Items.Refresh();
            p.ExtractionValidationReport = null;   // sheet changed
            RenderValidation(null);
            PersistExtraction(p);

            ShowToast(isNew
                ? (changedName ? $"Variable added as “{name}”." : "Variable added.")
                : (changedName ? $"Variable saved as “{name}”." : "Variable updated."));
        }
        catch (Exception ex) { HandleDxError("save the variable", ex); }
    }

    private void SaveExtractionSheet_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var p = CurrentResearchProject();
            if (p is null) { ShowToast("Open a project first."); return; }
            // Commit any in-progress cell edit before saving.
            TryCommitGridEdit();
            PersistExtraction(p);
            ShowToast("Extraction sheet saved.");
        }
        catch (Exception ex) { HandleDxError("save the sheet", ex); }
    }

    // Commits an in-flight cell/row edit without ever throwing (a mid-edit grid
    // can refuse CommitEdit; that must not crash the action).
    private void TryCommitGridEdit()
    {
        try { DxGrid.CommitEdit(DataGridEditingUnit.Cell, true); DxGrid.CommitEdit(DataGridEditingUnit.Row, true); }
        catch { /* ignore — best effort */ }
    }

    // ---- Generate by AI + review ------------------------------------------

    private void GenerateExtractionSheet_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var p = CurrentResearchProject();
            if (p is null) { ShowToast("Open a project first."); return; }
            TryCommitGridEdit();
            CommitGrid(p);
            if (!_researchAi.IsConfigured) { ShowToast("Research AI is not set up yet. Add an endpoint or enable a development provider in AI Settings, then try again."); return; }

            // Anti-spam lock: one AI-generated sheet per project. The button is
            // disabled while a sheet exists (see RefreshExtractionSummary); this
            // guard is the belt-and-braces fallback. The student must Clear Sheet
            // (with its own confirmation) before generating again. A FAILED
            // generation leaves the sheet empty, so Retry stays available.
            if (_extractionVariables.Count > 0)
            {
                ShowToast("AI extraction sheet already generated. Clear the sheet first to generate a new one.");
                return;
            }

            StartAiWithDelay("Generate extraction sheet", () => _ = RunGenerateExtractionSheet(p));
        }
        catch (Exception ex) { HandleDxError("start the AI generation", ex); }
    }

    private async Task RunGenerateExtractionSheet(ResearchProject p)
    {
        var ct = BeginAiWork("Building Extraction Sheet",
            "Preparing research plan", "Building variable request", "Sending request",
            "Waiting for variables", "Validating variables", "Preparing review sheet");
        try
        {
            AiWorkAdvance();   // plan summary prepared (inside the compact prompt)
            AiWorkAdvance();   // variable request built
            AiWorkAdvance();   // request sent → waiting
            var result = await _researchAi.GenerateExtractionSheetAsync(p, "", ct);
            AiWorkAdvance();   // response received + parsed → validating
            // The service already rejects zero-variable output, but never trust a
            // single layer for "no silent failure": re-check before showing review.
            if (result.Variables.Count == 0)
                throw new ResearchAiException("Research AI returned an incomplete extraction sheet. Please retry or continue manually.", category: "parse_failure");
            AiWorkAdvance();   // validated → preparing review
            _pendingSheet = result;
            ShowExtractionReview(result, "Review the suggested variables before applying. Nothing changes until you choose an action below.", "Apply Suggested Sheet");
            await CompleteAiWorkAsync();
        }
        catch (OperationCanceledException)
        {
            ShowToast("Cancelled. Your existing content was kept.");
        }
        catch (ResearchAiNotConfiguredException) { ShowToast("Research AI is not configured yet."); }
        catch (ResearchAiException ex)
        {
            // NEVER silent: every failure (provider 500, overload, rate limit,
            // parse failure, incomplete output, timeout) lands in the failure
            // panel with Retry / Continue Manually / Settings / Details / Close.
            // The existing sheet/content is untouched; retry rebuilds the same
            // compact input.
            _lastAiDiagnostics = ex.Diagnostics;
            ShowAiFailure(ex.Message, () => _ = RunGenerateExtractionSheet(p));
        }
        catch (Exception)
        {
            ShowAiFailure("Research AI could not build the sheet. Your content was kept. Please try again.",
                () => _ = RunGenerateExtractionSheet(p));
        }
        finally { EndAiWork(); }
    }

    private void ShowExtractionReview(ExtractionSheetResult result, string subtitle, string applyLabel = "Apply to Sheet")
    {
        DxReviewSubtitle.Text = subtitle;
        // Edit-before-apply: bind the editable review grid to a working copy so the
        // student can change any field or remove rows before applying. Apply uses
        // this edited collection, never the raw AI output.
        _reviewVariables.Clear();
        foreach (var v in result.Variables) _reviewVariables.Add(v);
        DxReviewGrid.ItemsSource = _reviewVariables;
        DxReviewValidation.Visibility = Visibility.Collapsed;
        DxReviewCountText.Text = _reviewVariables.Count == 1 ? "1 suggested variable" : $"{_reviewVariables.Count} suggested variables";

        // One clear action: the review modal only offers Apply + Cancel (no
        // Regenerate — regenerating from here wastes API calls; the workflow is
        // Clear Sheet then Generate). Applying to an empty sheet sets it; applying
        // to a non-empty sheet (e.g. Paste Questions) adds the missing rows
        // non-destructively.
        DxReviewApplyText.Text = applyLabel;

        if (!string.IsNullOrWhiteSpace(result.ConfidenceSummary))
        {
            DxReviewConfidence.Text = result.ConfidenceSummary;
            DxReviewConfidenceCard.Visibility = Visibility.Visible;
        }
        else DxReviewConfidenceCard.Visibility = Visibility.Collapsed;

        if (result.MissingExpectedVariables.Count > 0)
        {
            DxReviewMissingList.ItemsSource = result.MissingExpectedVariables;
            DxReviewMissingSection.Visibility = Visibility.Visible;
        }
        else DxReviewMissingSection.Visibility = Visibility.Collapsed;

        if (result.ExtraOrUnexplainedColumns.Count > 0)
        {
            DxReviewExtraList.ItemsSource = result.ExtraOrUnexplainedColumns;
            DxReviewExtraSection.Visibility = Visibility.Visible;
        }
        else DxReviewExtraSection.Visibility = Visibility.Collapsed;

        ExtractionReviewOverlay.Visibility = Visibility.Visible;
        FadeIn(ExtractionReviewOverlay, 150);
    }

    private void ExtractionReviewCancel_Click(object sender, RoutedEventArgs e)
    {
        ExtractionReviewOverlay.Visibility = Visibility.Collapsed;
        _pendingSheet = null;
        _reviewVariables.Clear();
    }

    // Escape closes the topmost visible Data Extraction overlay (dismiss only —
    // never applies changes). Highest ZIndex first.
    private void DataExtraction_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Escape) return;

        // Generic confirm sits on top (ZIndex 60): dismiss without running either action.
        if (RlConfirmOverlay.Visibility == Visibility.Visible)
        {
            RlConfirmOverlay.Visibility = Visibility.Collapsed;
            _rlConfirmAction = null;
            _rlConfirmCancelAction = null;
            e.Handled = true;
            return;
        }

        // Cleanup summary modal (ZIndex 59): Escape = Go Back to the review screen.
        if (AiCleanupSummaryOverlay.Visibility == Visibility.Visible)
        {
            AiCleanupSummaryOverlay.Visibility = Visibility.Collapsed;
            e.Handled = true;
            return;
        }

        // The conflict window has staged decisions — Escape must fully discard
        // them (same as its Cancel button), not just hide the overlay.
        if (ConflictOverlay.Visibility == Visibility.Visible)
        {
            ConflictCancel_Click(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        var overlays = new (UIElement O, Action? OnClose)[]
        {
            (AiDelayOverlay, CancelAiDelayCore),   // Escape = cancel, no AI request
            (VariableEditOverlay, () => _editingVariable = null),
            (ExtractionReviewOverlay, () => { _pendingSheet = null; _reviewVariables.Clear(); }),
            (ExtractionFixReviewOverlay, () => _fixProposals.Clear()),
            (CsvSummaryOverlay, null),
            (PasteQuestionsOverlay, null),
            (GoogleFormOverlay, null),
        };
        foreach (var (o, onClose) in overlays)
        {
            if (o.Visibility == Visibility.Visible)
            {
                o.Visibility = Visibility.Collapsed;
                onClose?.Invoke();
                e.Handled = true;
                return;
            }
        }
    }

    private void ReviewDeleteRow_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (RowVar(sender) is { } v)
            {
                _reviewVariables.Remove(v);
                DxReviewCountText.Text = _reviewVariables.Count == 1 ? "1 suggested variable" : $"{_reviewVariables.Count} suggested variables";
            }
        }
        catch (Exception ex) { HandleDxError("remove the suggestion", ex); }
    }

    private void ExtractionReviewApply_Click(object sender, RoutedEventArgs e)
    {
        // Single apply action: set the sheet when empty; otherwise add the new
        // rows without discarding the student's existing ones. Regeneration and
        // full replacement are intentionally not offered from this modal.
        if (_extractionVariables.Count == 0) ApplySuggested(ApplyMode.Replace);
        else ApplySuggested(ApplyMode.AddMissing);
    }

    private enum ApplyMode { Replace, AddMissing }

    // Validates the EDITED suggestions before they are applied: every row needs a
    // name, and names must be unique. Type/role gaps are advisory (not blocking).
    private bool ValidateReviewBeforeApply()
    {
        try { DxReviewGrid.CommitEdit(DataGridEditingUnit.Cell, true); DxReviewGrid.CommitEdit(DataGridEditingUnit.Row, true); } catch { }

        var blocking = new List<string>();
        var seen = new HashSet<string>();
        int rowNo = 0;
        foreach (var v in _reviewVariables)
        {
            rowNo++;
            string name = SanitizeVarName(v.VariableName);
            if (name.Length == 0) { blocking.Add($"Row {rowNo} has no variable name."); continue; }
            if (!seen.Add(name.ToLowerInvariant())) blocking.Add($"Duplicate variable name '{name}'.");
        }
        if (_reviewVariables.Count == 0) blocking.Add("There are no variables to apply.");

        if (blocking.Count > 0)
        {
            DxReviewValidationText.Text = "Fix these before applying:  " + string.Join("   •  ", blocking.Take(6));
            DxReviewValidation.Visibility = Visibility.Visible;
            return false;
        }
        DxReviewValidation.Visibility = Visibility.Collapsed;
        return true;
    }

    private void ApplySuggested(ApplyMode mode, bool skipValidation = false)
    {
        var p = CurrentResearchProject();
        if (p is null) { ExtractionReviewOverlay.Visibility = Visibility.Collapsed; return; }
        if (!skipValidation && !ValidateReviewBeforeApply()) return;

        PushDxUndo();   // applying an AI sheet is undoable

        // Normalize names on the edited suggestions before applying.
        foreach (var v in _reviewVariables)
        {
            string n = SanitizeVarName(v.VariableName);
            if (n.Length > 0) v.VariableName = n;
        }

        int added = 0;
        if (mode == ApplyMode.Replace)
        {
            _extractionVariables.Clear();
            foreach (var v in _reviewVariables) { _extractionVariables.Add(v); added++; }
            // A brand-new sheet: old resolved/ignored decisions no longer apply.
            _ignoredConflictKeys.Clear();
            p.IgnoredConflictKeys = new List<string>();
        }
        else
        {
            var existing = _extractionVariables.Select(v => v.VariableName.Trim().ToLowerInvariant()).Where(s => s.Length > 0).ToHashSet();
            foreach (var v in _reviewVariables)
            {
                string key = v.VariableName.Trim().ToLowerInvariant();
                if (key.Length > 0 && existing.Contains(key)) continue;
                _extractionVariables.Add(v);
                if (key.Length > 0) existing.Add(key);
                added++;
            }
        }

        // If the AI reported a target sample size in free text, keep a numeric copy.
        if (p.TargetSampleSize is null && _pendingSheet is not null && !string.IsNullOrWhiteSpace(_pendingSheet.TargetSampleSizeText))
        {
            var m = Regex.Match(_pendingSheet.TargetSampleSizeText, @"(\d{2,7})");
            if (m.Success && int.TryParse(m.Value, out int t) && t is >= 10 and <= 1_000_000) p.TargetSampleSize = t;
        }

        ExtractionReviewOverlay.Visibility = Visibility.Collapsed;
        _pendingSheet = null;
        _reviewVariables.Clear();
        // Applying new variables invalidates the last validation run.
        p.ExtractionValidationReport = null;
        RenderValidation(null);
        PersistExtraction(p);
        ShowToast(mode == ApplyMode.Replace ? $"Applied {added} variables to the sheet." : (added == 0 ? "No new variables to add." : $"Added {added} new variable{(added == 1 ? "" : "s")}."));
    }

    // ---- CSV import -------------------------------------------------------

    private void ImportCsv_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var p = CurrentResearchProject();
            if (p is null) { ShowToast("Open a project first."); return; }

            var ofd = new OpenFileDialog
            {
                Title = "Choose a CSV sample or data export",
                Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
                CheckFileExists = true
            };
            if (ofd.ShowDialog() != true) return;

            if (!TryLoadCsvSample(ofd.FileName, out var summary, out string error))
            {
                ShowToast(error);
                return;
            }

            TryCommitGridEdit();
            CommitGrid(p);
            p.CsvSampleSummary = summary;
            p.ExtractionValidationReport = null;   // sheet context changed
            RenderValidation(null);

            // Phase 4A: keep a per-project copy of the full CSV so the LOCAL
            // statistics engine has the raw values. The copy stays in the app
            // data folder on this device and is never sent to any AI/network.
            if (!TryStoreDatasetCopy(ofd.FileName, p))
                ShowToast("The dataset copy for statistics could not be stored. Statistics will ask you to upload the CSV again.");

            SaveResearch();
            RefreshExtractionChips(p);

            // With a sheet already built, column mapping is LOCAL (name/label/alias
            // matching + Validate/Resolve Conflicts) — no AI call, and no bypassing
            // the one-AI-sheet-per-project lock. Only an EMPTY sheet offers the
            // cancelable AI generation from the fresh CSV headers.
            if (_extractionVariables.Count > 0)
                ShowToast($"CSV summary saved ({summary.Columns.Count} columns, {summary.TotalRows} rows). Run Validate Sheet to match columns against your variables.");
            else if (_researchAi.IsConfigured)
                StartAiWithDelay("Map CSV columns to variables", () => _ = RunGenerateExtractionSheet(p));   // review before applying
            else
                ShowToast($"CSV summary saved ({summary.Columns.Count} columns, {summary.TotalRows} rows). Set up Research AI to map columns, or add variables manually.");
        }
        catch (Exception ex) { HandleDxError("import the CSV", ex); }
    }

    private void ClearSample_Click(object sender, RoutedEventArgs e)
    {
        var p = CurrentResearchProject();
        if (p is null || p.CsvSampleSummary is null) { ShowToast("No CSV sample to clear."); return; }
        ShowRlConfirm("Clear the CSV sample?",
            "This removes the uploaded CSV summary. Your variable sheet is not changed.",
            "Clear sample",
            onConfirm: () =>
            {
                try
                {
                    p.CsvSampleSummary = null;
                    p.ExtractionValidationReport = null;
                    DeleteDatasetCopy(p.Id);   // statistics dataset copy goes with it
                    RenderValidation(null);
                    SaveResearch();
                    RefreshExtractionChips(p);
                    ShowToast("CSV sample cleared.");
                }
                catch (Exception ex) { HandleDxError("clear the sample", ex); }
            });
    }

    private void ViewCsvSummary_Click(object sender, RoutedEventArgs e)
    {
        var p = CurrentResearchProject();
        if (p?.CsvSampleSummary is not { } csv) { ShowToast("Import a CSV sample first."); return; }
        CsvSummaryTitle.Text = $"{csv.FileName} — {csv.CountSummary}";
        CsvSummarySub.Text = $"First {csv.SampledRows} rows sampled for column profiling. Only this summary (never the raw data) is used.";
        CsvSummaryList.ItemsSource = csv.Columns;
        CsvSummaryOverlay.Visibility = Visibility.Visible;
        FadeIn(CsvSummaryOverlay, 150);
    }

    private void CloseCsvSummary_Click(object sender, RoutedEventArgs e)
        => CsvSummaryOverlay.Visibility = Visibility.Collapsed;

    // ---- Clear the whole sheet --------------------------------------------

    private void ClearSheet_Click(object sender, RoutedEventArgs e)
    {
        var p = CurrentResearchProject();
        if (p is null) { ShowToast("Open a project first."); return; }
        if (_extractionVariables.Count == 0) { ShowToast("The sheet is already empty."); return; }
        ShowRlConfirm("Clear the extraction sheet?",
            "This will remove all variables from the extraction sheet. This cannot be undone. Your CSV sample is kept.",
            "Clear Sheet",
            onConfirm: () =>
            {
                try
                {
                    PushDxUndo();
                    _extractionVariables.Clear();
                    p.ExtractionValidationReport = null;
                    // A cleared sheet starts fresh — drop resolved/ignored decisions.
                    _ignoredConflictKeys.Clear();
                    p.IgnoredConflictKeys = new List<string>();
                    RenderValidation(null);
                    PersistExtraction(p);
                    ShowToast("Extraction sheet cleared. Use Undo to restore it.");
                }
                catch (Exception ex) { HandleDxError("clear the sheet", ex); }
            });
    }

    // ---- Paste questions --------------------------------------------------

    private void OpenPasteQuestions_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentResearchProject() is null) { ShowToast("Open a project first."); return; }
        DxQuestionsBox.Text = "";
        DxQuestionsValidation.Visibility = Visibility.Collapsed;
        PasteQuestionsOverlay.Visibility = Visibility.Visible;
        FadeIn(PasteQuestionsOverlay, 150);
        DxQuestionsBox.Focus();
    }

    private void CancelPasteQuestions_Click(object sender, RoutedEventArgs e)
        => PasteQuestionsOverlay.Visibility = Visibility.Collapsed;

    private void ExtractVariablesFromQuestions_Click(object sender, RoutedEventArgs e)
    {
        var p = CurrentResearchProject();
        if (p is null) { PasteQuestionsOverlay.Visibility = Visibility.Collapsed; return; }

        string text = (DxQuestionsBox.Text ?? "").Trim();
        DxQuestionsValidation.Foreground = (Brush)FindResource("DangerBrush");   // messages below are errors
        if (text.Length < 4)
        {
            DxQuestionsValidation.Text = "Paste at least one question (one per line).";
            DxQuestionsValidation.Visibility = Visibility.Visible;
            return;
        }
        if (!_researchAi.IsConfigured)
        {
            DxQuestionsValidation.Text = "Research AI is not configured yet. Add an endpoint or enable a development provider in Settings.";
            DxQuestionsValidation.Visibility = Visibility.Visible;
            return;
        }

        DxQuestionsValidation.Visibility = Visibility.Collapsed;
        StartAiWithDelay("Extract variables from questions", () => _ = RunExtractQuestions(p, text));
    }

    private async Task RunExtractQuestions(ResearchProject p, string text)
    {
        DxExtractQuestionsBtn.IsEnabled = false;
        var ct = BeginAiWork("Extracting Variables from Questions",
            "Preparing questions", "Building variable request", "Sending request",
            "Waiting for variables", "Validating variables", "Preparing review sheet");
        try
        {
            AiWorkAdvance();   // clean questions prepared (local parse, done above)
            AiWorkAdvance();   // variable request built
            AiWorkAdvance();   // request sent → waiting (batched for >25 lines)
            var result = await _researchAi.ExtractVariablesFromQuestionsAsync(p, text, ct);
            AiWorkAdvance();   // response received + parsed → validating
            // Record the EXACT original question wording as a source-column alias on
            // each resulting variable. A Google Forms CSV export uses that exact
            // wording as its column header, so this makes later CSV matching reliable
            // even when the AI shortened/reworded the questionLabel or variableName.
            AttachSourceAliases(result.Variables, text);
            AiWorkAdvance();   // validated → preparing review
            _pendingSheet = result;
            PasteQuestionsOverlay.Visibility = Visibility.Collapsed;
            ShowExtractionReview(result, "Review the variables built from your questions before applying.", "Apply to Sheet");
            await CompleteAiWorkAsync();
        }
        catch (OperationCanceledException)
        {
            ShowToast("Cancelled. Your existing content was kept.");
        }
        catch (ResearchAiException ex)
        {
            // Keep the questions visible in the still-open Paste Questions overlay
            // (never clear them) and offer the standard retry/settings/details panel
            // for EVERY failure category. Retry reuses the exact same clean question
            // text — never re-derives or resends anything larger.
            _lastAiDiagnostics = ex.Diagnostics;
            ShowAiFailure(ex.Message, () => _ = RunExtractQuestions(p, text));
        }
        catch (Exception)
        {
            DxQuestionsValidation.Text = "Research AI could not read those questions. Please try again.";
            DxQuestionsValidation.Visibility = Visibility.Visible;
        }
        finally
        {
            EndAiWork();
            DxExtractQuestionsBtn.IsEnabled = true;
        }
    }

    // ---- Google Form link -------------------------------------------------

    private void OpenGoogleForm_Click(object sender, RoutedEventArgs e)
    {
        var p = CurrentResearchProject();
        if (p is null) { ShowToast("Open a project first."); return; }
        DxFormUrlBox.Text = p.GoogleFormUrl ?? "";
        DxFormMessage.Visibility = Visibility.Collapsed;
        GoogleFormOverlay.Visibility = Visibility.Visible;
        FadeIn(GoogleFormOverlay, 150);
        DxFormUrlBox.Focus();
    }

    private void CancelGoogleForm_Click(object sender, RoutedEventArgs e)
        => GoogleFormOverlay.Visibility = Visibility.Collapsed;

    private void ShowFormMessage(string message, bool error)
    {
        DxFormMessage.Text = message;
        DxFormMessage.Foreground = (Brush)FindResource(error ? "DangerBrush" : "SuccessBrush");
        DxFormMessage.Visibility = Visibility.Visible;
    }

    private void SaveFormLink_Click(object sender, RoutedEventArgs e)
    {
        var p = CurrentResearchProject();
        if (p is null) { GoogleFormOverlay.Visibility = Visibility.Collapsed; return; }

        string url = (DxFormUrlBox.Text ?? "").Trim();
        if (url.Length == 0)
        {
            p.GoogleFormUrl = "";
            SaveResearch();
            RefreshExtractionChips(p);
            GoogleFormOverlay.Visibility = Visibility.Collapsed;
            ShowToast("Google Form link cleared.");
            return;
        }
        if (!IsGoogleFormUrl(url))
        {
            ShowFormMessage("That does not look like a Google Form link (docs.google.com/forms or forms.gle).", true);
            return;
        }
        p.GoogleFormUrl = url;
        SaveResearch();
        RefreshExtractionChips(p);
        GoogleFormOverlay.Visibility = Visibility.Collapsed;
        ShowToast("Google Form link saved.");
    }

    private async void TryReadForm_Click(object sender, RoutedEventArgs e)
    {
        var p = CurrentResearchProject();
        if (p is null) return;
        string url = (DxFormUrlBox.Text ?? "").Trim();
        if (!IsGoogleFormUrl(url))
        {
            ShowFormMessage("Enter a valid Google Form link first (docs.google.com/forms or forms.gle).", true);
            return;
        }

        DxReadFormBtn.IsEnabled = false;
        ShowLoading("Trying to read your form...");
        List<string> questions;
        try { questions = await TryFetchGoogleFormQuestionsAsync(url, CancellationToken.None); }
        catch { questions = new List<string>(); }
        finally { HideLoading(); DxReadFormBtn.IsEnabled = true; }

        // Save the link regardless of whether reading succeeded.
        p.GoogleFormUrl = url;
        SaveResearch();
        RefreshExtractionChips(p);

        if (questions.Count == 0)
        {
            ShowFormMessage("Could not read this Google Form link. Make it public, paste questions manually, or upload a CSV/Google Sheets export.", true);
            return;
        }

        // Hand the detected questions to the paste-questions flow for review.
        GoogleFormOverlay.Visibility = Visibility.Collapsed;
        DxQuestionsBox.Text = string.Join(Environment.NewLine, questions);
        DxQuestionsValidation.Text = $"Loaded {questions.Count} possible question(s) from the form. Review/trim them, then extract variables.";
        DxQuestionsValidation.Foreground = (Brush)FindResource("MutedBrush");
        DxQuestionsValidation.Visibility = Visibility.Visible;
        PasteQuestionsOverlay.Visibility = Visibility.Visible;
        FadeIn(PasteQuestionsOverlay, 150);
    }

    // ---- Validate ---------------------------------------------------------

    private void ValidateSheet_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var p = CurrentResearchProject();
            if (p is null) { ShowToast("Open a project first."); return; }
            TryCommitGridEdit();
            CommitGrid(p);

            if (TryExtractTargetSampleSize(p, out int target)) p.TargetSampleSize = target;

            var report = ValidateExtractionSheet(p);
            p.ExtractionValidationReport = report;
            UpdateExtractionStatus(p);
            p.UpdatedAt = DateTime.UtcNow;
            SaveResearch();
            RenderValidation(report);
            RefreshExtractionChips(p);

            ShowToast(report.IsReady
                ? (report.Warnings.Count == 0 ? "Validation passed — your sheet is ready." : "Validation passed with some warnings to review.")
                : $"Validation found {report.Errors.Count} issue{(report.Errors.Count == 1 ? "" : "s")} to fix.");
        }
        catch (Exception ex) { HandleDxError("validate the sheet", ex); }
    }

    private void RenderValidation(ExtractionValidationReport? report)
    {
        if (report is null)
        {
            DxValEmpty.Visibility = Visibility.Visible;
            DxErrorsSection.Visibility = Visibility.Collapsed;
            DxWarningsSection.Visibility = Visibility.Collapsed;
            DxSuggestionsSection.Visibility = Visibility.Collapsed;
            DxReadyBanner.Visibility = Visibility.Collapsed;
            DxResolveConflictsBtn.Visibility = Visibility.Collapsed;
            DxValStatusText.Text = "Not checked";
            DxValStatusText.Foreground = (Brush)FindResource("MutedBrush");
            DxValStatusPill.Background = (Brush)FindResource("SoftCardBrush");
            return;
        }

        // Conflicts entry point: anything flagged as an error or warning can be
        // worked through in the Resolve Conflicts window.
        DxResolveConflictsBtn.Visibility = (report.Errors.Count + report.Warnings.Count) > 0
            ? Visibility.Visible : Visibility.Collapsed;

        DxValEmpty.Visibility = Visibility.Collapsed;

        DxErrorsList.ItemsSource = report.Errors;
        DxErrorsHeader.Text = $"Errors ({report.Errors.Count})";
        DxErrorsSection.Visibility = report.Errors.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        DxWarningsList.ItemsSource = report.Warnings;
        DxWarningsHeader.Text = $"Warnings ({report.Warnings.Count})";
        DxWarningsSection.Visibility = report.Warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        DxSuggestionsList.ItemsSource = report.Suggestions;
        DxSuggestionsHeader.Text = $"Suggestions ({report.Suggestions.Count})";
        DxSuggestionsSection.Visibility = report.Suggestions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // Three honest states — never "ready + warnings" dressed up as all-clear:
        //   • Blocked            — errors remain (red)
        //   • Ready, with warnings — no errors but warnings to review (amber, no green banner)
        //   • Ready for next phase — no errors and no warnings (green banner)
        // The sample-incomplete blocker gets its own explicit status wording so it
        // reads as a distinct "not ready for Statistics" gate, not a generic error.
        DxValStatusText.Text = report.SampleSizeIncomplete
            ? "Blocked: upload a complete CSV before Statistics"
            : report.StatusText;
        if (!report.IsReady)
        {
            DxValStatusText.Foreground = (Brush)FindResource("DangerBrush");
            DxValStatusPill.Background = (Brush)FindResource("DangerSoftBrush");
            DxReadyBanner.Visibility = Visibility.Collapsed;
        }
        else if (report.Warnings.Count > 0)
        {
            DxValStatusText.Foreground = (Brush)FindResource("WarningBrush");
            DxValStatusPill.Background = (Brush)FindResource("WarningSoftBrush");
            DxReadyBanner.Visibility = Visibility.Collapsed;   // don't imply "all clean"
        }
        else
        {
            DxValStatusText.Foreground = (Brush)FindResource("SuccessBrush");
            DxValStatusPill.Background = (Brush)FindResource("SuccessSoftBrush");
            DxReadyBanner.Visibility = Visibility.Visible;
            DxReadyBannerText.Text = "Your extraction sheet is ready for the next phase.";
        }
    }

    // =====================================================================
    // Research Lab (Phase 3) — Extraction Sheet undo / redo (session-only)
    // =====================================================================

    // Exact snapshot of the current sheet (Ids preserved so restore is faithful).
    private List<ResearchVariable> SnapshotSheet()
        => _extractionVariables.Select(v => { var c = v.Clone(); c.Id = v.Id; return c; }).ToList();

    // True if the two snapshots differ in anything a user can see — row count,
    // order/identity, or any editable field. Used to decide whether a grid cell
    // edit was a REAL change (vs. entering a cell and leaving it unchanged),
    // so Undo/Redo history only grows on real edits.
    private static bool SheetSnapshotsDiffer(List<ResearchVariable> a, List<ResearchVariable> b)
    {
        if (a.Count != b.Count) return true;
        for (int i = 0; i < a.Count; i++)
        {
            var x = a[i]; var y = b[i];
            if (x.Id != y.Id) return true;
            if (x.VariableName != y.VariableName) return true;
            if (x.QuestionLabel != y.QuestionLabel) return true;
            if (x.VariableType != y.VariableType) return true;
            if (x.MeasurementLevel != y.MeasurementLevel) return true;
            if (x.Role != y.Role) return true;
            if (x.Coding != y.Coding) return true;
            if (x.ValueLabels != y.ValueLabels) return true;
            if (x.MissingValueRule != y.MissingValueRule) return true;
            if (x.Source != y.Source) return true;
            if (x.Notes != y.Notes) return true;
            if (x.IsRequired != y.IsRequired) return true;
        }
        return false;
    }

    // Fires the instant a grid cell enters edit mode (typing, F2, double-click,
    // or a checkbox/combo click) — captures the "before" state. Does NOT push
    // onto the undo stack yet; that only happens on commit if something actually
    // changed (see DxGrid_CellEditEnding). Never fires for scrolling, selection,
    // hover, or opening/closing dialogs — those never enter cell edit mode.
    private void DxGrid_BeginningEdit(object sender, DataGridBeginningEditEventArgs e)
    {
        try { _dxPendingCellSnapshot = SnapshotSheet(); }
        catch { _dxPendingCellSnapshot = null; }
    }

    // Fires when a cell edit ends. On Commit, the new value is not yet written
    // back to the bound ResearchVariable at this point in the WPF edit lifecycle
    // (that happens right after this handler returns), so the actual-change
    // check is deferred to the next dispatcher pass. On Cancel (e.g. Escape with
    // no real change), the pending snapshot is discarded — no bogus undo entry.
    private void DxGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        var pending = _dxPendingCellSnapshot;
        _dxPendingCellSnapshot = null;
        if (pending is null || e.EditAction != DataGridEditAction.Commit) return;

        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            try
            {
                if (!SheetSnapshotsDiffer(pending, SnapshotSheet())) return;
                _dxUndo.Push(pending);
                _dxRedo.Clear();
                UpdateUndoRedoButtons();
                if (CurrentResearchProject() is { } p)
                {
                    p.ExtractionValidationReport = null;   // sheet changed
                    RenderValidation(null);
                    PersistExtraction(p);
                }
            }
            catch (Exception ex) { HandleDxError("track the cell edit", ex); }
        }));
    }

    // Call BEFORE any mutation of the sheet (add/edit/delete/duplicate/clear/
    // apply AI sheet/apply AI fixes/conflict actions). A new action clears redo.
    private void PushDxUndo()
    {
        _dxUndo.Push(SnapshotSheet());
        _dxRedo.Clear();
        UpdateUndoRedoButtons();
    }

    private void UpdateUndoRedoButtons()
    {
        DxUndoBtn.IsEnabled = _dxUndo.Count > 0;
        DxRedoBtn.IsEnabled = _dxRedo.Count > 0;
    }

    private void RestoreSheetSnapshot(ResearchProject p, List<ResearchVariable> snap)
    {
        _extractionVariables.Clear();
        foreach (var v in snap) _extractionVariables.Add(v);
        p.ExtractionValidationReport = null;   // restored sheet needs re-validation
        RenderValidation(null);
        DxGrid.Items.Refresh();
        PersistExtraction(p);
    }

    private void UndoSheet_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var p = CurrentResearchProject();
            if (p is null) { ShowToast("Open a project first."); return; }
            if (_dxUndo.Count == 0) { ShowToast("Nothing to undo."); return; }
            _dxRedo.Push(SnapshotSheet());
            RestoreSheetSnapshot(p, _dxUndo.Pop());
            UpdateUndoRedoButtons();
            ShowToast("Undone.");
        }
        catch (Exception ex) { HandleDxError("undo the change", ex); }
    }

    private void RedoSheet_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var p = CurrentResearchProject();
            if (p is null) { ShowToast("Open a project first."); return; }
            if (_dxRedo.Count == 0) { ShowToast("Nothing to redo."); return; }
            _dxUndo.Push(SnapshotSheet());
            RestoreSheetSnapshot(p, _dxRedo.Pop());
            UpdateUndoRedoButtons();
            ShowToast("Redone.");
        }
        catch (Exception ex) { HandleDxError("redo the change", ex); }
    }

    // =====================================================================
    // Research Lab (Phase 3) — pre-run delay for AI actions
    //
    // Every Data Extraction AI action passes through a 5-second countdown with
    // Cancel / Start Now, so a stray click never costs API usage.
    // =====================================================================

    private void StartAiWithDelay(string title, Action action)
    {
        CancelAiDelayCore();   // never stack two pending actions
        _aiDelayAction = action;
        _aiDelaySecondsLeft = 5;
        AiDelayTitle.Text = title;
        AiDelayCountText.Text = "Starting in 5 seconds…";
        AiDelayOverlay.Visibility = Visibility.Visible;
        FadeIn(AiDelayOverlay, 150);

        _aiDelayTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _aiDelayTimer.Tick += (_, _) =>
        {
            _aiDelaySecondsLeft--;
            if (_aiDelaySecondsLeft <= 0)
            {
                var action2 = _aiDelayAction;
                CancelAiDelayCore();
                action2?.Invoke();
            }
            else
            {
                AiDelayCountText.Text = $"Starting in {_aiDelaySecondsLeft} second{(_aiDelaySecondsLeft == 1 ? "" : "s")}…";
            }
        };
        _aiDelayTimer.Start();
    }

    // Stops the timer and hides the overlay WITHOUT running the action.
    private void CancelAiDelayCore()
    {
        _aiDelayTimer?.Stop();
        _aiDelayTimer = null;
        _aiDelayAction = null;
        AiDelayOverlay.Visibility = Visibility.Collapsed;
    }

    private void AiDelayCancel_Click(object sender, RoutedEventArgs e)
    {
        CancelAiDelayCore();
        ShowToast("Cancelled — no AI request was sent.");
    }

    private void AiDelayStartNow_Click(object sender, RoutedEventArgs e)
    {
        var action = _aiDelayAction;
        CancelAiDelayCore();
        action?.Invoke();
    }

    // =====================================================================
    // Research Lab — long-running Research AI work overlay
    //
    // Used by every HEAVY action (AI Recommendations, Proposal Draft, Import
    // Existing Proposal extraction, Data Extraction Generate-by-AI, Google
    // Form/Paste Questions -> variables, Fix with Research AI). BLOCKING by
    // design: the progress panel stays in front of the user (no background
    // mode) with a simple step checklist, a live elapsed timer, and a REAL
    // Cancel (the returned token actually cancels the in-flight HTTP request).
    //
    // Deliberately separate from ShowLoading/HideLoading/LoadingOverlay, which
    // remain exactly as they were for flashcard generation and other fast,
    // short-lived actions (Test Research AI, reading a Google Form link).
    // =====================================================================

    private readonly System.Collections.ObjectModel.ObservableCollection<AiWorkStep> _aiWorkSteps = new();
    private int _aiWorkStepIndex = -1;

    private CancellationToken BeginAiWork(string actionName, params string[] steps)
    {
        EndAiWork();   // defensive: never stack two heavy actions

        _aiWorkCts = new CancellationTokenSource();
        _aiWorkStartedAt = DateTime.UtcNow;

        AiWorkTitleText.Text = "Research AI is working";
        AiWorkActionText.Text = actionName;
        AiWorkStillWorkingNote.Visibility = Visibility.Collapsed;
        AiWorkElapsedText.Text = "0:00";

        // Build the step checklist; the first step starts as the current one.
        _aiWorkSteps.Clear();
        foreach (var s in steps) _aiWorkSteps.Add(new AiWorkStep(s));
        _aiWorkStepIndex = _aiWorkSteps.Count > 0 ? 0 : -1;
        if (_aiWorkStepIndex >= 0) _aiWorkSteps[0].State = "Current";
        if (AiWorkStepsList.ItemsSource is null) AiWorkStepsList.ItemsSource = _aiWorkSteps;

        AiWorkingOverlay.Visibility = Visibility.Visible;
        AiWorkingOverlay.Opacity = 0;
        FadeIn(AiWorkingOverlay, 150);

        _aiWorkTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _aiWorkTimer.Tick += (_, _) => UpdateAiWorkElapsed();
        _aiWorkTimer.Start();

        return _aiWorkCts.Token;
    }

    private void UpdateAiWorkElapsed()
    {
        var elapsed = DateTime.UtcNow - _aiWorkStartedAt;
        AiWorkElapsedText.Text = elapsed.Hours > 0
            ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}"
            : $"{elapsed.Minutes}:{elapsed.Seconds:D2}";

        if (elapsed.TotalSeconds >= ResearchAiTimeouts.StillWorkingNoticeSeconds)
            AiWorkStillWorkingNote.Visibility = Visibility.Visible;
    }

    // Checks off the current step and highlights the next one. Called only at
    // REAL workflow boundaries (input prepared, request sent, response received,
    // output validated) — never on a fake timer, so the checklist always tells
    // the truth about where the action actually is.
    private void AiWorkAdvance()
    {
        if (_aiWorkStepIndex < 0 || _aiWorkStepIndex >= _aiWorkSteps.Count) return;
        _aiWorkSteps[_aiWorkStepIndex].State = "Done";
        _aiWorkStepIndex++;
        if (_aiWorkStepIndex < _aiWorkSteps.Count) _aiWorkSteps[_aiWorkStepIndex].State = "Current";
    }

    // Clean completion state: every step checked + "Completed" title, held
    // briefly so the user sees the full green checklist before the panel closes
    // (the caller's finally block hides it via EndAiWork).
    private async Task CompleteAiWorkAsync()
    {
        foreach (var s in _aiWorkSteps) s.State = "Done";
        _aiWorkStepIndex = _aiWorkSteps.Count;
        AiWorkTitleText.Text = "Completed";
        try { await Task.Delay(650); } catch { /* never throws in practice */ }
    }

    private void EndAiWork()
    {
        _aiWorkTimer?.Stop();
        _aiWorkTimer = null;
        _aiWorkCts?.Dispose();
        _aiWorkCts = null;
        AiWorkingOverlay.Visibility = Visibility.Collapsed;
    }

    // Cancel: actually cancels the in-flight request. Does NOT hide the overlay
    // here — the awaited call will observe the cancellation almost immediately
    // and the calling RunXxx method's own finally block calls EndAiWork(), so
    // the panel closes as part of that same non-destructive failure path.
    private void AiWorkCancel_Click(object sender, RoutedEventArgs e) => _aiWorkCts?.Cancel();

    // =====================================================================
    // Research Lab — generic AI failure/timeout overlay
    //
    // Shown when a heavy action fails after running for the full allowed
    // duration (or a provider/network error), never for a user-initiated
    // Cancel (that gets its own lightweight toast). Retry re-invokes the exact
    // same start delegate, which reuses whatever compact input that action
    // already builds — never a fresh huge raw payload. Nothing is ever cleared
    // before showing this panel.
    // =====================================================================

    private void ShowAiFailure(string message, Action retryAction)
    {
        AiFailureMessageText.Text = message;
        _aiFailureRetryAction = retryAction;
        AiFailureOverlay.Visibility = Visibility.Visible;
        FadeIn(AiFailureOverlay, 150);
    }

    private void AiFailureRetry_Click(object sender, RoutedEventArgs e)
    {
        AiFailureOverlay.Visibility = Visibility.Collapsed;
        var action = _aiFailureRetryAction;
        _aiFailureRetryAction = null;
        action?.Invoke();
    }

    private void AiFailureContinueManually_Click(object sender, RoutedEventArgs e)
    {
        AiFailureOverlay.Visibility = Visibility.Collapsed;
        _aiFailureRetryAction = null;
    }

    // =====================================================================
    // Research Lab (Phase 3) — Resolve Sheet–Sample Conflicts
    //
    // Conflicts are rebuilt from live project state (never persisted). Every
    // conflict can be fixed manually; Fix with Research AI is optional and its
    // proposals are always reviewed before applying. No statistics anywhere.
    // =====================================================================

    private static ExtractionConflict? ConflictOf(object sender)
        => (sender as FrameworkElement)?.DataContext as ExtractionConflict;

    // Stable key: kind + normalized identity (column for CSV-side conflicts,
    // variable/question name otherwise). Matches the keys ValidateExtractionSheet
    // uses, so ignoring/resolving a conflict also clears the matching validation
    // warning — and survives a Validate re-run / app restart via IgnoredConflictKeys.
    private static string ConflictKey(ExtractionConflict c)
    {
        string identity = (c.Kind is "CsvOnly" or "Type")
            ? (c.Column?.Name ?? "")
            : (c.Variable?.VariableName ?? c.Column?.Name ?? "");
        return MakeConflictKey(c.Kind, identity);
    }

    private static string MapCsvType(string inferred) => inferred switch
    {
        "Numeric" => "Numeric",
        "Binary" => "Binary",
        "Categorical" => "Categorical",
        "Date" => "Date",
        "Text" => "Text",
        _ => "Unknown"
    };

    // Conservative type-mismatch check: only flags combinations that cannot be
    // right (e.g. text data in a numeric variable), never guesses.
    private static bool IsCsvTypeMismatch(CsvColumnSummary col, ResearchVariable v)
    {
        string t = (v.VariableType ?? "").Trim();
        if (t.Length == 0 || t.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
            || t.Equals("ID", StringComparison.OrdinalIgnoreCase)
            || t.Equals("Text", StringComparison.OrdinalIgnoreCase)) return false;
        return col.InferredType switch
        {
            "Text" => t is "Numeric" or "Continuous" or "Date" or "Binary" or "Ordinal",
            "Date" => t is "Numeric" or "Continuous" or "Binary",
            _ => false
        };
    }

    private List<ExtractionConflict> BuildConflicts(ResearchProject p)
    {
        var list = new List<ExtractionConflict>();
        var vars = _extractionVariables.ToList();

        // --- Sheet-internal issues ---
        foreach (var grp in vars.Where(v => !string.IsNullOrWhiteSpace(v.VariableName))
                                .GroupBy(v => v.VariableName.Trim().ToLowerInvariant())
                                .Where(g => g.Count() > 1))
            foreach (var v in grp.Skip(1))
                list.Add(new ExtractionConflict
                {
                    Kind = "Duplicate",
                    Severity = "Error",
                    Title = $"Duplicate variable name “{v.VariableName.Trim()}”",
                    Detail = "Two or more rows share this name. Rename this row or delete it.",
                    Variable = v,
                    RenameVis = Visibility.Visible,
                    DeleteVis = Visibility.Visible
                });

        int rowNo = 0;
        foreach (var v in vars)
        {
            rowNo++;
            string name = (v.VariableName ?? "").Trim();
            if (name.Length == 0)
            {
                list.Add(new ExtractionConflict
                {
                    Kind = "Name",
                    Severity = "Error",
                    Title = $"Row {rowNo} has no variable name",
                    Detail = "Give it a short machine name (e.g. sleep_hours) or delete the row.",
                    Variable = v,
                    RenameVis = Visibility.Visible,
                    DeleteVis = Visibility.Visible
                });
                continue;
            }
            if (!ValidNameRegex.IsMatch(name))
                list.Add(new ExtractionConflict
                {
                    Kind = "Name",
                    Severity = "Error",
                    Title = $"Variable name “{name}” is invalid",
                    Detail = "Use letters, numbers and underscores only, with no spaces or symbols (e.g. sleep_hours).",
                    Variable = v,
                    RenameVis = Visibility.Visible,
                    DeleteVis = Visibility.Visible
                });

            bool unknownType = string.IsNullOrWhiteSpace(v.VariableType) || v.VariableType.Equals("Unknown", StringComparison.OrdinalIgnoreCase);
            bool unknownRole = string.IsNullOrWhiteSpace(v.Role) || v.Role.Equals("Unknown", StringComparison.OrdinalIgnoreCase);
            if (unknownType || unknownRole)
                list.Add(new ExtractionConflict
                {
                    Kind = "Meta",
                    Title = $"“{name}” has an unknown {(unknownType && unknownRole ? "type and role" : unknownType ? "type" : "role")}",
                    Detail = "Set the specific type and role so the sheet is unambiguous.",
                    Variable = v,
                    EditVis = Visibility.Visible
                });

            bool needsCoding = v.VariableType is "Categorical" or "Binary" or "Ordinal";
            if (needsCoding && string.IsNullOrWhiteSpace(v.Coding) && string.IsNullOrWhiteSpace(v.ValueLabels))
                list.Add(new ExtractionConflict
                {
                    Kind = "Coding",
                    Title = $"“{name}” has no value labels",
                    Detail = $"A {v.VariableType.ToLowerInvariant()} variable needs coding (e.g. 0 = No, 1 = Yes).",
                    Variable = v,
                    EditVis = Visibility.Visible
                });
        }

        if (vars.Count > 0 && !vars.Any(v => string.Equals(v.Role, "Outcome", StringComparison.OrdinalIgnoreCase)))
            list.Add(new ExtractionConflict
            {
                Kind = "Outcome",
                Title = "No primary outcome variable is marked",
                Detail = "Mark the variable that answers your research question as “Outcome”, or ignore this if intentional."
            });

        // --- Plan/recommendation variables not yet in the sheet ---
        if (p.Recommendations is { SuggestedVariables.Count: > 0 })
        {
            var sheetTokens = vars.SelectMany(v => new[] { v.VariableName, v.QuestionLabel })
                                  .Where(s => !string.IsNullOrWhiteSpace(s))
                                  .Select(s => s.Trim().ToLowerInvariant()).ToList();
            foreach (var rv in p.Recommendations.SuggestedVariables)
            {
                string key = rv.HeaderDisplay.Trim().ToLowerInvariant();
                if (key.Length < 3) continue;
                if (sheetTokens.Any(t => t.Contains(key) || key.Contains(t))) continue;

                string protoName = SanitizeVarName(rv.HeaderDisplay);
                var proto = new ResearchVariable
                {
                    VariableName = protoName.Length == 0 ? "variable" : protoName,
                    QuestionLabel = rv.VariableLabel,
                    VariableType = ResearchVariableOptions.VariableTypes.FirstOrDefault(t => string.Equals(t, rv.VariableType, StringComparison.OrdinalIgnoreCase)) ?? "Unknown",
                    Role = ResearchVariableOptions.Roles.FirstOrDefault(r => string.Equals(r, rv.Role, StringComparison.OrdinalIgnoreCase)) ?? "Unknown",
                    Coding = rv.SuggestedCoding,
                    Source = "AI Recommendation",
                    Notes = rv.Notes
                };
                list.Add(new ExtractionConflict
                {
                    Kind = "PlanVar",
                    Severity = "Suggestion",
                    Source = "Recommendations",
                    Title = $"The research plan mentions “{rv.HeaderDisplay.Trim()}”, but no matching variable was found",
                    Detail = "Add it to the sheet so the plan and the sheet stay aligned, or ignore it if it is covered by another variable.",
                    Variable = proto,   // prototype — not in the sheet until added
                    AddVis = Visibility.Visible
                });
            }
        }

        // --- Sheet ↔ CSV sample conflicts (robust matching; system columns skipped) ---
        if (p.CsvSampleSummary is { Columns.Count: > 0 } csv)
        {
            var match = MatchCsvToSheet(vars, csv);
            // Suggest matches from the still-unmatched (sheet-only) variables. If a
            // column looks like a likely match to one of them, surface that as the
            // pre-selected option so the user can confirm it in one click.
            var sheetOnlyNames = match.SheetOnly.Select(v => v.VariableName.Trim()).Distinct().ToList();

            foreach (var col in match.CsvOnly)
            {
                string? suggested = match.SheetOnly
                    .FirstOrDefault(v => VariableMatchesColumnFuzzy(v, NormalizeLabel(col.Name)))
                    ?.VariableName.Trim();
                list.Add(new ExtractionConflict
                {
                    Kind = "CsvOnly",
                    Source = "CSV",
                    Title = $"CSV column “{col.Name}” is not in the extraction sheet",
                    Detail = $"Detected as {col.InferredType} with {col.UniqueCount} unique values ({col.MissingPercent}% missing). Add it as a new variable, match it to an existing one, or ignore it.",
                    Column = col,
                    AddVis = Visibility.Visible,
                    MatchVis = sheetOnlyNames.Count > 0 ? Visibility.Visible : Visibility.Collapsed,
                    MatchCandidates = new List<string>(sheetOnlyNames),
                    SelectedMatch = suggested
                });
            }

            foreach (var kv in match.ColumnMatch)
            {
                if (IsCsvTypeMismatch(kv.Key, kv.Value))
                    list.Add(new ExtractionConflict
                    {
                        Kind = "Type",
                        Source = "CSV",
                        Title = $"“{kv.Value.VariableName.Trim()}”: type may not match the CSV data",
                        Detail = $"The CSV column “{kv.Key.Name}” looks {kv.Key.InferredType}, but the sheet says {kv.Value.VariableType}. Edit the variable if the sheet is wrong, or ignore if the CSV sample is unusual.",
                        Variable = kv.Value,
                        Column = kv.Key,
                        EditVis = Visibility.Visible
                    });
            }

            foreach (var v in match.SheetOnly)
                list.Add(new ExtractionConflict
                {
                    Kind = "SheetOnly",
                    Title = $"Variable “{v.VariableName.Trim()}” is missing from the CSV",
                    Detail = "The uploaded sample has no matching column. Rename it to match a CSV column, delete it, or ignore it if that data comes later.",
                    Variable = v,
                    RenameVis = Visibility.Visible,
                    DeleteVis = Visibility.Visible
                });

            int? target = p.TargetSampleSize;
            if (target is > 0 && csv.TotalRows > 0 && csv.TotalRows < target)
                list.Add(new ExtractionConflict
                {
                    Kind = "SampleIncomplete",
                    Severity = "Error",
                    Source = "Proposal",
                    Title = $"Sample size is incomplete ({csv.TotalRows} of {target})",
                    Detail = $"More samples are required before statistics can be run. Upload a complete CSV with all {target} samples "
                        + $"({target - csv.TotalRows} more). This cannot be ignored or auto-resolved."
                });
        }

        // The sample-incomplete blocker is NEVER filtered by ignored keys — more
        // real data is required, so it always shows until the CSV is complete.
        return list.Where(c => c.Kind == "SampleIncomplete" || !_ignoredConflictKeys.Contains(ConflictKey(c))).ToList();
    }

    private void RefreshConflicts(ResearchProject p)
    {
        var conflicts = BuildConflicts(p);
        _conflicts.Clear();
        foreach (var c in conflicts) _conflicts.Add(c);

        DxConflictCountText.Text = conflicts.Count == 0 ? "No conflicts"
            : conflicts.Count == 1 ? "1 conflict" : $"{conflicts.Count} conflicts";
        DxConflictEmptyText.Text = p.ExtractionValidationReport?.IsReady == true
            ? "No conflicts remaining — your extraction sheet is ready for the next phase."
            : "No conflicts detected between the sheet, your sample, and the plan.";
        DxConflictEmpty.Visibility = conflicts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // Opens the Resolve Conflicts window. Manual resolution is the default path
    // (Part F): validate first, and if there is nothing to resolve, say so
    // instead of opening an empty modal or pushing the user toward AI.
    private void OpenConflicts_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var p = CurrentResearchProject();
            if (p is null) { ShowToast("Open a project first."); return; }
            TryCommitGridEdit();
            CommitGrid(p);
            if (_extractionVariables.Count == 0) { ShowToast("Add or generate variables first, then resolve conflicts."); return; }
            if (TryExtractTargetSampleSize(p, out int target)) p.TargetSampleSize = target;

            p.ExtractionValidationReport = ValidateExtractionSheet(p);
            UpdateExtractionStatus(p);
            RenderValidation(p.ExtractionValidationReport);
            SaveResearch();

            if (ConflictList.ItemsSource is null) ConflictList.ItemsSource = _conflicts;
            RefreshConflicts(p);

            if (_conflicts.Count == 0)
            {
                ShowToast("No conflicts found. Your extraction sheet is ready for the next step.");
                return;
            }

            // Fresh session: no stale AI-timeout banner, no stale fix flag.
            DxConflictAiTimeout.Visibility = Visibility.Collapsed;
            _fixFromConflicts = false;

            // Snapshot so Cancel can discard all staged decisions + any immediate
            // manual edits made while the window is open.
            _conflictSnapshot = SnapshotSheet();
            _conflictIgnoredSnapshot = new HashSet<string>(_ignoredConflictKeys);

            ConflictOverlay.Visibility = Visibility.Visible;
            FadeIn(ConflictOverlay, 150);
        }
        catch (Exception ex) { HandleDxError("open the conflict window", ex); }
    }

    private void UpdateConflictCountText()
        => DxConflictCountText.Text = _conflicts.Count == 0 ? "No conflicts"
            : _conflicts.Count == 1 ? "1 conflict" : $"{_conflicts.Count} conflicts";

    // ---- Staged manual actions (nothing touches the sheet until Save) ------

    private void ConflictAdd_Click(object sender, RoutedEventArgs e)
    {
        if (ConflictOf(sender) is not { } c) return;
        if (c.Column is null && !(c.Kind == "PlanVar" && c.Variable is not null)) return;
        c.StagedAction = "Add";
        c.Status = "Resolved";
        ShowToast("Staged: add to sheet. Choose Save and Close to apply.");
    }

    private void ConflictMatch_Click(object sender, RoutedEventArgs e)
    {
        if (ConflictOf(sender) is not { Column: not null } c) return;
        if (string.IsNullOrWhiteSpace(c.SelectedMatch)) { ShowToast("Choose a variable to match first."); return; }
        c.StagedAction = "Match";
        c.Status = "Resolved";
        ShowToast($"Staged: match to “{c.SelectedMatch}”. Choose Save and Close to apply.");
    }

    // Rename / Edit manually open the full editor and apply immediately (a
    // direct manual edit). Cancel still reverts everything via the snapshot.
    private void ConflictEdit_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (ConflictOf(sender) is not { Variable: { } v } c) return;
            c.Status = "Needs review";
            OpenVariableEditor(v);
        }
        catch (Exception ex) { HandleDxError("open the variable editor", ex); }
    }

    private void ConflictDelete_Click(object sender, RoutedEventArgs e)
    {
        if (ConflictOf(sender) is not { Variable: not null } c) return;
        c.StagedAction = "Delete";
        c.Status = "Resolved";
        ShowToast("Staged: delete from sheet. Choose Save and Close to apply.");
    }

    private void ConflictIgnore_Click(object sender, RoutedEventArgs e)
    {
        if (ConflictOf(sender) is not { } c) return;
        c.StagedAction = "Ignore";
        c.Status = "Ignored";
    }

    private void ConflictMarkResolved_Click(object sender, RoutedEventArgs e)
    {
        if (ConflictOf(sender) is not { } c) return;
        c.StagedAction = "MarkResolved";
        c.Status = "Resolved";
    }

    // Cancel: discard ALL staged decisions and any immediate edits, restoring the
    // sheet + ignored set to how they were when the window opened.
    private void ConflictCancel_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var p = CurrentResearchProject();
            ConflictOverlay.Visibility = Visibility.Collapsed;
            if (p is null) return;

            if (_conflictSnapshot is not null)
            {
                _extractionVariables.Clear();
                foreach (var v in _conflictSnapshot) _extractionVariables.Add(v);
                DxGrid.Items.Refresh();
            }
            if (_conflictIgnoredSnapshot is not null)
            {
                _ignoredConflictKeys.Clear();
                foreach (var k in _conflictIgnoredSnapshot) _ignoredConflictKeys.Add(k);
            }
            _conflictSnapshot = null;
            _conflictIgnoredSnapshot = null;
            _fixFromConflicts = false;
            DxConflictAiTimeout.Visibility = Visibility.Collapsed;

            p.ExtractionValidationReport = null;
            RenderValidation(null);
            PersistExtraction(p);
            _conflicts.Clear();
            ShowToast("Changes discarded — no conflict decisions were saved.");
        }
        catch (Exception ex) { HandleDxError("cancel the conflicts", ex); }
    }

    // Save and Close: apply every staged decision to the sheet, then re-validate
    // and refresh so the warning/conflict count reflects the new state.
    private void ConflictSaveClose_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var p = CurrentResearchProject();
            if (p is null) { ConflictOverlay.Visibility = Visibility.Collapsed; return; }

            // One undo step for the whole resolution session.
            if (_conflictSnapshot is not null) { _dxUndo.Push(_conflictSnapshot); _dxRedo.Clear(); UpdateUndoRedoButtons(); }

            int applied = 0;
            foreach (var c in _conflicts.ToList())
            {
                switch (c.StagedAction)
                {
                    case "Add":
                        ResearchVariable? nv = null;
                        if (c.Column is { } col)
                            nv = new ResearchVariable
                            {
                                VariableName = MakeUniqueName(col.Name),
                                VariableType = MapCsvType(col.InferredType),
                                MeasurementLevel = col.IsLikelyCategorical ? "Nominal" : (col.InferredType == "Numeric" ? "Scale" : "NotApplicable"),
                                Role = "Unknown", Source = "CSV Sample",
                                Notes = col.IsLikelyCategorical ? "Add value labels for each category." : ""
                            };
                        else if (c.Kind == "PlanVar" && c.Variable is { } proto)
                        {
                            nv = proto.Clone();
                            nv.VariableName = MakeUniqueName(proto.VariableName);
                        }
                        if (nv is not null) { _extractionVariables.Add(nv); applied++; }
                        break;

                    case "Match" when c.Column is { } mcol && !string.IsNullOrWhiteSpace(c.SelectedMatch):
                        var mv = _extractionVariables.FirstOrDefault(x => string.Equals(x.VariableName.Trim(), c.SelectedMatch!.Trim(), StringComparison.OrdinalIgnoreCase));
                        if (mv is not null)
                        {
                            // Persist the match as a source-column alias (keeps the
                            // student's chosen short name; the CSV header will now
                            // match this variable on every future validate run).
                            if (!mv.SourceColumnAliases.Any(a => NormalizeLabel(a) == NormalizeLabel(mcol.Name)))
                                mv.SourceColumnAliases.Add(mcol.Name);
                            if (string.IsNullOrWhiteSpace(mv.VariableType) || mv.VariableType == "Unknown") mv.VariableType = MapCsvType(mcol.InferredType);
                            applied++;
                        }
                        break;

                    case "Delete" when c.Variable is { } dv:
                        if (_extractionVariables.Remove(dv)) applied++;
                        break;

                    case "Ignore":
                    case "MarkResolved":
                        _ignoredConflictKeys.Add(ConflictKey(c));
                        applied++;
                        break;
                }
            }

            DxGrid.Items.Refresh();
            ConflictOverlay.Visibility = Visibility.Collapsed;
            _conflictSnapshot = null;
            _conflictIgnoredSnapshot = null;
            _fixFromConflicts = false;
            DxConflictAiTimeout.Visibility = Visibility.Collapsed;

            // Persist the resolved/ignored decisions so they survive a Validate
            // re-run and app restart, then re-validate from the new sheet state.
            p.IgnoredConflictKeys = _ignoredConflictKeys.ToList();
            CommitGrid(p);
            if (TryExtractTargetSampleSize(p, out int tt)) p.TargetSampleSize = tt;
            var report = ValidateExtractionSheet(p);
            p.ExtractionValidationReport = report;
            UpdateExtractionStatus(p);
            PersistExtraction(p);
            RenderValidation(report);
            _conflicts.Clear();   // window is closing; rebuilt fresh on next open

            int remaining = report.Errors.Count + report.Warnings.Count;
            ShowToast(applied == 0
                ? "No changes to save."
                : remaining == 0
                    ? $"Saved. All conflicts resolved — your extraction sheet is ready for the next step."
                    : $"Saved {applied} decision{(applied == 1 ? "" : "s")}. {remaining} item{(remaining == 1 ? "" : "s")} still need review.");
        }
        catch (Exception ex) { HandleDxError("save the conflict decisions", ex); }
    }

    // Optional AI assist — runs WITHOUT leaving the Resolve Conflicts modal. The
    // conflict window stays open underneath the delay/progress/review overlays, so
    // a failure lands the student right back in the manual workflow with
    // everything intact. Manual resolution stays first-class and local.
    private void ConflictFixAi_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var p = CurrentResearchProject();
            if (p is null) return;
            if (_extractionVariables.Count == 0) { ShowToast("Add or generate variables first."); return; }
            if (!_researchAi.IsConfigured) { ShowToast("Research AI is not set up yet. Add an endpoint or enable a development provider in AI Settings, then try again."); return; }

            DxConflictAiTimeout.Visibility = Visibility.Collapsed;
            _fixFromConflicts = true;

            // Validate locally, then build the compact per-conflict input from the
            // ACTIVE conflicts only (the modal's list is already filtered of
            // resolved/ignored items). Only keys/titles/names are sent — never raw
            // CSV rows, never proposal text, never resolved conflicts.
            TryCommitGridEdit();
            CommitGrid(p);
            p.ExtractionValidationReport = ValidateExtractionSheet(p);
            RefreshConflicts(p);

            if (_conflicts.Count == 0)
            {
                ShowToast("No active conflicts to fix — your extraction sheet is in good shape.");
                return;
            }

            var inputs = _conflicts.Select(c => new ConflictFixInput
            {
                ConflictKey = ConflictKey(c),
                Kind = c.Kind,
                Severity = c.Severity,
                Title = c.Title,
                Detail = c.Detail,
                VariableName = c.Variable?.VariableName ?? "",
                ColumnName = c.Column?.Name ?? ""
            }).ToList();

            // StartAiWithDelay shows a 5s cancelable countdown — cancelling here
            // sends no AI request at all.
            StartAiWithDelay("Fix with Research AI", () => _ = RunConflictFixes(p, inputs));
        }
        catch (Exception ex) { HandleDxError("start the AI fixes", ex); }
    }

    // ---- Fix with Research AI (optional; only from the Resolve Conflicts modal) --

    // Keys of the conflicts actually sent, so a garbled/echoed-wrong key from the
    // model can never mark an unrelated conflict as resolved.
    private readonly HashSet<string> _fixSentKeys = new();

    private async Task RunConflictFixes(ResearchProject p, List<ConflictFixInput> inputs)
    {
        var ct = BeginAiWork("Fixing Conflicts",
            "Preparing active conflicts", "Sending compact conflict list", "Waiting for fixes",
            "Validating fixes", "Preparing fix review");
        try
        {
            _fixSentKeys.Clear();
            foreach (var i in inputs) _fixSentKeys.Add(i.ConflictKey);
            AiWorkAdvance();   // active conflicts prepared → sending
            AiWorkAdvance();   // compact list sent → waiting
            var result = await _researchAi.SuggestConflictFixesAsync(p, inputs, ct);
            AiWorkAdvance();   // fixes received + parsed → validating

            if (PresentFixProposals(p, result, isLocalFallback: false))
            {
                await CompleteAiWorkAsync();
            }
            else
            {
                // Provider replied, but nothing usable survived filtering — offer
                // safe local suggestions rather than dropping the student.
                ShowLocalFixFallback(p, inputs,
                    "Research AI did not return usable conflict fixes, so the app prepared safe local cleanup suggestions instead.");
            }
        }
        catch (OperationCanceledException)
        {
            ShowToast("Cancelled. Your existing content was kept.");
        }
        catch (ResearchAiException ex)
        {
            // Never dead-end. A missing key is a real config problem the student
            // must fix, so keep the retry/manual banner for "auth". Every other
            // failure (parse_failure, empty_response, provider_error, timeout,
            // network, overload…) falls back to safe LOCAL cleanup suggestions
            // shown in the same review screen, with the diagnostics still available.
            _lastAiDiagnostics = ex.Diagnostics;
            if (ex.Category == "auth")
                HandleFixFailure(ex.Message, ex.Diagnostics, ex.IsTimeout);
            else
                ShowLocalFixFallback(p, inputs, LocalFallbackMessage(ex.Category));
        }
        catch (Exception)
        {
            ShowLocalFixFallback(p, inputs,
                "Research AI could not suggest fixes, so the app prepared safe local cleanup suggestions instead.");
        }
        finally { EndAiWork(); }
    }

    // Wording for the toast shown when local cleanup suggestions replace a failed
    // Research AI response. Category-specific so the student knows what happened.
    private static string LocalFallbackMessage(string? category) => category switch
    {
        "parse_failure" => "Research AI response could not be parsed, so the app prepared safe local cleanup suggestions instead.",
        "empty_response" => "Research AI returned an empty response, so the app prepared safe local cleanup suggestions instead.",
        "timeout" => "Research AI did not finish in time, so the app prepared safe local cleanup suggestions instead.",
        "network" => "Research AI could not be reached, so the app prepared safe local cleanup suggestions instead.",
        _ => "Research AI could not suggest fixes, so the app prepared safe local cleanup suggestions instead."
    };

    // Fills the review list from a fix result (AI or local), applying the same
    // guards for both: keep only proposals for conflicts we actually sent, force
    // hard blockers to manual_review, and pre-select the high-confidence safe
    // items. Toggles the "Local cleanup suggestions" label. Returns false when
    // nothing usable remains, so the caller can offer a local fallback instead.
    private bool PresentFixProposals(ResearchProject p, ConflictFixResult result, bool isLocalFallback)
    {
        _fixProposals.Clear();
        foreach (var f in (result?.Fixes ?? new List<ConflictFixProposal>()).Where(f => _fixSentKeys.Contains(f.ConflictKey)))
        {
            // Belt-and-braces: a blocker (incomplete sample) can never be
            // presented as safe_ignore/mark_resolved even if the model tried.
            if (IsNonIgnorableKey(f.ConflictKey) && f.EffectiveCategory is not "manual_review")
            {
                f.Category = "manual_review";
                f.Action = "no_safe_fix";
                if (string.IsNullOrWhiteSpace(f.Explanation))
                    f.Explanation = "More samples are required before statistics can be run.";
            }
            f.Accepted = f.DefaultSelected;   // pre-select high-confidence safe fixes + safe ignores
            _fixProposals.Add(f);
        }

        if (_fixProposals.Count == 0) return false;

        EnsureFixProposalsGrouped();
        int manual = _fixProposals.Count(f => f.EffectiveCategory is "manual_review" or "no_safe_fix");
        DxFixCountText.Text = (_fixProposals.Count == 1 ? "1 proposal" : $"{_fixProposals.Count} proposals")
            + (manual > 0 ? $" · {manual} need review" : "");

        DxFixSourceLabel.Text = "Local cleanup suggestions";
        DxFixSourceLabel.Visibility = isLocalFallback ? Visibility.Visible : Visibility.Collapsed;

        var warnings = result?.Warnings ?? new List<string>();
        if (warnings.Count > 0) { DxFixWarningsList.ItemsSource = warnings; DxFixWarningsSection.Visibility = Visibility.Visible; }
        else DxFixWarningsSection.Visibility = Visibility.Collapsed;

        ExtractionFixReviewOverlay.Visibility = Visibility.Visible;
        FadeIn(ExtractionFixReviewOverlay, 150);
        return true;
    }

    // Builds safe, deterministic local cleanup suggestions and shows them in the
    // SAME review screen, labeled "Local cleanup suggestions". If even the local
    // logic produces nothing (or anything goes wrong), keep the non-destructive
    // manual banner so the student is never dropped out of the workflow.
    private void ShowLocalFixFallback(ResearchProject p, List<ConflictFixInput> inputs, string message)
    {
        try
        {
            var local = BuildLocalConflictFallback(p, inputs);
            if (PresentFixProposals(p, local, isLocalFallback: true))
                ShowToast(message);
            else
                HandleFixFailure("Research AI could not suggest fixes. You can resolve the conflicts manually.", _lastAiDiagnostics, isTimeout: false);
        }
        catch
        {
            HandleFixFailure("Research AI could not suggest fixes. You can resolve the conflicts manually.", _lastAiDiagnostics, isTimeout: false);
        }
    }

    private static readonly string[] RoutineMetadataColumns =
        { "timestamp", "username", "email address", "email", "score" };

    // Google Form / system columns that are routinely safe to ignore for analysis.
    private static bool IsRoutineMetadataColumn(string columnName)
    {
        string c = (columnName ?? "").Trim().ToLowerInvariant();
        if (c.Length == 0) return false;
        if (RoutineMetadataColumns.Contains(c)) return true;
        return c.Contains("google form") || c.Contains("form response");
    }

    // Deterministic, conservative local fallback: ONLY two kinds of automatic
    // suggestions — ignore routine form/system metadata, and alias a CSV column
    // that clearly matches an existing variable. Everything else (coding/type
    // issues, sheet-only variables, plan suggestions, unmatched columns) is left
    // for manual review, and the hard blockers (incomplete sample size, missing
    // outcome, and any non-ignorable key) are NEVER locally ignored.
    private ConflictFixResult BuildLocalConflictFallback(ResearchProject p, List<ConflictFixInput> inputs)
    {
        var result = new ConflictFixResult
        {
            Warnings = { "These are safe local cleanup suggestions prepared by the app — not Research AI output. Only routine metadata and clear column matches are proposed automatically; anything that could affect your statistics is left for manual review." }
        };

        foreach (var c in inputs ?? new List<ConflictFixInput>())
        {
            string col = (c.ColumnName ?? "").Trim();
            string varName = (c.VariableName ?? "").Trim();

            // Hard blockers are NEVER auto-ignored — always manual review.
            if (IsNonIgnorableKey(c.ConflictKey) || c.Kind is "SampleIncomplete" or "Outcome")
            {
                result.Fixes.Add(new ConflictFixProposal
                {
                    ConflictKey = c.ConflictKey, ConflictTitle = c.Title,
                    Category = "manual_review", Action = "no_safe_fix", Confidence = "high",
                    TargetVariable = varName, TargetColumn = col,
                    Explanation = c.Kind == "SampleIncomplete"
                        ? "More samples are required before statistics can be run — this cannot be ignored or auto-resolved."
                        : c.Kind == "Outcome"
                            ? "A primary outcome variable is needed — decide which variable answers your research question."
                            : "This may affect your statistics — please review it manually."
                });
                continue;
            }

            // Routine Google Form / system metadata columns are safe to ignore.
            if (c.Kind == "CsvOnly" && IsRoutineMetadataColumn(col))
            {
                result.Fixes.Add(new ConflictFixProposal
                {
                    ConflictKey = c.ConflictKey, ConflictTitle = c.Title,
                    Category = "safe_ignore", Action = "ignore", Confidence = "high",
                    TargetColumn = col,
                    Explanation = "Routine form/system column (e.g. Timestamp, Username, Email Address, Score) — safe to ignore for analysis."
                });
                continue;
            }

            // A CSV column whose header clearly matches an existing variable → alias.
            if (c.Kind == "CsvOnly" && col.Length > 0)
            {
                string norm = NormalizeLabel(col);
                var matchVar = _extractionVariables.FirstOrDefault(v => VariableMatchesColumnFuzzy(v, norm));
                if (matchVar is not null)
                {
                    result.Fixes.Add(new ConflictFixProposal
                    {
                        ConflictKey = c.ConflictKey, ConflictTitle = c.Title,
                        Category = "safe_fix", Action = "add_alias", Confidence = "medium",
                        TargetVariable = matchVar.VariableName, TargetColumn = col, ProposedValue = col,
                        Explanation = $"The CSV column “{col}” matches “{matchVar.VariableName.Trim()}” — add it as an alias so they map together."
                    });
                    continue;
                }
            }

            // Everything else → manual review (never a guessed or unsafe fix).
            result.Fixes.Add(new ConflictFixProposal
            {
                ConflictKey = c.ConflictKey, ConflictTitle = c.Title,
                Category = "manual_review", Action = "no_safe_fix", Confidence = "low",
                TargetVariable = varName, TargetColumn = col,
                Explanation = "No safe automatic fix — please review this conflict manually."
            });
        }

        return result;
    }

    // On an AI-fix failure/timeout that started INSIDE the Resolve Conflicts modal,
    // keep the modal open and show a non-destructive retry/manual banner — never
    // drop the student out of the workflow and never clear the sheet/CSV/mappings.
    private void HandleFixFailure(string message, string? diagnostics, bool isTimeout)
    {
        _lastAiDiagnostics = diagnostics;
        if (_fixFromConflicts && ConflictOverlay.Visibility == Visibility.Visible)
        {
            DxConflictAiTimeoutText.Text = isTimeout
                ? "Research AI did not finish within the allowed time while proposing conflict fixes. Your content was kept. You can still resolve conflicts manually."
                : "Research AI could not propose conflict fixes right now. You can still resolve conflicts manually.";
            DxConflictAiTimeout.Visibility = Visibility.Visible;
        }
        else ShowToast(message);
    }

    // Timeout banner — Retry AI Fix.
    private void ConflictAiRetry_Click(object sender, RoutedEventArgs e)
    {
        DxConflictAiTimeout.Visibility = Visibility.Collapsed;
        ConflictFixAi_Click(sender, e);   // rebuilds the same compact payload; no raw form/CSV
    }

    // Timeout banner — Continue Manually: just dismiss the notice, stay in the modal.
    private void ConflictAiContinueManually_Click(object sender, RoutedEventArgs e)
        => DxConflictAiTimeout.Visibility = Visibility.Collapsed;

    // Timeout banner — Close: leave the conflict window WITHOUT discarding the
    // sheet/CSV/aliases or the resolved/ignored keys (non-destructive close). Any
    // not-yet-saved staged toggles are simply dropped, like clicking away.
    private void ConflictCloseAfterTimeout_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            DxConflictAiTimeout.Visibility = Visibility.Collapsed;
            ConflictOverlay.Visibility = Visibility.Collapsed;
            _conflictSnapshot = null;
            _conflictIgnoredSnapshot = null;
            _fixFromConflicts = false;
            var p = CurrentResearchProject();
            if (p is null) return;
            p.IgnoredConflictKeys = _ignoredConflictKeys.ToList();
            var report = ValidateExtractionSheet(p);
            p.ExtractionValidationReport = report;
            UpdateExtractionStatus(p);
            PersistExtraction(p);
            RenderValidation(report);
        }
        catch (Exception ex) { HandleDxError("close the conflict window", ex); }
    }

    // ---- Review AI Conflict Cleanup: grouped triage, per-proposal review ------

    // The sample-incomplete blocker (and any future hard blockers) can never be
    // ignored or marked resolved by AI — more real data is required.
    private static bool IsNonIgnorableKey(string key)
        => key == SampleIncompleteKey || (key ?? "").StartsWith("SampleIncomplete|", StringComparison.Ordinal);

    private System.Windows.Data.CollectionViewSource? _fixProposalsView;

    // Groups the review list into Safe fixes / Safe to ignore / Needs manual
    // review / No safe fix, ordered by CategoryOrder. The grouped view sits over
    // the same ObservableCollection, so Remove/edit still work directly.
    private void EnsureFixProposalsGrouped()
    {
        if (_fixProposalsView is null)
        {
            _fixProposalsView = new System.Windows.Data.CollectionViewSource { Source = _fixProposals, IsLiveGroupingRequested = true };
            _fixProposalsView.SortDescriptions.Add(new System.ComponentModel.SortDescription("CategoryOrder", System.ComponentModel.ListSortDirection.Ascending));
            _fixProposalsView.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription("CategoryDisplay"));
        }
        if (!ReferenceEquals(DxFixProposalsList.ItemsSource, _fixProposalsView.View))
            DxFixProposalsList.ItemsSource = _fixProposalsView.View;
    }

    private void ConflictFixRemove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ConflictFixProposal f)
            _fixProposals.Remove(f);
    }

    private void ConflictFixAcceptHighSafe_Click(object sender, RoutedEventArgs e)
    {
        int n = 0;
        foreach (var f in _fixProposals.Where(f => f.EffectiveCategory == "safe_fix" && f.IsApplicable && f.Confidence == "high"))
        {
            if (!f.Accepted) n++;
            f.Accepted = true;
        }
        ShowToast(n == 0 ? "No further high-confidence safe fixes to accept." : $"Selected {n} high-confidence safe fix{(n == 1 ? "" : "es")}.");
    }

    private void ConflictFixAcceptIgnores_Click(object sender, RoutedEventArgs e)
    {
        int n = 0;
        foreach (var f in _fixProposals.Where(f => f.EffectiveCategory == "safe_ignore" && f.IsApplicable))
        {
            if (!f.Accepted) n++;
            f.Accepted = true;
        }
        ShowToast(n == 0 ? "No safe-to-ignore items to select." : $"Selected {n} routine warning{(n == 1 ? "" : "s")} to ignore.");
    }

    private void ExtractionFixCancel_Click(object sender, RoutedEventArgs e)
    {
        // Dismiss the AI review only, applying nothing. The conflict modal is
        // still open underneath — return to it with nothing changed.
        ExtractionFixReviewOverlay.Visibility = Visibility.Collapsed;
        _fixProposals.Clear();
        ShowToast("No fixes were applied. Your content was kept.");
    }

    // Apply selected → show the cleanup summary modal first (nothing is applied
    // until the student confirms). The summary describes the AI's whole triage.
    private void ExtractionFixApply_Click(object sender, RoutedEventArgs e)
    {
        int selected = _fixProposals.Count(f => f.Accepted && f.IsApplicable);
        if (selected == 0) { ShowToast("Tick at least one item to apply, or Cancel."); return; }

        int safeFixes = _fixProposals.Count(f => f.EffectiveCategory == "safe_fix");
        int safeIgnores = _fixProposals.Count(f => f.EffectiveCategory == "safe_ignore");
        int needReview = _fixProposals.Count(f => f.EffectiveCategory is "manual_review" or "no_safe_fix");
        int total = _fixProposals.Count;

        AiCleanupSummaryText.Text = $"Research AI reviewed {total} conflict{(total == 1 ? "" : "s")}. "
            + $"It found {safeFixes} safe fix{(safeFixes == 1 ? "" : "es")}, {safeIgnores} routine warning{(safeIgnores == 1 ? "" : "s")} safe to ignore, "
            + $"and {needReview} item{(needReview == 1 ? "" : "s")} that still need your review. "
            + $"You have {selected} item{(selected == 1 ? "" : "s")} selected to apply now.";
        AiCleanupSummaryOverlay.Visibility = Visibility.Visible;
        FadeIn(AiCleanupSummaryOverlay, 150);
    }

    // Summary modal — Go Back: return to the review screen unchanged.
    private void AiCleanupGoBack_Click(object sender, RoutedEventArgs e)
        => AiCleanupSummaryOverlay.Visibility = Visibility.Collapsed;

    // Summary modal — Cancel: close both the summary and the review, apply nothing.
    private void AiCleanupCancel_Click(object sender, RoutedEventArgs e)
    {
        AiCleanupSummaryOverlay.Visibility = Visibility.Collapsed;
        ExtractionFixReviewOverlay.Visibility = Visibility.Collapsed;
        _fixProposals.Clear();
        ShowToast("No fixes were applied. Your content was kept.");
    }

    // Summary modal — Confirm and Apply: applies ONLY the accepted proposals, each
    // mapped to the same safe local mutations the manual actions use. Then persists
    // mappings/ignored keys, reruns LOCAL validation, refreshes the conflict list
    // (the modal stays open), and shows a summary.
    private void AiCleanupConfirm_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            AiCleanupSummaryOverlay.Visibility = Visibility.Collapsed;
            var p = CurrentResearchProject();
            if (p is null) { ExtractionFixReviewOverlay.Visibility = Visibility.Collapsed; return; }

            var accepted = _fixProposals.Where(f => f.Accepted && f.IsApplicable).ToList();
            if (accepted.Count == 0) { ShowToast("Tick at least one fix to apply, or Cancel."); return; }

            PushDxUndo();   // one undo step for the whole applied batch

            ResearchVariable? FindVar(string name) =>
                string.IsNullOrWhiteSpace(name) ? null
                : _extractionVariables.FirstOrDefault(v => string.Equals(v.VariableName.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));

            int applied = 0, ignored = 0;
            foreach (var f in accepted)
            {
                switch (f.Action)
                {
                    case "add_variable":
                    {
                        string name = SanitizeVarName(FirstNonEmpty(f.ProposedValue, f.TargetColumn));
                        if (name.Length == 0) break;
                        var col = p.CsvSampleSummary?.Columns.FirstOrDefault(c => string.Equals(c.Name.Trim(), f.TargetColumn.Trim(), StringComparison.OrdinalIgnoreCase));
                        var nv = new ResearchVariable
                        {
                            VariableName = MakeUniqueName(name),
                            QuestionLabel = f.TargetColumn,
                            VariableType = col is null ? "Unknown" : MapCsvType(col.InferredType),
                            MeasurementLevel = col?.IsLikelyCategorical == true ? "Nominal" : (col?.InferredType == "Numeric" ? "Scale" : "NotApplicable"),
                            Role = "Unknown",
                            Source = string.IsNullOrWhiteSpace(f.TargetColumn) ? "AI Recommendation" : "CSV Sample"
                        };
                        if (!string.IsNullOrWhiteSpace(f.TargetColumn)) nv.SourceColumnAliases.Add(f.TargetColumn);
                        _extractionVariables.Add(nv);
                        applied++;
                        break;
                    }
                    case "map_csv_column_to_variable":
                    case "add_alias":
                    {
                        var mv = FindVar(f.TargetVariable);
                        if (mv is null) break;
                        string alias = FirstNonEmpty(f.ProposedValue, f.TargetColumn);
                        if (alias.Length == 0) break;
                        if (!mv.SourceColumnAliases.Any(a => NormalizeLabel(a) == NormalizeLabel(alias)))
                            mv.SourceColumnAliases.Add(alias);
                        applied++;
                        break;
                    }
                    case "rename_variable":
                    {
                        var rv = FindVar(f.TargetVariable);
                        string newName = SanitizeVarName(f.ProposedValue);
                        if (rv is null || newName.Length == 0) break;
                        bool taken = _extractionVariables.Any(v => !ReferenceEquals(v, rv) && string.Equals(v.VariableName.Trim(), newName, StringComparison.OrdinalIgnoreCase));
                        rv.VariableName = taken ? MakeUniqueName(newName) : newName;
                        applied++;
                        break;
                    }
                    case "update_coding":
                    {
                        var cv = FindVar(f.TargetVariable);
                        if (cv is null || string.IsNullOrWhiteSpace(f.ProposedValue)) break;
                        cv.Coding = f.ProposedValue.Trim();
                        applied++;
                        break;
                    }
                    case "mark_resolved":
                    case "ignore":
                        // Blockers (incomplete sample) can never be ignored away.
                        if (IsNonIgnorableKey(f.ConflictKey)) break;
                        _ignoredConflictKeys.Add(f.ConflictKey);
                        applied++;
                        ignored++;
                        break;
                }
            }

            ExtractionFixReviewOverlay.Visibility = Visibility.Collapsed;
            _fixProposals.Clear();
            DxGrid.Items.Refresh();

            // Persist mappings/aliases/ignored keys, rerun LOCAL validation, and
            // refresh the still-open conflict modal with what actually remains.
            p.IgnoredConflictKeys = _ignoredConflictKeys.ToList();
            CommitGrid(p);
            var report = ValidateExtractionSheet(p);
            p.ExtractionValidationReport = report;
            UpdateExtractionStatus(p);
            PersistExtraction(p);
            RenderValidation(report);
            RefreshConflicts(p);
            UpdateConflictCountText();

            // Applied fixes were explicitly confirmed, so they must survive a later
            // Cancel of the conflict window — rebase the session snapshots on the
            // new state (Cancel now only discards changes made AFTER this apply).
            if (ConflictOverlay.Visibility == Visibility.Visible)
            {
                _conflictSnapshot = SnapshotSheet();
                _conflictIgnoredSnapshot = new HashSet<string>(_ignoredConflictKeys);
            }

            int remaining = _conflicts.Count;
            int fixesApplied = applied - ignored;
            ShowToast("Research AI cleaned up the conflict list. Routine issues were handled, and only items that may need your judgment are still shown. "
                + $"{fixesApplied} fix{(fixesApplied == 1 ? "" : "es")} applied, {ignored} warning{(ignored == 1 ? "" : "s")} marked safe to ignore, "
                + $"{remaining} item{(remaining == 1 ? "" : "s")} remaining for manual review.");
        }
        catch (Exception ex) { HandleDxError("apply the AI fixes", ex); }
    }

    // =====================================================================
    // Research Lab (Phase 4A) — Statistics: readiness dashboard, deterministic
    // descriptive analysis, professional output tables, Results tab.
    //
    // EVERY number shown here comes from DescriptiveStatisticsEngine — pure,
    // deterministic C# in ResearchLabStatistics.cs. There are NO AI calls in
    // this region, no raw CSV rows are ever sent anywhere, and no inferential
    // statistics exist in Phase 4A.
    // =====================================================================

    private string ResearchDatasetDir => Path.Combine(dataDir, "research_data");
    private string ResearchDatasetPath(string projectId) => Path.Combine(ResearchDatasetDir, projectId + ".csv");

    private StatisticsReadinessResult? _statReadiness;
    private List<StatisticsOutputTable> _statTables = new();
    private StatisticsDataset? _statDatasetCache;
    private string _statDatasetCacheKey = "";
    private bool _statUiReady;   // guards option handlers while controls initialize

    // Phase 4B Part 1 — Recommended Analysis. The last data/match are cached from
    // the Statistics refresh so the recommendation view + Refresh button reuse
    // them without recomputing. _recoInDescriptive tracks the internal switch.
    private StatisticsDataset? _statData;
    private StatisticsMatchInput _statMatch = new();
    private bool _recoShowingDescriptive = true;
    private TestRecommendationResult? _recoResult;

    // Phase 4B/4C — Computed Results workflow. Newest first. Each row is a flat,
    // aggregate-only display model — NOT a live IInferenceExportable — so a
    // freshly-computed row and one reloaded from the project file (Phase 4C
    // persistence) use exactly the same display/Copy/Export code path.
    private readonly System.Collections.ObjectModel.ObservableCollection<ComputedResultRow> _computedRows = new();
    private string _computedResultsProjectId = "";
    private bool _recoRunning;
    private ComputedResultRow? _computedDetailsRow;

    // Aggregate-only view model for one computed-analysis card. No participant
    // rows are ever stored here — only display strings, already rendered by the
    // engine at compute time. Mirrors SavedComputedResult field-for-field so
    // ToSaved/FromSaved (Phase 4C) are simple 1:1 mappings.
    public sealed class ComputedResultRow
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string TestName { get; set; } = "";
        public string Variables { get; set; } = "";
        public string OutcomeName { get; set; } = "";
        public string PredictorName { get; set; } = "";
        public string ValidNDisplay { get; set; } = "";
        public string PValueDisplay { get; set; } = "";
        public string EffectDisplay { get; set; } = "";
        public DateTime ComputedAt { get; set; } = DateTime.UtcNow;
        public string ComputedTimeDisplay { get; set; } = "";
        public string SignificanceText { get; set; } = "";
        public string SignificanceKind { get; set; } = "Muted";   // "Good" | "Muted"
        public string FullPlainText { get; set; } = "";
        public string CsvText { get; set; } = "";
        public string AnalysisFingerprint { get; set; } = "";
        public bool HasPValue { get; set; }
        public bool IsSignificant { get; set; }

        // Phase 4F Slices 3-4 — SESSION-ONLY live typed result, kept so the
        // narrative generator can produce copy/export text from structured values
        // (never by parsing FullPlainText/CsvText). Set once at compute time in
        // BuildComputedRow; NULL for rows reloaded from the saved project file.
        // Never persisted: ToSavedComputedResult/FromSavedComputedResult do not
        // map it and SavedComputedResult has no such field. JsonIgnore is
        // defensive hygiene (this row is never itself serialized). Holds only the
        // aggregate result object — no participant rows exist on it to leak.
        [System.Text.Json.Serialization.JsonIgnore]
        public IInferenceExportable? LiveResult { get; set; }

        // Persisted deterministic manuscript narrative (Phase: narrative
        // persistence). Generated at compute time from LiveResult and carried
        // through ToSaved/FromSaved so Report Builder can show Methods/Results
        // after a restart without a Re-run. Null for older saved results.
        public SavedNarrative? Narrative { get; set; }

        // Phase 4C Slice 3 — set once per Computed Results render by comparing
        // AnalysisFingerprint against the CURRENT project fingerprint (computed
        // once per render, not per card). Never recomputed by a getter, so
        // binding to it is cheap and stable between renders.
        public bool IsStale { get; set; }
        public string StaleText { get; set; } = "";
        public Visibility StaleVisibility => IsStale ? Visibility.Visible : Visibility.Collapsed;
    }

    // Deterministic Magic Fix repair suggestions, recomputed on every Statistics
    // refresh. _magicFixReview is the working copy shown in the review modal.
    private List<MagicFixProposal> _magicFixProposals = new();
    private readonly System.Collections.ObjectModel.ObservableCollection<MagicFixProposal> _magicFixReview = new();
    private System.Windows.Data.CollectionViewSource? _magicFixView;

    // Row display model for the Variable Matching Summary list.
    public sealed class StatMatchDisplayRow
    {
        public string Group { get; set; } = "";
        public int GroupOrder { get; set; }
        public string Name { get; set; } = "";
        public string Label { get; set; } = "";
        public string TypeDisplay { get; set; } = "";
        public string Role { get; set; } = "";
        public string ColumnDisplay { get; set; } = "";
        public string CountsDisplay { get; set; } = "";
        public string StatusText { get; set; } = "";
        public string StatusKind { get; set; } = "Muted";
    }

    // Keeps a per-project copy of the uploaded CSV so the LOCAL statistics
    // engine has the full raw values. The copy stays on this device in the app
    // data folder and is never sent to any AI or network service.
    private bool TryStoreDatasetCopy(string sourcePath, ResearchProject p)
    {
        try
        {
            Directory.CreateDirectory(ResearchDatasetDir);
            File.Copy(sourcePath, ResearchDatasetPath(p.Id), overwrite: true);
            _statDatasetCacheKey = "";
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void DeleteDatasetCopy(string projectId)
    {
        try
        {
            string f = ResearchDatasetPath(projectId);
            if (File.Exists(f)) File.Delete(f);
        }
        catch { /* best effort — readiness will simply ask for a re-upload */ }
        _statDatasetCacheKey = "";
    }

    // Parses the stored dataset copy (cached per file version). Returns null
    // when no copy exists or it cannot be read; 'error' explains why.
    private StatisticsDataset? LoadStatisticsDataset(ResearchProject p, out string error)
    {
        error = "";
        string path = ResearchDatasetPath(p.Id);
        if (!File.Exists(path))
        {
            // Older projects have only the privacy-safe CsvSampleSummary and no
            // full local dataset copy — statistics need the complete CSV.
            if (p.CsvSampleSummary is not null)
                error = "Full CSV data is required to run statistics. Please upload the complete CSV file.";
            return null;
        }

        string key;
        try { var fi = new FileInfo(path); key = p.Id + "|" + fi.LastWriteTimeUtc.Ticks + "|" + fi.Length; }
        catch { key = p.Id; }
        if (_statDatasetCache is not null && _statDatasetCacheKey == key) return _statDatasetCache;

        if (!StatisticsCsvReader.TryReadFile(path, out var ds, out error)) return null;
        // Show the original upload name when we have it (the stored copy is named by project id).
        if (p.CsvSampleSummary is { FileName.Length: > 0 } s) ds.FileName = s.FileName;
        _statDatasetCache = ds;
        _statDatasetCacheKey = key;
        return ds;
    }

    // Bridges the Phase 3 matching engine (names, labels, aliases) into the
    // statistics layer, so Statistics and Data Extraction always agree. Matching
    // is done against the ACTUAL dataset headers (the data that will be analyzed)
    // when a full dataset is loaded, falling back to the privacy-safe summary
    // only when no dataset copy is present.
    private StatisticsMatchInput BuildStatisticsMatchInput(ResearchProject p, StatisticsDataset? data)
    {
        var input = new StatisticsMatchInput();

        CsvSampleSummary csv;
        if (data is { ColumnCount: > 0 })
            csv = new CsvSampleSummary { Columns = data.ColumnNames.Select(n => new CsvColumnSummary { Name = n }).ToList() };
        else if (p.CsvSampleSummary is { Columns.Count: > 0 } s)
            csv = s;
        else return input;

        var match = MatchCsvToSheet(p.Variables ?? new List<ResearchVariable>(), csv);
        foreach (var kv in match.ColumnMatch)
            if (!input.VariableColumn.ContainsKey(kv.Value.Id))
                input.VariableColumn[kv.Value.Id] = kv.Key.Name;
        input.MetadataColumns = match.Metadata.Select(c => c.Name).ToList();
        input.CsvOnlyColumns = match.CsvOnly.Select(c => c.Name).ToList();
        return input;
    }

    private string CurrentStatisticsFingerprint(ResearchProject p)
        => StatisticsFingerprint.Compute(p.Variables, ResearchDatasetPath(p.Id), p.TargetSampleSize);

    private bool IsStatisticsStale(ResearchProject p)
        => p.DescriptiveStatistics is { } rec
           && !string.Equals(rec.SourceFingerprint, CurrentStatisticsFingerprint(p), StringComparison.Ordinal);

    // ---- Statistics tab refresh (runs on every tab open) --------------------

    private void EnsureStatisticsOptionsInit()
    {
        if (_statUiReady) return;
        StDecimalsBox.ItemsSource = new[] { "0", "1", "2", "3" };
        StDecimalsBox.SelectedIndex = 2;
        StStyleBox.ItemsSource = new[] { "Academic Clean", "SPSS-like Academic", "Compact Report", "Thesis Detailed" };
        StStyleBox.SelectedIndex = 0;
        if (StScoreBar.Parent is FrameworkElement track)
            track.SizeChanged += (_, _) => UpdateStatScoreBar();
        _statUiReady = true;
    }

    private void RefreshStatisticsTab()
    {
        try
        {
            EnsureStatisticsOptionsInit();
            var p = CurrentResearchProject();
            if (p is null) return;

            // Commit any in-flight sheet edits so readiness sees current data.
            TryCommitGridEdit();
            CommitGrid(p);

            var sheetReport = ValidateExtractionSheet(p);   // local, no AI
            var data = LoadStatisticsDataset(p, out string dataError);
            var match = BuildStatisticsMatchInput(p, data);

            _statReadiness = StatisticsReadinessService.Evaluate(
                p.Variables ?? new List<ResearchVariable>(), data, match, p.TargetSampleSize, sheetReport.Errors);

            // Old projects have a CSV summary but no stored copy on this device —
            // replace the generic "no dataset" wording with the exact fix.
            if (dataError.Length > 0)
            {
                var nd = _statReadiness.Issues.FirstOrDefault(i => i.Code is "NoDataset" or "EmptyDataset");
                if (nd is not null) nd.Message = dataError;
            }

            // Deterministic local repair suggestions for type/coding mismatches.
            _magicFixProposals = MagicFixService.BuildProposals(p.Variables ?? new List<ResearchVariable>(), data, match);

            RenderStatisticsReadiness();
            RenderDatasetOverview(p, data, match);
            RenderMatchingSummary(p, data, match);

            StOptionsCard.Visibility = Visibility.Visible;
            bool stale = p.DescriptiveStatistics is not null && IsStatisticsStale(p);
            StStaleBanner.Visibility = stale ? Visibility.Visible : Visibility.Collapsed;

            if (p.DescriptiveStatistics is { } rec) RenderStatisticsOutput(rec);
            else { StOutputPanel.Visibility = Visibility.Collapsed; _statTables.Clear(); }

            // Cache for Recommended Analysis (Phase 4B) and refresh that section.
            _statData = data;
            _statMatch = match;
            RefreshRecommendedAnalysis(p);
        }
        catch (Exception ex) { HandleDxError("refresh the statistics dashboard", ex); }
    }

    // ---- Phase 4B Part 1: Recommended Analysis (deterministic test planning) ----

    private void StShowDescriptive_Click(object sender, RoutedEventArgs e) => ShowStatView("descriptive");
    private void StShowRecommended_Click(object sender, RoutedEventArgs e) => ShowStatView("recommended");
    private void StShowComputed_Click(object sender, RoutedEventArgs e) => ShowStatView("computed");

    // Backward-compatible wrapper for existing callers.
    private void SetRecommendedView(bool showRecommended) => ShowStatView(showRecommended ? "recommended" : "descriptive");

    // Three-way internal Statistics switch: Descriptive | Recommended | Computed.
    private void ShowStatView(string which)
    {
        _recoShowingDescriptive = which == "descriptive";
        var active = (Style)FindResource("SegTabActive");
        var normal = (Style)FindResource("SegTab");
        StSegDescriptive.Style = which == "descriptive" ? active : normal;
        StSegRecommended.Style = which == "recommended" ? active : normal;
        StSegComputed.Style = which == "computed" ? active : normal;
        StDescriptiveView.Visibility = which == "descriptive" ? Visibility.Visible : Visibility.Collapsed;
        StRecommendedView.Visibility = which == "recommended" ? Visibility.Visible : Visibility.Collapsed;
        StComputedView.Visibility = which == "computed" ? Visibility.Visible : Visibility.Collapsed;
        if (which == "computed") RenderComputedResults();
    }

    // Evaluates the gate and (when unlocked) builds the deterministic
    // recommendations. Called on every Statistics refresh so the locked/unlocked
    // state always reflects current Phase 4A readiness.
    private void RefreshRecommendedAnalysis(ResearchProject p)
    {
        try
        {
            // Computed Results are project-scoped: on first load of a project (or
            // switching to a different one), restore its saved results from the
            // project file (Phase 4C persistence) instead of leaving the previous
            // project's rows on screen. A same-project refresh is a no-op here —
            // every mutation already persists immediately, so there is nothing
            // "unsaved" to reload mid-session.
            if (_computedResultsProjectId != p.Id)
            {
                _computedRows.Clear();
                foreach (var saved in p.ComputedResults ?? new List<SavedComputedResult>())
                    _computedRows.Add(FromSavedComputedResult(saved));
                _computedResultsProjectId = p.Id;
                if (StComputedDetailsCard is not null) StComputedDetailsCard.Visibility = Visibility.Collapsed;
                RenderComputedResults();
            }

            var vars = p.Variables ?? new List<ResearchVariable>();
            var gate = TestRecommendationEngine.EvaluateGate(vars, _statData, _statMatch, _statReadiness);

            StRecoChecklist.ItemsSource = null;
            StRecoChecklist.ItemsSource = gate.Checklist;
            StRecoLockedReason.Text = gate.PrimaryReason;

            StRecoLockedCard.Visibility = gate.Locked ? Visibility.Visible : Visibility.Collapsed;
            StRecoUnlocked.Visibility = gate.Locked ? Visibility.Collapsed : Visibility.Visible;

            if (gate.Locked)
            {
                _recoResult = null;
                return;
            }

            StRecoWarnBanner.Visibility = gate.HasWarnings ? Visibility.Visible : Visibility.Collapsed;
            _recoResult = TestRecommendationEngine.Build(vars, _statData, _statMatch, _statReadiness);
            RenderTestRecommendations(_recoResult);
        }
        catch (Exception ex) { HandleDxError("refresh recommended analysis", ex); }
    }

    private void RenderTestRecommendations(TestRecommendationResult r)
    {
        StRecoOutcomeText.Text = $"Outcome variable: {r.OutcomeDisplay}";
        StRecoSummaryCards.ItemsSource = new List<StatisticsReadinessCard>
        {
            new() { Title = "Outcome variable", Value = r.OutcomeName.Length > 0 ? r.OutcomeName : "—" },
            new() { Title = "Candidate variables", Value = r.CandidatePredictors.ToString() },
            new() { Title = "Ready to plan", Value = r.ReadyCount.ToString(), Kind = r.ReadyCount > 0 ? "Good" : "Muted" },
            new() { Title = "Needs assumption review", Value = r.AssumptionReviewCount.ToString(), Kind = r.AssumptionReviewCount > 0 ? "Warn" : "Muted" },
            new() { Title = "Needs role review", Value = r.RoleReviewCount.ToString(), Kind = r.RoleReviewCount > 0 ? "Warn" : "Muted" },
            new() { Title = "Unsupported", Value = r.UnsupportedCount.ToString(), Kind = r.UnsupportedCount > 0 ? "Bad" : "Muted" }
        };

        // Group the cards by status so the (potentially long) list reads as
        // clearly-sorted sections instead of a flat all-ready wall.
        var view = new System.Windows.Data.CollectionViewSource { Source = r.Recommendations };
        view.SortDescriptions.Add(new System.ComponentModel.SortDescription("GroupOrder", System.ComponentModel.ListSortDirection.Ascending));
        view.SortDescriptions.Add(new System.ComponentModel.SortDescription("PredictorName", System.ComponentModel.ListSortDirection.Ascending));
        view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription("GroupDisplay"));
        StRecoList.ItemsSource = view.View;

        bool empty = r.Recommendations.Count == 0;
        StRecoEmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        StRecoEmptyText.Text = empty
            ? "No candidate predictors were found to pair with the outcome. Add or match more variables in Data Extraction to plan comparisons."
            : "";
    }

    // Recommendation actions never run tests — they only refresh/copy/export the
    // deterministic plan. Guarded so they no-op when the section is locked.
    private bool RecoUnlockedGuard()
    {
        if (_recoResult is null || StRecoUnlocked.Visibility != Visibility.Visible)
        {
            ShowToast("Complete Descriptive Statistics readiness first to use Recommended Analysis.");
            return false;
        }
        return true;
    }

    private void StRecoRefresh_Click(object sender, RoutedEventArgs e)
    {
        RefreshStatisticsTab();
        if (StRecoUnlocked.Visibility == Visibility.Visible)
            ShowToast("Recommendations refreshed from your current extraction sheet and dataset.");
    }

    private void StRecoCopy_Click(object sender, RoutedEventArgs e)
    {
        if (!RecoUnlockedGuard()) return;
        try
        {
            Clipboard.SetText(TestRecommendationExport.BuildPlainText(_recoResult!));
            ShowToast("Recommended analysis plan copied to the clipboard.");
        }
        catch { ShowToast("The recommendations could not be copied. Please try again."); }
    }

    private void StRecoExportTxt_Click(object sender, RoutedEventArgs e)
    {
        if (!RecoUnlockedGuard()) return;
        SaveStatExport("recommended_analysis_plan.txt", "Text files (*.txt)|*.txt",
            TestRecommendationExport.BuildPlainText(_recoResult!), "Recommended analysis plan exported");
    }

    private void StRecoExportCsv_Click(object sender, RoutedEventArgs e)
    {
        if (!RecoUnlockedGuard()) return;
        SaveStatExport("recommended_analysis_plan.csv", "CSV files (*.csv)|*.csv",
            TestRecommendationExport.BuildCsv(_recoResult!), "Recommended analysis plan exported");
    }

    // ---- Phase 4B Part 2: run a recommended test → Computed Results ----------
    // This is the only place a test is actually computed. It executes ONLY the
    // categorical or rank test that Part 1 recommended for an eligible pairing —
    // each engine re-checks eligibility, so no arbitrary test can be run here.
    private async void StRecoRunInference_Click(object sender, RoutedEventArgs e)
    {
        // Phase 7: running an analysis is a protected action (active license required).
        if (!LicenseAllowsProtectedAction(out string gateMessage)) { ShowToast(gateMessage); return; }
        if (_recoRunning) return;
        if (!RecoUnlockedGuard()) return;
        if ((sender as FrameworkElement)?.DataContext is not TestRecommendation rec) return;
        if (!IsRunnableReco(rec))
        {
            ShowToast("This card cannot be computed. Only categorical, rank, or correlation comparisons that are ready or need assumption review can be run.");
            return;
        }

        var p = CurrentResearchProject();
        if (p is null) return;
        if (_statData is null)
        {
            ShowToast("Upload the complete CSV dataset to compute this analysis.");
            return;
        }

        var vars = p.Variables ?? new List<ResearchVariable>();
        var outcome = vars.FirstOrDefault(v => (v.VariableName ?? "").Trim() == rec.OutcomeName);
        var predictor = vars.FirstOrDefault(v => (v.VariableName ?? "").Trim() == rec.PredictorName);
        if (outcome is null || predictor is null)
        {
            ShowToast("The variables for this comparison could not be found in the extraction sheet.");
            return;
        }

        _recoRunning = true;
        try
        {
            // Switch to Computed Results and show a lightweight, non-blocking
            // loading state (async delays — never Thread.Sleep, never freezes UI).
            ShowStatView("computed");
            StComputedDetailsCard.Visibility = Visibility.Collapsed;
            StComputedEmpty.Visibility = Visibility.Collapsed;   // hide empty-state behind the loader
            StRunLoadingCard.Visibility = Visibility.Visible;

            await StepRunLoading("Preparing variables…");
            await StepRunLoading("Checking assumptions…");
            await StepRunLoading("Computing locally…");
            IInferenceExportable result = DispatchCompute(rec, outcome, predictor);
            await StepRunLoading("Building result summary…");

            AddOrReplaceComputedRow(BuildComputedRow(rec, result, p));
            _computedResultsProjectId = p.Id;
            PersistComputedResults(p);

            StRunLoadingCard.Visibility = Visibility.Collapsed;
            RenderComputedResults();
            ShowToast("Analysis complete. Result is available in Computed Results.");
        }
        catch (Exception ex)
        {
            StRunLoadingCard.Visibility = Visibility.Collapsed;
            HandleDxError("run the analysis", ex);
        }
        finally { _recoRunning = false; }
    }

    private async System.Threading.Tasks.Task StepRunLoading(string message)
    {
        StRunLoadingStep.Text = message;
        await System.Threading.Tasks.Task.Delay(600);
    }

    // A card is runnable when exactly one of the three (mutually-exclusive)
    // compute gates is open. Role-review / unsupported / not-recommended cards
    // never qualify.
    private static bool IsRunnableReco(TestRecommendation rec)
        => rec.CanComputeCategorical || rec.CanComputeRank || rec.CanComputeSpearman || rec.CanComputeWelch || rec.CanComputeAnova || rec.CanComputePearson || rec.CanCompute2x2Measures;

    // Dispatches to the engine matching the recommended test. Precondition:
    // IsRunnableReco(rec) is true and _statData is loaded. Categorical is
    // mutually exclusive by kind. Welch (continuous outcome × binary group)
    // and ANOVA (continuous outcome × categorical 3+-group predictor) are each a
    // SUBSET of Rank, so both are checked BEFORE Rank: for those pairings the
    // headline recommendation is the parametric test (Welch / one-way ANOVA), and
    // the rank test (Mann-Whitney U / Kruskal-Wallis) is only its named robust
    // alternative. Welch and ANOVA are mutually exclusive (binary vs 3+ groups).
    // Pearson (continuous × continuous) is a SUBSET of Spearman, so it is checked
    // BEFORE the Spearman fallback: Pearson is the headline parametric correlation
    // and Spearman is only its named robust nonparametric alternative. Every other
    // rank pairing still falls through to Rank; ordinal-involving correlations
    // still fall through to Spearman.
    private IInferenceExportable DispatchCompute(TestRecommendation rec, ResearchVariable outcome, ResearchVariable predictor)
        => rec.CanCompute2x2Measures ? TwoByTwoMeasuresEngine.Compute(rec, outcome, predictor, _statData!, _statMatch, CurrentResearchProject()?.StudyType ?? "")
         : rec.CanComputeCategorical ? CategoricalInferenceEngine.Compute(rec, outcome, predictor, _statData!, _statMatch)
         : rec.CanComputeWelch ? ParametricInferenceEngine.ComputeWelchTTest(rec, outcome, predictor, _statData!, _statMatch)
         : rec.CanComputeAnova ? ParametricInferenceEngine.ComputeOneWayAnova(rec, outcome, predictor, _statData!, _statMatch)
         : rec.CanComputeRank ? RankInferenceEngine.Compute(rec, outcome, predictor, _statData!, _statMatch)
         : rec.CanComputePearson ? ParametricInferenceEngine.ComputePearsonCorrelation(rec, outcome, predictor, _statData!, _statMatch)
         : SpearmanCorrelationEngine.Compute(rec, outcome, predictor, _statData!, _statMatch);

    // Builds the aggregate-only display row for a computed result. No math here —
    // it only reads already-computed values off the result object and renders its
    // ToPlainText()/ToCsv() ONCE, so the row never needs the live object again.
    private ComputedResultRow BuildComputedRow(TestRecommendation rec, IInferenceExportable result, ResearchProject p)
    {
        var row = new ComputedResultRow
        {
            Variables = result.ResultTitle,
            OutcomeName = rec.OutcomeName,
            PredictorName = rec.PredictorName,
            ComputedAt = DateTime.UtcNow,
            ComputedTimeDisplay = DateTime.Now.ToString("MMM d, yyyy · h:mm tt", System.Globalization.CultureInfo.InvariantCulture),
            FullPlainText = result.ToPlainText(),
            CsvText = result.ToCsv(),
            AnalysisFingerprint = CurrentStatisticsFingerprint(p),
            // Session-only — lets the narrative generator work from structured
            // values. Never persisted (see ComputedResultRow.LiveResult).
            LiveResult = result
        };

        double? pv = null;
        switch (result)
        {
            case CategoricalTestResult c:
                row.TestName = c.TestUsed;
                row.ValidNDisplay = $"N = {c.ValidPairs}";
                row.PValueDisplay = c.PValue is null ? "p: not calculated" : $"p = {InferenceMath.FormatPValue(c.PValue.Value)}";
                row.EffectDisplay = c.CramersV is { } cv
                    ? $"Cramér's V = {InferenceMath.FormatNumber(cv, 3)}" + (c.Phi is { } ph ? $"  ·  φ = {InferenceMath.FormatNumber(ph, 3)}" : "")
                    : "Effect: —";
                pv = c.PValue;
                break;
            case RankTestResult r:
                row.TestName = r.TestUsed;
                row.ValidNDisplay = $"N = {r.ValidN}";
                row.PValueDisplay = r.PValue is null ? "p: not calculated" : $"p = {InferenceMath.FormatPValue(r.PValue.Value)}";
                row.EffectDisplay = r.EffectValue is { } ev ? $"{r.EffectName} = {InferenceMath.FormatNumber(ev, 3)}" : "Effect: —";
                pv = r.PValue;
                break;
            case SpearmanResult s:
                row.TestName = s.TestUsed;
                row.ValidNDisplay = $"N = {s.PairN}";
                row.PValueDisplay = s.PValue is null ? "p: not calculated" : $"p = {InferenceMath.FormatPValue(s.PValue.Value)}";
                row.EffectDisplay = s.Rho is null ? "Effect: —" : $"ρ = {InferenceMath.FormatNumber(s.Rho, 3)}";
                pv = s.PValue;
                break;
            case WelchTTestResult w:
                row.TestName = w.TestUsed;
                row.ValidNDisplay = $"N = {w.ValidN}";
                row.PValueDisplay = w.PValue is null ? "p: not calculated" : $"p = {InferenceMath.FormatPValue(w.PValue.Value)}";
                row.EffectDisplay = w.HedgesG is { } hg ? $"Hedges g = {InferenceMath.FormatNumber(hg, 3)}" : "Effect: —";
                pv = w.PValue;
                break;
            case OneWayAnovaResult a:
                row.TestName = a.TestUsed;
                row.ValidNDisplay = $"N = {a.ValidN}";
                row.PValueDisplay = a.PValue is null ? "p: not calculated" : $"p = {InferenceMath.FormatPValue(a.PValue.Value)}";
                row.EffectDisplay = a.EtaSquared is { } es ? $"η² = {InferenceMath.FormatNumber(es, 3)}" : "Effect: —";
                pv = a.PValue;
                break;
            case PearsonCorrelationResult pr:
                row.TestName = pr.TestUsed;
                row.ValidNDisplay = $"N = {pr.PairN}";
                row.PValueDisplay = pr.PValue is null ? "p: not calculated" : $"p = {InferenceMath.FormatPValue(pr.PValue.Value)}";
                row.EffectDisplay = pr.R is null ? "Effect: —" : $"r = {InferenceMath.FormatNumber(pr.R, 3)}";
                pv = pr.PValue;
                break;
            case TwoByTwoMeasuresResult m:
                row.TestName = m.TestUsed;
                row.ValidNDisplay = $"N = {m.N}";
                row.PValueDisplay = m.AssociationP is null ? "p: not calculated" : $"p = {InferenceMath.FormatPValue(m.AssociationP.Value)}";
                row.EffectDisplay = m.OddsRatio is { } orv ? $"OR = {InferenceMath.FormatNumber(orv, 3)}" : "Effect: —";
                pv = m.AssociationP;
                break;
            default:
                row.TestName = "Result";
                break;
        }

        row.HasPValue = pv is not null && result.Computed;
        if (row.HasPValue)
        {
            row.IsSignificant = pv!.Value < 0.05;
            row.SignificanceText = row.IsSignificant ? "Significant (p < .05)" : "Not significant";
            row.SignificanceKind = row.IsSignificant ? "Good" : "Muted";
        }
        else
        {
            row.SignificanceText = "Needs review";
            row.SignificanceKind = "Muted";
        }

        // Narrative persistence: capture the deterministic manuscript narrative now,
        // from the live typed result, so it survives a restart. Never fails the
        // statistic — on any error we simply store no narrative and the report keeps
        // its Re-run fallback. Aggregate-only, no AI (generator invariant).
        try
        {
            var nar = ResearchLabNarrativeGenerator.Generate(result, isStale: false);
            if (nar is not null && !(string.IsNullOrWhiteSpace(nar.MethodsText)
                                     && string.IsNullOrWhiteSpace(nar.ResultsText)
                                     && string.IsNullOrWhiteSpace(nar.NotesText)))
            {
                row.Narrative = new SavedNarrative
                {
                    Title = nar.Title,
                    MethodsText = nar.MethodsText,
                    ResultsText = nar.ResultsText,
                    NotesText = nar.NotesText,
                    GeneratedAt = DateTime.UtcNow,
                    SourceFingerprint = row.AnalysisFingerprint,
                    IsDeterministic = nar.IsDeterministic,
                    AiUsed = nar.AiUsed
                };
            }
            else row.Narrative = null;
        }
        catch { row.Narrative = null; }

        return row;
    }

    // ---- Beta fix: prevent duplicate computed-result rows --------------------
    // Running the SAME analysis again via "Run this analysis" or "Run all
    // supported analyses" must UPDATE the existing row in place instead of
    // appending a confusing identical duplicate (the row-level Re-run already
    // replaces in place; this brings the Run/Run-all paths in line). A genuinely
    // new result — a different variable pairing, a different test, or the same
    // analysis after the dataset/extraction sheet changed (new fingerprint) — is
    // still inserted newest-first. This changes nothing statistical: it only
    // decides insert-vs-replace on the display collection.
    private void AddOrReplaceComputedRow(ComputedResultRow newRow)
    {
        int idx = -1;
        for (int i = 0; i < _computedRows.Count; i++)
            if (IsSameComputedResult(_computedRows[i], newRow)) { idx = i; break; }

        if (idx >= 0)
        {
            newRow.Id = _computedRows[idx].Id;   // keep identity stable (details card / selection)
            _computedRows[idx] = newRow;          // replace in place — Total unchanged, position preserved
        }
        else
        {
            _computedRows.Insert(0, newRow);      // genuinely new — newest first
        }
    }

    // Two rows describe the SAME computed result when they share the outcome and
    // predictor variables, the test that was run, AND the analysis fingerprint
    // (dataset + extraction-sheet state). Requiring all three means distinct
    // tests for a pairing, or the same analysis over DIFFERENT data, are never
    // collapsed together — only an exact re-run of the identical analysis is
    // treated as a replacement.
    private static bool IsSameComputedResult(ComputedResultRow a, ComputedResultRow b)
        => string.Equals(a.OutcomeName, b.OutcomeName, StringComparison.Ordinal)
        && string.Equals(a.PredictorName, b.PredictorName, StringComparison.Ordinal)
        && string.Equals(a.TestName, b.TestName, StringComparison.Ordinal)
        && string.Equals(a.AnalysisFingerprint, b.AnalysisFingerprint, StringComparison.Ordinal);

    // ---- Phase 4C (Slice 1): Computed Results persistence --------------------
    // Single source of truth for saving to disk: converts the current in-memory
    // rows (already newest-first) into the flat, aggregate-only SavedComputedResult
    // shape and writes the whole project store once. Called after every mutation
    // (single Run, the Run-all batch, Clear) so the saved copy never drifts from
    // what the UI shows. No participant rows, no AI — every field here is a
    // string/bool/DateTime already produced by the engine at compute time.
    private void PersistComputedResults(ResearchProject p)
    {
        p.ComputedResults = _computedRows.Select(ToSavedComputedResult).ToList();
        SaveResearch();
    }

    private static SavedComputedResult ToSavedComputedResult(ComputedResultRow row) => new()
    {
        Id = row.Id,
        TestName = row.TestName,
        Variables = row.Variables,
        OutcomeName = row.OutcomeName,
        PredictorName = row.PredictorName,
        ValidNDisplay = row.ValidNDisplay,
        PValueDisplay = row.PValueDisplay,
        EffectDisplay = row.EffectDisplay,
        SignificanceText = row.SignificanceText,
        SignificanceKind = row.SignificanceKind,
        HasPValue = row.HasPValue,
        IsSignificant = row.IsSignificant,
        FullPlainText = row.FullPlainText,
        CsvText = row.CsvText,
        ComputedAt = row.ComputedAt,
        AnalysisFingerprint = row.AnalysisFingerprint,
        Narrative = row.Narrative
    };

    private static ComputedResultRow FromSavedComputedResult(SavedComputedResult s) => new()
    {
        Id = s.Id,
        TestName = s.TestName,
        Variables = s.Variables,
        OutcomeName = s.OutcomeName,
        PredictorName = s.PredictorName,
        ValidNDisplay = s.ValidNDisplay,
        PValueDisplay = s.PValueDisplay,
        EffectDisplay = s.EffectDisplay,
        SignificanceText = s.SignificanceText,
        SignificanceKind = s.SignificanceKind,
        HasPValue = s.HasPValue,
        IsSignificant = s.IsSignificant,
        FullPlainText = s.FullPlainText,
        CsvText = s.CsvText,
        ComputedAt = s.ComputedAt,
        ComputedTimeDisplay = s.ComputedTimeDisplay,
        AnalysisFingerprint = s.AnalysisFingerprint,
        Narrative = s.Narrative
    };

    private void RenderComputedResults()
    {
        // Phase 4C Slice 3 — stale check. Computed ONCE per render (not per
        // card): recompute the CURRENT project fingerprint via the existing
        // StatisticsFingerprint path and compare each row's saved fingerprint
        // against it. A missing/empty saved fingerprint (an edge case, not
        // expected in practice) is always treated as stale — never crashes,
        // never silently claims "fresh" when it can't verify.
        var p = CurrentResearchProject();
        string currentFingerprint = p is not null ? CurrentStatisticsFingerprint(p) : "";
        foreach (var row in _computedRows)
        {
            row.IsStale = string.IsNullOrEmpty(row.AnalysisFingerprint)
                || !string.Equals(row.AnalysisFingerprint, currentFingerprint, StringComparison.Ordinal);
            row.StaleText = row.IsStale ? "Stale — dataset or extraction sheet changed" : "";
        }

        StComputedList.ItemsSource = null;
        StComputedList.ItemsSource = _computedRows;
        bool any = _computedRows.Count > 0;
        StComputedEmpty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        StComputedSummary.Visibility = any ? Visibility.Visible : Visibility.Collapsed;

        int total = _computedRows.Count;
        int withP = _computedRows.Count(r => r.HasPValue);
        int sig = _computedRows.Count(r => r.IsSignificant);
        int stale = _computedRows.Count(r => r.IsStale);
        string last = total > 0 ? _computedRows[0].ComputedTimeDisplay : "—";

        var cards = new List<StatisticsReadinessCard>
        {
            new() { Title = "Total results", Value = total.ToString() },
            new() { Title = "Last computed", Value = last }
        };
        // Significance card is only meaningful when at least one result has a p-value.
        if (withP > 0)
            cards.Add(new StatisticsReadinessCard { Title = "Significant", Value = $"{sig} of {withP}", Kind = sig > 0 ? "Good" : "Muted" });
        // Stale count card only appears when at least one result is stale.
        if (stale > 0)
            cards.Add(new StatisticsReadinessCard { Title = "Stale results", Value = stale.ToString(), Kind = "Warn" });
        StComputedSummaryCards.ItemsSource = cards;
    }

    private void StComputedViewDetails_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ComputedResultRow row) return;
        _computedDetailsRow = row;
        StComputedDetailsTitle.Text = $"{row.TestName} — {row.Variables}";
        StComputedDetailsText.Text = row.FullPlainText;
        StComputedDetailsCard.Visibility = Visibility.Visible;
        StComputedDetailsCard.BringIntoView();
    }

    private void StComputedHideDetails_Click(object sender, RoutedEventArgs e)
    {
        StComputedDetailsCard.Visibility = Visibility.Collapsed;
        _computedDetailsRow = null;
    }

    private void StComputedDetailsCopy_Click(object sender, RoutedEventArgs e)
    {
        if (_computedDetailsRow is null) return;
        try { Clipboard.SetText(_computedDetailsRow.FullPlainText); ShowToast("Result copied to the clipboard."); }
        catch { ShowToast("The result could not be copied. Please try again."); }
    }

    // ---- Phase 4F Slices 3-4: manuscript narrative copy / TXT export ----------
    // All four actions read the SESSION-ONLY LiveResult on the open details row
    // and generate narrative from structured values (never by parsing
    // FullPlainText/CsvText). A row reloaded from the saved project file has no
    // LiveResult, so the user is asked to Re-run instead. Stale status is always
    // surfaced (banner prepended to copies; flag + Notes banner in the export).
    private const string NarrativeRerunMessage =
        "Re-run this result to generate manuscript-ready Methods and Results text.";

    // Returns the freshly-generated narrative for the open details row, or null
    // (and a toast) when there is no row or no live typed result to work from.
    private ResearchLabNarrativeResult? DetailsNarrativeOrToast()
    {
        var row = _computedDetailsRow;
        if (row is null) return null;
        if (row.LiveResult is null) { ShowToast(NarrativeRerunMessage); return null; }
        return ResearchLabNarrativeGenerator.Generate(row.LiveResult, row.IsStale);
    }

    private void CopyNarrative(string? body, string what)
    {
        try
        {
            Clipboard.SetText(string.IsNullOrWhiteSpace(body) ? " " : body);
            ShowToast($"{what} copied to the clipboard.");
        }
        catch { ShowToast("The text could not be copied. Please try again."); }
    }

    private void StComputedCopyMethods_Click(object sender, RoutedEventArgs e)
    {
        var n = DetailsNarrativeOrToast();
        if (n is null) return;
        CopyNarrative(ResearchLabNarrativeGenerator.WithStaleBanner(n.MethodsText, _computedDetailsRow!.IsStale), "Methods");
    }

    private void StComputedCopyResults_Click(object sender, RoutedEventArgs e)
    {
        var n = DetailsNarrativeOrToast();
        if (n is null) return;
        CopyNarrative(ResearchLabNarrativeGenerator.WithStaleBanner(n.ResultsText, _computedDetailsRow!.IsStale), "Results");
    }

    private void StComputedCopyMethodsResults_Click(object sender, RoutedEventArgs e)
    {
        var n = DetailsNarrativeOrToast();
        if (n is null) return;
        CopyNarrative(ResearchLabNarrativeGenerator.ComposeMethodsPlusResults(n, _computedDetailsRow!.IsStale), "Methods and Results");
    }

    private void StComputedExportNarrativeTxt_Click(object sender, RoutedEventArgs e)
    {
        var n = DetailsNarrativeOrToast();
        if (n is null) return;
        var row = _computedDetailsRow!;
        string txt = ResearchLabNarrativeGenerator.ComposeNarrativeTxt(n, row.TestName, row.ComputedTimeDisplay, row.IsStale);
        SaveStatExport("methods_and_results.txt", "Text files (*.txt)|*.txt", txt, "Narrative exported");
    }

    private void StComputedCopy_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ComputedResultRow row) return;
        try { Clipboard.SetText(row.FullPlainText); ShowToast("Result copied to the clipboard."); }
        catch { ShowToast("The result could not be copied. Please try again."); }
    }

    private void StComputedExportTxt_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ComputedResultRow row) return;
        SaveStatExport("analysis_result.txt", "Text files (*.txt)|*.txt", row.FullPlainText, "Analysis result exported");
    }

    private void StComputedExportCsv_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ComputedResultRow row) return;
        SaveStatExport("analysis_result.csv", "CSV files (*.csv)|*.csv", row.CsvText, "Analysis result exported");
    }

    // ---- Phase 4C Slice 2: Delete one computed result -------------------------
    // Removes exactly one result from the in-memory list AND the persisted
    // project copy. Never touches the dataset, extraction sheet, recommendations,
    // or the project itself. Safe no-op if the result is already gone.
    private void StComputedDelete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ComputedResultRow row) return;
        if (!_computedRows.Remove(row))
        {
            ShowToast("That result is no longer available.");
            return;
        }
        if (_computedDetailsRow?.Id == row.Id)
        {
            _computedDetailsRow = null;
            StComputedDetailsCard.Visibility = Visibility.Collapsed;
        }
        RenderComputedResults();
        var p = CurrentResearchProject();
        if (p is not null) PersistComputedResults(p);
        ShowToast("Result deleted.");
    }

    // ---- Phase 4C Slice 2: Re-run one computed result --------------------------
    // Re-locates the SAME pairing by its saved Outcome/Predictor names and
    // recomputes it through the existing dispatch path — never a different test,
    // never a guessed variable. Replaces the row IN PLACE (same Id, same card
    // position); never inserts a duplicate.
    private async void StComputedRerun_Click(object sender, RoutedEventArgs e)
    {
        if (_recoRunning) return;
        if ((sender as FrameworkElement)?.DataContext is not ComputedResultRow row) return;

        var p = CurrentResearchProject();
        if (p is null) return;
        if (_statData is null)
        {
            ShowToast("Upload the complete CSV dataset to compute this analysis.");
            return;
        }
        if (_recoResult is null)
        {
            ShowToast("This variable pairing is no longer available. Re-run it from Recommended Analysis instead.");
            return;
        }

        // Look up by NAME only — if the pairing can't be found unambiguously,
        // never guess a different variable; tell the user instead.
        var rec = _recoResult.Recommendations.FirstOrDefault(r =>
            r.OutcomeName == row.OutcomeName && r.PredictorName == row.PredictorName);
        if (rec is null)
        {
            ShowToast("This variable pairing is no longer available. Re-run it from Recommended Analysis instead.");
            return;
        }
        if (!IsRunnableReco(rec))
        {
            ShowToast("This pairing can no longer be computed. Re-run it from Recommended Analysis instead.");
            return;
        }

        var vars = p.Variables ?? new List<ResearchVariable>();
        var outcome = vars.FirstOrDefault(v => (v.VariableName ?? "").Trim() == rec.OutcomeName);
        var predictor = vars.FirstOrDefault(v => (v.VariableName ?? "").Trim() == rec.PredictorName);
        if (outcome is null || predictor is null)
        {
            ShowToast("The variables for this comparison could not be found in the extraction sheet.");
            return;
        }

        _recoRunning = true;
        try
        {
            StRunLoadingCard.Visibility = Visibility.Visible;

            await StepRunLoading("Preparing variables…");
            await StepRunLoading("Checking assumptions…");
            await StepRunLoading("Computing locally…");
            IInferenceExportable result = DispatchCompute(rec, outcome, predictor);
            await StepRunLoading("Building result summary…");

            var updated = BuildComputedRow(rec, result, p);
            updated.Id = row.Id;   // replace in place — same identity, same card position

            int idx = _computedRows.IndexOf(row);
            if (idx >= 0) _computedRows[idx] = updated;
            else _computedRows.Insert(0, updated);   // row vanished mid-run — never lose the result

            if (_computedDetailsRow?.Id == row.Id)
            {
                _computedDetailsRow = updated;
                StComputedDetailsTitle.Text = $"{updated.TestName} — {updated.Variables}";
                StComputedDetailsText.Text = updated.FullPlainText;
            }

            PersistComputedResults(p);
            StRunLoadingCard.Visibility = Visibility.Collapsed;
            RenderComputedResults();
            ShowToast(result.Computed
                ? "Result re-run and updated (deterministic, no AI)."
                : "This comparison could not produce a reliable result — see the details.");
        }
        catch (Exception ex)
        {
            StRunLoadingCard.Visibility = Visibility.Collapsed;
            HandleDxError("re-run the analysis", ex);
        }
        finally { _recoRunning = false; }
    }

    // ---- Computed Results productivity actions -------------------------------
    // Run every currently-runnable card in sequence (never parallel), adding each
    // result to Computed Results. Non-runnable cards (role review / unsupported)
    // are skipped; a card that CannotCompute is still added (as a "needs review"
    // card) and counted — one bad pairing never aborts the batch.
    private async void StRecoRunAll_Click(object sender, RoutedEventArgs e)
    {
        // Phase 7: running analyses is a protected action (active license required).
        if (!LicenseAllowsProtectedAction(out string gateMessage)) { ShowToast(gateMessage); return; }
        if (_recoRunning) return;
        if (!RecoUnlockedGuard()) return;
        var p = CurrentResearchProject();
        if (p is null) return;
        if (_statData is null) { ShowToast("Upload the complete CSV dataset to compute analyses."); return; }
        if (_recoResult is null) return;

        var vars = p.Variables ?? new List<ResearchVariable>();
        var runnable = _recoResult.Recommendations.Where(IsRunnableReco).ToList();
        if (runnable.Count == 0) { ShowToast("No supported analyses are available to run yet."); return; }

        _recoRunning = true;
        try
        {
            ShowStatView("computed");
            StComputedDetailsCard.Visibility = Visibility.Collapsed;
            StComputedEmpty.Visibility = Visibility.Collapsed;
            StRunLoadingCard.Visibility = Visibility.Visible;

            int computed = 0, review = 0;
            for (int i = 0; i < runnable.Count; i++)
            {
                var rec = runnable[i];
                StRunLoadingStep.Text = $"Running supported analyses {i + 1} of {runnable.Count}…";
                await System.Threading.Tasks.Task.Delay(400);

                var outcome = vars.FirstOrDefault(v => (v.VariableName ?? "").Trim() == rec.OutcomeName);
                var predictor = vars.FirstOrDefault(v => (v.VariableName ?? "").Trim() == rec.PredictorName);
                if (outcome is null || predictor is null) { review++; continue; }

                var result = DispatchCompute(rec, outcome, predictor);
                AddOrReplaceComputedRow(BuildComputedRow(rec, result, p));
                if (result.Computed) computed++; else review++;
            }
            _computedResultsProjectId = p.Id;
            PersistComputedResults(p);   // one save after the whole batch, not per item

            StRunLoadingCard.Visibility = Visibility.Collapsed;
            RenderComputedResults();
            ShowToast($"Ran {runnable.Count} supported analyses — {computed} computed, {review} need review. See Computed Results.");
        }
        catch (Exception ex)
        {
            StRunLoadingCard.Visibility = Visibility.Collapsed;
            HandleDxError("run all supported analyses", ex);
        }
        finally { _recoRunning = false; }
    }

    private void StComputedExportAllTxt_Click(object sender, RoutedEventArgs e)
    {
        if (_computedRows.Count == 0) { ShowToast("No computed results to export yet."); return; }
        SaveStatExport("computed_results_all.txt", "Text files (*.txt)|*.txt", BuildAllResultsTxt(), "All computed results exported");
    }

    private void StComputedExportAllCsv_Click(object sender, RoutedEventArgs e)
    {
        if (_computedRows.Count == 0) { ShowToast("No computed results to export yet."); return; }
        SaveStatExport("computed_results_all.csv", "CSV files (*.csv)|*.csv", BuildAllResultsCsv(), "All computed results exported");
    }

    private void StComputedClear_Click(object sender, RoutedEventArgs e)
    {
        if (_computedRows.Count == 0) { ShowToast("There are no computed results to clear."); return; }
        _computedRows.Clear();
        _computedDetailsRow = null;
        StComputedDetailsCard.Visibility = Visibility.Collapsed;
        RenderComputedResults();
        // Clear both the in-memory list and the saved copy — dataset, extraction
        // sheet, and recommendations are never touched by this action.
        var p = CurrentResearchProject();
        if (p is not null) PersistComputedResults(p);
        ShowToast("Computed results cleared. Your dataset, extraction sheet, and recommendations are unchanged.");
    }

    // Combined exports — aggregate only, no participant rows.
    private string BuildAllResultsTxt()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("COMPUTED RESULTS — combined export");
        sb.AppendLine($"Total results: {_computedRows.Count}");
        sb.AppendLine("Computed locally · deterministic · no AI. Aggregate outputs only.");
        sb.AppendLine(new string('=', 78));
        foreach (var row in _computedRows)
        {
            sb.AppendLine();
            sb.AppendLine(row.FullPlainText);
            sb.AppendLine(new string('-', 78));
        }
        return sb.ToString();
    }

    private string BuildAllResultsCsv()
    {
        var sb = new System.Text.StringBuilder();
        string Q(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
        sb.AppendLine(string.Join(",", new[] { "Test", "Variables", "N", "PValue", "Effect", "Significance", "ComputedTime", "AiInvolved" }.Select(Q)));
        foreach (var row in _computedRows)
            sb.AppendLine(string.Join(",", new[]
            {
                Q(row.TestName), Q(row.Variables), Q(row.ValidNDisplay), Q(row.PValueDisplay),
                Q(row.EffectDisplay), Q(row.SignificanceText), Q(row.ComputedTimeDisplay), Q("no")
            }));
        return sb.ToString();
    }

    // Locked-state navigation buttons.
    private void StRecoGoDescriptive_Click(object sender, RoutedEventArgs e) => SetRecommendedView(false);
    private void StRecoGoDataExtraction_Click(object sender, RoutedEventArgs e)
    {
        SwitchResearchTab(SegDataExt);
        ShowToast("Resolve any remaining items in Data Extraction, then return to Statistics.");
    }
    private void StRecoUploadCsv_Click(object sender, RoutedEventArgs e)
    {
        ImportCsv_Click(sender, e);
        RefreshStatisticsTab();
    }
    private void StRecoRunMagicFix_Click(object sender, RoutedEventArgs e)
    {
        SetRecommendedView(false);
        MagicFix_Click(sender, e);
    }

    private void RenderStatisticsReadiness()
    {
        if (_statReadiness is not { } r) return;

        (Brush bg, Brush fg, string text) = r.State switch
        {
            StatisticsReadinessState.Ready => ((Brush)FindResource("SuccessSoftBrush"), (Brush)FindResource("SuccessBrush"), "Ready for descriptive statistics"),
            StatisticsReadinessState.NeedsReview => ((Brush)FindResource("WarningSoftBrush"), (Brush)FindResource("WarningBrush"), "Ready with warnings"),
            _ => ((Brush)FindResource("DangerSoftBrush"), (Brush)FindResource("DangerBrush"), "Blocked")
        };
        StStateBadge.Background = bg;
        StStateBadgeText.Foreground = fg;
        StStateBadgeText.Text = text;

        StScoreText.Text = $"{r.Score}/100";
        StScoreBar.Background = fg;
        UpdateStatScoreBar();

        StExplanationText.Text = r.Explanation;
        StCardsList.ItemsSource = r.Cards;

        // Run button: enabled unless blocked; a disabled button explains why.
        StRunBtn.IsEnabled = r.CanRun;
        StRunBtn.ToolTip = r.CanRun
            ? "Generate descriptive statistics from deterministic calculations."
            : (r.Blockers.FirstOrDefault()?.Message ?? "Analysis is blocked until required issues are resolved.");

        // Blocked → show the exact next action; warnings → offer the review list.
        bool blocked = !r.CanRun;
        StPrimaryActionBtn.Visibility = blocked ? Visibility.Visible : Visibility.Collapsed;
        StPrimaryActionBtn.Content = r.PrimaryActionLabel;
        StReviewWarningsBtn.Visibility = r.Warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        StReviewWarningsBtn.Content = $"Review Warnings ({r.Warnings.Count})";

        // Magic Fix: fast guided repair path. Shown only when there is actually
        // at least one applicable repair (blocker- or warning-level), so it is
        // never a dead-end. Repairs cover mis-typed/unordered/mis-labelled
        // variables, which can exist even in a "Ready with warnings" state.
        int repairable = _magicFixProposals.Count(p => p.IsApplicable);
        StMagicFixBtn.Visibility = repairable > 0 ? Visibility.Visible : Visibility.Collapsed;
        StMagicFixBtn.Content = $"Magic Fix ({repairable})";

        var visibleIssues = r.Issues.Where(i => i.Severity != StatisticsSeverity.Info)
                             .OrderByDescending(i => (int)i.Severity).ToList();
        StIssuesList.ItemsSource = visibleIssues;
        // Blockers are always shown; the warnings-only list stays collapsed
        // until the user asks for it.
        StIssuesSection.Visibility = blocked && visibleIssues.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateStatScoreBar()
    {
        if (_statReadiness is not { } r || StScoreBar.Parent is not FrameworkElement track) return;
        double w = track.ActualWidth;
        if (w <= 0)
        {
            Dispatcher.BeginInvoke(new Action(UpdateStatScoreBar), DispatcherPriority.Loaded);
            return;
        }
        StScoreBar.Width = Math.Max(r.Score > 0 ? 8 : 0, w * r.Score / 100.0);
    }

    private void RenderDatasetOverview(ResearchProject p, StatisticsDataset? data, StatisticsMatchInput match)
    {
        var rows = new List<StatisticsReadinessCard>();
        void Add(string k, string v, string kind = "Muted") => rows.Add(new StatisticsReadinessCard { Title = k, Value = v, Kind = kind });

        if (data is null)
        {
            StDatasetCard.Visibility = Visibility.Collapsed;
            return;
        }

        var vars = (p.Variables ?? new List<ResearchVariable>()).Where(v => !string.IsNullOrWhiteSpace(v.VariableName)).ToList();
        int matched = vars.Count(v => match.VariableColumn.ContainsKey(v.Id));
        int requiredUnmatched = vars.Count(v => v.IsRequired && !match.VariableColumn.ContainsKey(v.Id));
        int optionalUnmatched = vars.Count(v => !v.IsRequired && !match.VariableColumn.ContainsKey(v.Id));

        long totalCells = (long)data.RowCount * data.ColumnCount;
        long missingCells = 0;
        for (int c = 0; c < data.ColumnCount; c++)
            for (int r = 0; r < data.RowCount; r++)
                if (StatisticsMissingTokens.IsMissing(data.Cell(r, c))) missingCells++;
        double missPct = totalCells == 0 ? 0 : 100.0 * missingCells / totalCells;

        StDatasetSubText.Text = "Raw values come from the uploaded CSV; how each variable is analyzed comes from the extraction sheet.";
        Add("CSV file", data.FileName.Length > 0 ? data.FileName : "—");
        Add("Uploaded rows (participants)", data.RowCount.ToString());
        if (p.TargetSampleSize is int t && t > 0)
        {
            Add("Target sample size", t.ToString());
            if (data.RowCount < t)
            {
                Add("Remaining samples needed", (t - data.RowCount).ToString(), "Bad");
                Add("Sample status", "Incomplete sample", "Bad");
            }
            else Add("Sample status", "Complete", "Good");
        }
        else Add("Target sample size", "Not found — confirm your sample size before running statistics", "Warn");

        Add("Dataset columns", data.ColumnCount.ToString());
        Add("Extraction sheet variables", vars.Count.ToString());
        Add("Matched variables", $"{matched} of {vars.Count}", matched == vars.Count && vars.Count > 0 ? "Good" : "Muted");
        if (requiredUnmatched > 0) Add("Unmatched required variables", requiredUnmatched.ToString(), "Bad");
        if (optionalUnmatched > 0) Add("Unmatched optional variables", optionalUnmatched.ToString(), "Warn");
        if (match.CsvOnlyColumns.Count > 0) Add("Dataset-only columns (not analyzed)", string.Join(", ", match.CsvOnlyColumns), "Warn");
        if (match.MetadataColumns.Count > 0) Add("System columns (ignored)", string.Join(", ", match.MetadataColumns));
        Add("Total cells", totalCells.ToString());
        Add("Missing cells", $"{missingCells} ({missPct.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}%)",
            missPct >= 50 ? "Bad" : missPct >= 20 ? "Warn" : "Muted");
        if (data.DuplicateColumnNames.Count > 0)
            Add("Duplicate column names", string.Join(", ", data.DuplicateColumnNames), "Warn");
        if (p.DescriptiveStatistics is { } rec)
            Add("Last analysis", rec.GeneratedDisplay, "Good");

        StOverviewList.ItemsSource = rows;
        StDatasetCard.Visibility = Visibility.Visible;
    }

    private void RenderMatchingSummary(ResearchProject p, StatisticsDataset? data, StatisticsMatchInput match)
    {
        var vars = (p.Variables ?? new List<ResearchVariable>()).Where(v => !string.IsNullOrWhiteSpace(v.VariableName)).ToList();
        if (vars.Count == 0 && (data is null || data.ColumnCount == 0))
        {
            StMatchingCard.Visibility = Visibility.Collapsed;
            return;
        }

        var rows = new List<StatMatchDisplayRow>();
        foreach (var v in vars)
        {
            match.VariableColumn.TryGetValue(v.Id, out string? col);
            var prep = StatisticsVariablePreparer.Prepare(v, data, col);
            string typeDisplay = string.IsNullOrWhiteSpace(v.MeasurementLevel) || v.MeasurementLevel == "NotApplicable"
                ? v.VariableType : $"{v.VariableType} · {v.MeasurementLevel}";

            var row = new StatMatchDisplayRow
            {
                Name = v.VariableName.Trim(),
                Label = (v.QuestionLabel ?? "").Trim(),
                TypeDisplay = typeDisplay,
                Role = string.IsNullOrWhiteSpace(v.Role) ? "—" : v.Role,
                ColumnDisplay = string.IsNullOrWhiteSpace(col) ? "No matching column" : col!,
            };

            bool matched = !string.IsNullOrWhiteSpace(col) && data is not null && data.ColumnIndexOf(col!) >= 0;
            if (prep.Kind == VariableAnalysisKind.Excluded && matched)
            {
                row.Group = "Excluded from analysis"; row.GroupOrder = 3;
                row.CountsDisplay = prep.ExclusionReason;
                row.StatusText = "Excluded"; row.StatusKind = "Muted";
            }
            else if (!matched && v.IsRequired)
            {
                row.Group = "Required variables — missing or unmatched"; row.GroupOrder = 0;
                row.CountsDisplay = "Required for analysis";
                row.StatusText = "Blocked"; row.StatusKind = "Bad";
            }
            else if (!matched)
            {
                row.Group = "Optional variables not found in dataset"; row.GroupOrder = 2;
                row.CountsDisplay = "Excluded from analysis";
                row.StatusText = "Not matched"; row.StatusKind = "Warn";
            }
            else
            {
                row.Group = "Matched variables"; row.GroupOrder = 1;
                row.CountsDisplay = $"Valid {prep.ValidN} · Missing {prep.MissingN}";
                string q = StatisticsVariablePreparer.MissingStatus(prep.MissingPercent);
                if (prep.ValidN == 0) { row.StatusText = "No valid values"; row.StatusKind = "Bad"; }
                else if (q == "OK") { row.StatusText = "Good"; row.StatusKind = "Good"; }
                else { row.StatusText = q; row.StatusKind = "Warn"; }
            }
            rows.Add(row);
        }

        foreach (var c in match.CsvOnlyColumns)
            rows.Add(new StatMatchDisplayRow
            {
                Group = "Dataset columns not linked to a variable", GroupOrder = 4,
                Name = c, Label = "", TypeDisplay = "—", Role = "—",
                ColumnDisplay = c, CountsDisplay = "Not analyzed",
                StatusText = "Ignored", StatusKind = "Warn"
            });
        foreach (var c in match.MetadataColumns)
            rows.Add(new StatMatchDisplayRow
            {
                Group = "System columns (ignored by design)", GroupOrder = 5,
                Name = c, Label = "", TypeDisplay = "—", Role = "—",
                ColumnDisplay = c, CountsDisplay = "Form/system metadata",
                StatusText = "Ignored", StatusKind = "Muted"
            });

        var view = new System.Windows.Data.CollectionViewSource { Source = rows };
        view.SortDescriptions.Add(new System.ComponentModel.SortDescription("GroupOrder", System.ComponentModel.ListSortDirection.Ascending));
        view.SortDescriptions.Add(new System.ComponentModel.SortDescription("Name", System.ComponentModel.ListSortDirection.Ascending));
        view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription("Group"));
        StMatchingList.ItemsSource = view.View;

        StGoToConflictsBtn.Visibility = _statReadiness?.Blockers.Any(b => b.ActionHint == "ResolveRequiredIssues") == true
            ? Visibility.Visible : Visibility.Collapsed;
        StMatchingCard.Visibility = Visibility.Visible;
    }

    // ---- Run + primary actions ----------------------------------------------

    private async void RunDescriptiveStatistics_Click(object sender, RoutedEventArgs e)
    {
        // Phase 7: running an analysis is a protected action (active license required).
        if (!LicenseAllowsProtectedAction(out string gateMessage)) { ShowToast(gateMessage); return; }

        var p = CurrentResearchProject();
        if (p is null) { ShowToast("Open a project first."); return; }

        // Phase 9: a project has never been counted until a first descriptive-statistics
        // run is FINALIZED on the server. Because we only persist results after a
        // successful finalize, a locally-saved DescriptiveStatistics record proves the
        // project is already counted; a null one means this is a (chargeable) first run.
        bool isFirstRun = p.DescriptiveStatistics is null;

        // An unrecoverable pending result (its slot can no longer be obtained) is resolved by
        // discarding — not by running again — so surface that dialog and stop here.
        if (HasUnrecoverablePending(p.Id))
        {
            ShowPendingResultOverlay(p.Id,
                "Your earlier analysis finished, but it couldn't be activated because your current plan's project allowance is now full. You can safely discard it. A fresh allowance begins with your next subscription cycle.");
            return;
        }

        // A prior first-run whose finalize hasn't landed yet blocks new first runs; try to recover it first.
        if (HasBlockingPending(p.Id))
        {
            await RetryPendingFinalizationsAsync();
            if (HasUnrecoverablePending(p.Id))
            {
                ShowPendingResultOverlay(p.Id,
                    "Your earlier analysis finished, but it couldn't be activated because your current plan's project allowance is now full. You can safely discard it. A fresh allowance begins with your next subscription cycle.");
                return;
            }
            if (HasBlockingPending(p.Id))
            {
                ShowToast("OrbitLab is still finishing recording this project's first analysis. It will complete automatically once you're back online.");
                return;
            }
            isFirstRun = p.DescriptiveStatistics is null;
        }

        // First run requires a successful backend RESERVATION before analysis starts.
        // There is no offline first run — a never-counted project cannot be started while
        // the backend is unavailable (that would defeat the project allowance).
        string? reservationId = null;
        if (isFirstRun && AppConfig.IsBackendConfigured)
        {
            if (_license is null) { ShowToast("Please log in to your OrbitLab account to use this feature."); return; }
            var res = await LicenseApiClient.ReserveProjectAsync(_license.Token, p.Id);
            if (!res.Ok)
            {
                string code = res.Error?.Code ?? "network";
                ShowToast(code switch
                {
                    "project_limit_reached" => ProjectLimitMessage(res.Error is not null ? EffectiveProjectLimit() : 1),
                    "not_entitled" => "Your OrbitLab subscription is not active. Please contact support or renew your Commercial Beta access.",
                    _ => "OrbitLab needs to reach the server to start your first analysis on this project. Check your connection and try again.",
                });
                return;
            }
            if (res.Data!.AlreadyCounted) isFirstRun = false;   // became counted elsewhere → free re-run
            else reservationId = res.Data.ReservationId;
        }

        async Task ReleaseIfHeldAsync()
        {
            if (reservationId is not null && _license is not null)
                try { await LicenseApiClient.ReleaseProjectAsync(_license.Token, p.Id, reservationId); } catch { }
        }

        try
        {
            RefreshStatisticsTab();   // re-evaluate readiness on the current state
            if (_statReadiness is not { } readiness) { await ReleaseIfHeldAsync(); return; }
            if (!readiness.CanRun)
            {
                ShowToast("Analysis is blocked until required issues are resolved.");
                await ReleaseIfHeldAsync();
                return;
            }

            var data = LoadStatisticsDataset(p, out string dataError);
            if (data is null)
            {
                ShowToast(dataError.Length > 0 ? dataError : "The dataset could not be read. Upload your CSV file again.");
                await ReleaseIfHeldAsync();
                return;
            }

            var match = BuildStatisticsMatchInput(p, data);
            var record = DescriptiveStatisticsEngine.Run(
                p.Variables ?? new List<ResearchVariable>(), data, match,
                p.TargetSampleSize, readiness, CurrentStatisticsFingerprint(p));

            // Output options chosen at run time.
            record.Decimals = int.TryParse(StDecimalsBox.SelectedItem as string, out int d) ? d : 2;
            record.OutputStyle = StStyleBox.SelectedItem as string ?? "Academic Clean";
            record.IncludeMissingSummary = StOptMissing.IsChecked == true;
            record.IncludeAuditNotes = StOptAudit.IsChecked == true;
            record.IncludeIgnoredColumnsNote = StOptIgnored.IsChecked == true;
            record.IncludeTextSummary = StOptText.IsChecked == true;

            // For a chargeable FIRST run we do NOT persist or display the results as
            // completed until finalization succeeds. A re-run of an already-counted
            // project commits immediately.
            if (reservationId is not null)
            {
                var fin = await LicenseApiClient.FinalizeProjectAsync(_license!.Token, p.Id, reservationId);
                if (fin.Ok && (fin.Data!.Finalized || fin.Data.AlreadyCounted))
                {
                    CommitDescriptiveResult(p, record, data, match);
                    await RefreshAccountOverviewAsync();
                }
                else if (fin.Error is not null && (fin.Error.Code == "reservation_expired" || fin.Error.Code == "reservation_not_found"))
                {
                    ShowToast("Your project reservation expired before the analysis finished recording. Please run the analysis again.");
                    // Results intentionally not persisted; the slot was freed server-side.
                }
                else
                {
                    // Transient network/server failure: keep the result ON HOLD locally (never
                    // uploaded, never shown as completed) and recover it automatically later.
                    AddPendingFinalization(p.Id, reservationId, record);
                    ShowToast("Your analysis ran, but OrbitLab couldn't finish recording it yet. It will be completed automatically when you're back online — this project is on hold until then.");
                }
            }
            else
            {
                CommitDescriptiveResult(p, record, data, match);   // already counted → free re-run
            }
        }
        catch (Exception ex)
        {
            HandleDxError("run descriptive statistics", ex);
            await ReleaseIfHeldAsync();
        }
    }

    // Persists + renders a finalized descriptive-statistics result. Only called once the
    // project's slot is definitely accounted for (finalized) or the run is a free re-run.
    private void CommitDescriptiveResult(ResearchProject p, DescriptiveStatisticsRecord record, StatisticsDataset data, StatisticsMatchInput match)
    {
        p.DescriptiveStatistics = record;
        p.CurrentStage = "Descriptive statistics completed";
        p.UpdatedAt = DateTime.UtcNow;
        SaveResearch();
        StStaleBanner.Visibility = Visibility.Collapsed;
        RenderStatisticsOutput(record);
        RenderDatasetOverview(p, data, match);
        ShowToast($"Descriptive statistics generated for {record.VariablesAnalyzed} variable{(record.VariablesAnalyzed == 1 ? "" : "s")} across {record.RowsAnalyzed} rows.");
    }

    private void StPrimaryAction_Click(object sender, RoutedEventArgs e)
    {
        switch (_statReadiness?.PrimaryActionCode)
        {
            case "UploadCsv":
            case "UploadCompleteCsv":
                ImportCsv_Click(sender, e);
                RefreshStatisticsTab();
                break;
            case "ResolveRequiredIssues":
            case "GoToDataExtraction":
            default:
                SwitchResearchTab(SegDataExt);
                ShowToast("Resolve the required issues in Data Extraction, then return to Statistics.");
                break;
        }
    }

    private void StReviewWarnings_Click(object sender, RoutedEventArgs e)
    {
        StIssuesSection.Visibility = StIssuesSection.Visibility == Visibility.Visible
            ? Visibility.Collapsed : Visibility.Visible;
        if (StIssuesSection.Visibility == Visibility.Visible)
            StIssuesSection.BringIntoView();
    }

    private void StGoToConflicts_Click(object sender, RoutedEventArgs e)
    {
        SwitchResearchTab(SegDataExt);
        ShowToast("Use Validate Sheet and Resolve Conflicts in Data Extraction, then return to Statistics.");
    }

    // ---- Output rendering ----------------------------------------------------

    private void RenderStatisticsOutput(DescriptiveStatisticsRecord rec)
    {
        // Sync the option controls to the record without triggering re-renders.
        bool wasReady = _statUiReady;
        _statUiReady = false;
        StDecimalsBox.SelectedItem = Math.Clamp(rec.Decimals, 0, 3).ToString();
        if ((StStyleBox.ItemsSource as string[])?.Contains(rec.OutputStyle) == true)
            StStyleBox.SelectedItem = rec.OutputStyle;
        StOptMissing.IsChecked = rec.IncludeMissingSummary;
        StOptAudit.IsChecked = rec.IncludeAuditNotes;
        StOptIgnored.IsChecked = rec.IncludeIgnoredColumnsNote;
        StOptText.IsChecked = rec.IncludeTextSummary;
        _statUiReady = wasReady;

        _statTables = StatisticsTableBuilder.Build(rec);
        StOutputMetaText.Text = $"Generated {rec.GeneratedDisplay} · {rec.RowsAnalyzed} rows · {rec.VariablesAnalyzed} of {rec.TotalVariables} variables · {rec.OutputStyle}";

        StOutputHost.Children.Clear();
        string lastSection = "";
        int tableNumber = 0;
        foreach (var t in _statTables)
        {
            if (t.Section != lastSection)
            {
                lastSection = t.Section;
                StOutputHost.Children.Add(new TextBlock
                {
                    Text = t.Section,
                    FontSize = 15,
                    FontWeight = FontWeights.Bold,
                    Foreground = (Brush)FindResource("TextBrush"),
                    Margin = new Thickness(4, 22, 0, 0)
                });
            }
            tableNumber++;
            StOutputHost.Children.Add(BuildStatTableCard(t, rec.OutputStyle, tableNumber));
        }
        StOutputPanel.Visibility = Visibility.Visible;
    }

    // Builds one professional table card. Styles adjust typography/rules only —
    // the numbers are identical in every style.
    private FrameworkElement BuildStatTableCard(StatisticsOutputTable t, string style, int tableNumber)
    {
        double fontSize = style switch { "Compact Report" => 11.5, "Thesis Detailed" => 13, _ => 12.5 };
        Thickness cellPad = style switch
        {
            "Compact Report" => new Thickness(9, 3, 9, 3),
            "Thesis Detailed" => new Thickness(11, 7, 11, 7),
            _ => new Thickness(10, 5, 10, 5)
        };
        bool gridLines = style == "SPSS-like Academic";
        bool thesis = style == "Thesis Detailed";

        var text = (Brush)FindResource("TextBrush");
        var muted = (Brush)FindResource("MutedBrush");
        var softLine = (Brush)FindResource("BorderBrushSoft");
        var softBg = (Brush)FindResource("SoftCardBrush");

        var card = new Border
        {
            Style = (Style)FindResource("CardBorder"),
            Padding = new Thickness(22),
            Margin = new Thickness(0, 12, 0, 0)
        };
        var stack = new StackPanel();
        card.Child = stack;

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new TextBlock
        {
            Text = thesis ? $"Table {tableNumber}. {t.Title}" : t.Title,
            FontSize = 13.5,
            FontWeight = FontWeights.Bold,
            Foreground = text,
            TextWrapping = TextWrapping.Wrap
        };
        header.Children.Add(title);
        var copyBtn = new Button
        {
            Style = (Style)FindResource("GhostButton"),
            Content = "Copy",
            MinWidth = 64,
            Height = 26,
            MinHeight = 26,
            FontSize = 11,
            Tag = t,
            VerticalAlignment = VerticalAlignment.Top
        };
        copyBtn.Click += StCopyTable_Click;
        Grid.SetColumn(copyBtn, 1);
        header.Children.Add(copyBtn);
        stack.Children.Add(header);

        if (t.Caption.Length > 0)
            stack.Children.Add(new TextBlock
            {
                Text = t.Caption,
                FontSize = 11.5,
                FontStyle = thesis ? FontStyles.Italic : FontStyles.Normal,
                Foreground = muted,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0)
            });

        var grid = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        bool keyValue = t.Columns.Count == 2 && t.Columns.All(c => c.Length == 0);
        int colCount = Math.Max(t.Columns.Count, t.Rows.Count == 0 ? 1 : t.Rows.Max(r => r.Cells.Count));
        for (int i = 0; i < colCount; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = keyValue
                    ? (i == 0 ? GridLength.Auto : new GridLength(1, GridUnitType.Star))
                    : (i == 0 ? new GridLength(1.4, GridUnitType.Star) : GridLength.Auto),
                MinWidth = keyValue && i == 0 ? 200 : 0
            });
        }

        int gridRow = 0;
        void AddRule(double height, Brush brush)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var rule = new Border { Height = height, Background = brush, Margin = new Thickness(0, 1, 0, 1) };
            Grid.SetRow(rule, gridRow);
            Grid.SetColumnSpan(rule, colCount);
            grid.Children.Add(rule);
            gridRow++;
        }
        void AddCells(List<string> cells, bool bold, bool subtle, bool isHeader)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int i = 0; i < colCount; i++)
            {
                string cell = i < cells.Count ? cells[i] : "";
                bool right = i < t.RightAlign.Count && t.RightAlign[i];
                var tb = new TextBlock
                {
                    Text = cell,
                    FontSize = fontSize,
                    FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground = subtle ? muted : text,
                    TextAlignment = right ? TextAlignment.Right : TextAlignment.Left,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = gridLines ? new Thickness(0) : cellPad,
                    Padding = gridLines ? cellPad : new Thickness(0)
                };
                FrameworkElement el = tb;
                if (gridLines)
                {
                    el = new Border
                    {
                        BorderBrush = softLine,
                        BorderThickness = new Thickness(0.5),
                        Background = isHeader ? softBg : Brushes.Transparent,
                        Child = tb
                    };
                }
                Grid.SetRow(el, gridRow);
                Grid.SetColumn(el, i);
                grid.Children.Add(el);
            }
            gridRow++;
        }

        bool hasHeader = t.Columns.Any(c => c.Length > 0);
        if (hasHeader)
        {
            if (!gridLines) AddRule(1.6, text);            // academic top rule
            AddCells(t.Columns, bold: true, subtle: false, isHeader: true);
            if (!gridLines) AddRule(1, text);              // header underline
        }
        foreach (var r in t.Rows)
        {
            if (r.IsEmphasis && !gridLines) AddRule(1, softLine);
            AddCells(r.Cells, bold: r.IsEmphasis, subtle: r.IsSubtle, isHeader: false);
        }
        if (hasHeader && !gridLines) AddRule(1.6, text);   // academic bottom rule

        stack.Children.Add(grid);
        return card;
    }

    // ---- Copy / export --------------------------------------------------------

    private void StCopyTable_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if ((sender as FrameworkElement)?.Tag is not StatisticsOutputTable t) return;
            Clipboard.SetText(StatisticsExportService.BuildTablePlainText(t));
            ShowToast($"“{t.Title}” copied to the clipboard.");
        }
        catch { ShowToast("The table could not be copied. Please try again."); }
    }

    private (DescriptiveStatisticsRecord Record, List<StatisticsOutputTable> Tables)? CurrentStatOutput()
    {
        var p = CurrentResearchProject();
        if (p?.DescriptiveStatistics is not { } rec)
        {
            ShowToast("No descriptive statistics have been generated yet. Go to Statistics to run analysis.");
            return null;
        }
        var tables = _statTables.Count > 0 ? _statTables : StatisticsTableBuilder.Build(rec);
        return (rec, tables);
    }

    private void StCopyAll_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (CurrentStatOutput() is not { } o) return;
            Clipboard.SetText(StatisticsExportService.BuildPlainText(o.Record, o.Tables));
            ShowToast("All descriptive results copied to the clipboard.");
        }
        catch { ShowToast("The results could not be copied. Please try again."); }
    }

    private void StExportCsv_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentStatOutput() is not { } o) return;
        SaveStatExport("descriptive_statistics.csv", "CSV files (*.csv)|*.csv",
            StatisticsExportService.BuildCsv(o.Record, o.Tables), "Tables exported");
    }

    private void StExportAudit_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentStatOutput() is not { } o) return;
        SaveStatExport("statistics_audit_notes.txt", "Text files (*.txt)|*.txt",
            StatisticsExportService.BuildAuditNotesText(o.Record), "Audit notes exported");
    }

    private void StExportHtml_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentStatOutput() is not { } o) return;
        SaveStatExport("descriptive_statistics_report.html", "HTML files (*.html)|*.html",
            StatisticsExportService.BuildHtml(o.Record, o.Tables), "HTML report exported");
    }

    private void SaveStatExport(string defaultName, string filter, string content, string successVerb)
    {
        try
        {
            var sfd = new SaveFileDialog { FileName = defaultName, Filter = filter, AddExtension = true };
            if (sfd.ShowDialog() != true) return;
            File.WriteAllText(sfd.FileName, content, Encoding.UTF8);
            ShowToast($"{successVerb} to {Path.GetFileName(sfd.FileName)}.");
        }
        catch (IOException)
        {
            ShowToast("The file could not be saved. It may be open in another program — close it and try again.");
        }
        catch (UnauthorizedAccessException)
        {
            ShowToast("The file could not be saved to that location. Choose a different folder and try again.");
        }
        catch
        {
            ShowToast("The export could not be completed. Please try a different location.");
        }
    }

    private void StOutputOption_Changed(object sender, RoutedEventArgs e)
    {
        if (!_statUiReady) return;
        try
        {
            var p = CurrentResearchProject();
            if (p?.DescriptiveStatistics is not { } rec) return;

            rec.Decimals = int.TryParse(StDecimalsBox.SelectedItem as string, out int d) ? d : rec.Decimals;
            rec.OutputStyle = StStyleBox.SelectedItem as string ?? rec.OutputStyle;
            rec.IncludeMissingSummary = StOptMissing.IsChecked == true;
            rec.IncludeAuditNotes = StOptAudit.IsChecked == true;
            rec.IncludeIgnoredColumnsNote = StOptIgnored.IsChecked == true;
            rec.IncludeTextSummary = StOptText.IsChecked == true;
            SaveResearch();
            RenderStatisticsOutput(rec);   // re-render only; full precision is stored
        }
        catch (Exception ex) { HandleDxError("update the output options", ex); }
    }

    // ---- Results tab -----------------------------------------------------------

    private void RefreshResultsTab()
    {
        try
        {
            var p = CurrentResearchProject();
            var rec = p?.DescriptiveStatistics;
            bool has = p is not null && rec is not null;
            ResEmptyState.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
            ResContent.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            if (!has) return;

            bool stale = IsStatisticsStale(p!);
            ResStaleBanner.Visibility = stale ? Visibility.Visible : Visibility.Collapsed;
            ResStatusChip.Background = (Brush)FindResource(stale ? "WarningSoftBrush" : "SuccessSoftBrush");
            ResStatusChipText.Foreground = (Brush)FindResource(stale ? "WarningBrush" : "SuccessBrush");
            ResStatusChipText.Text = stale ? "Needs rerun" : "Current";

            ResGeneratedText.Text = $"Generated {rec!.GeneratedDisplay} · deterministic calculations";
            ResSummaryList.ItemsSource = new List<StatisticsReadinessCard>
            {
                new() { Title = "Dataset", Value = rec.DatasetFileName.Length > 0 ? rec.DatasetFileName : "—" },
                new() { Title = "Rows analyzed", Value = rec.RowsAnalyzed.ToString() },
                new() { Title = "Variables analyzed", Value = $"{rec.VariablesAnalyzed} of {rec.TotalVariables}" },
                new() { Title = "Missing cells", Value = $"{rec.MissingCells} ({rec.OverallMissingPercent.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}%)" },
                new() { Title = "Readiness at run time", Value = $"{rec.ReadinessStateAtRun} (score {rec.ReadinessScoreAtRun}/100)" }
            };
        }
        catch (Exception ex) { HandleDxError("refresh the results overview", ex); }
    }

    private void ResViewTables_Click(object sender, RoutedEventArgs e)
    {
        SwitchResearchTab(SegStats);
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (StOutputPanel.Visibility == Visibility.Visible) StOutputPanel.BringIntoView();
        }), DispatcherPriority.Loaded);
    }

    private void ResBackToStats_Click(object sender, RoutedEventArgs e)
        => SwitchResearchTab(SegStats);

    // ---- Magic Fix: deterministic local sheet-repair review + apply ----------

    // Opens the review modal with a fresh working copy of the current proposals.
    // If nothing repairable was found, tell the user plainly (no empty modal).
    private void MagicFix_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_magicFixProposals.Count == 0)
            {
                ShowToast("No safe automatic repairs were found. Please review the remaining issues manually.");
                return;
            }

            // Work on clones so Cancel discards edits and the dashboard copy stays intact.
            _magicFixReview.Clear();
            foreach (var p in _magicFixProposals)
                _magicFixReview.Add(CloneMagicFix(p));

            if (_magicFixView is null)
            {
                _magicFixView = new System.Windows.Data.CollectionViewSource { Source = _magicFixReview, IsLiveGroupingRequested = true };
                _magicFixView.SortDescriptions.Add(new System.ComponentModel.SortDescription("GroupOrder", System.ComponentModel.ListSortDirection.Ascending));
                _magicFixView.SortDescriptions.Add(new System.ComponentModel.SortDescription("VariableName", System.ComponentModel.ListSortDirection.Ascending));
                _magicFixView.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription("GroupDisplay"));
            }
            if (!ReferenceEquals(MagicFixList.ItemsSource, _magicFixView.View))
                MagicFixList.ItemsSource = _magicFixView.View;

            int repairable = _magicFixReview.Count(p => p.IsApplicable);
            MagicFixCountText.Text = _magicFixReview.Count == 1 ? "1 suggestion" : $"{_magicFixReview.Count} suggestions";
            _ = repairable;

            MagicFixReviewOverlay.Visibility = Visibility.Visible;
            FadeIn(MagicFixReviewOverlay, 150);
        }
        catch (Exception ex) { HandleDxError("open Magic Fix", ex); }
    }

    private static MagicFixProposal CloneMagicFix(MagicFixProposal p) => new()
    {
        VariableId = p.VariableId, VariableName = p.VariableName, Label = p.Label,
        CurrentType = p.CurrentType, CurrentLevel = p.CurrentLevel, ObservedPreview = p.ObservedPreview,
        Explanation = p.Explanation, Confidence = p.Confidence, Group = p.Group, IsApplicable = p.IsApplicable,
        SetsCoding = p.SetsCoding, ProposedType = p.ProposedType, ProposedCoding = p.ProposedCoding, Accepted = p.Accepted
    };

    private void MagicFixRemove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is MagicFixProposal p)
            _magicFixReview.Remove(p);
    }

    private void MagicFixAcceptHigh_Click(object sender, RoutedEventArgs e)
    {
        int n = 0;
        foreach (var p in _magicFixReview.Where(p => p.IsApplicable && p.Confidence == "High"))
        {
            if (!p.Accepted) n++;
            p.Accepted = true;
        }
        ShowToast(n == 0 ? "No further high-confidence repairs to accept." : $"Selected {n} high-confidence repair{(n == 1 ? "" : "s")}.");
    }

    private void MagicFixCancel_Click(object sender, RoutedEventArgs e)
    {
        MagicFixReviewOverlay.Visibility = Visibility.Collapsed;
        _magicFixReview.Clear();
        ShowToast("No changes were made. Your extraction sheet was not modified.");
    }

    // Apply selected → confirmation summary (nothing changes until confirmed).
    private void MagicFixApply_Click(object sender, RoutedEventArgs e)
    {
        int selected = _magicFixReview.Count(p => p.Accepted && p.IsApplicable);
        if (selected == 0) { ShowToast("Tick at least one repair to apply, or Cancel."); return; }

        int total = _magicFixReview.Count;
        int high = _magicFixReview.Count(p => p.IsApplicable && p.Confidence == "High");
        MagicFixSummaryText.Text = $"Magic Fix found {total} possible repair{(total == 1 ? "" : "s")}. "
            + $"{high} high-confidence repair{(high == 1 ? "" : "s")} {(high == 1 ? "is" : "are")} available, and you have {selected} selected to apply now.";
        MagicFixSummaryOverlay.Visibility = Visibility.Visible;
        FadeIn(MagicFixSummaryOverlay, 150);
    }

    private void MagicFixSummaryGoBack_Click(object sender, RoutedEventArgs e)
        => MagicFixSummaryOverlay.Visibility = Visibility.Collapsed;

    private void MagicFixSummaryCancel_Click(object sender, RoutedEventArgs e)
    {
        MagicFixSummaryOverlay.Visibility = Visibility.Collapsed;
        MagicFixReviewOverlay.Visibility = Visibility.Collapsed;
        _magicFixReview.Clear();
        ShowToast("No changes were made. Your extraction sheet was not modified.");
    }

    // Confirm → apply ONLY the accepted repairs to the extraction sheet metadata.
    // Never touches the CSV/dataset. One undo step; then persist + rerun readiness.
    private void MagicFixConfirm_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            MagicFixSummaryOverlay.Visibility = Visibility.Collapsed;
            var p = CurrentResearchProject();
            if (p is null) { MagicFixReviewOverlay.Visibility = Visibility.Collapsed; return; }

            var accepted = _magicFixReview.Where(x => x.Accepted && x.IsApplicable).ToList();
            if (accepted.Count == 0) { ShowToast("Tick at least one repair to apply, or Cancel."); return; }

            // Sync the live grid collection to the project's current variables so
            // edits land on the exact instances PersistExtraction will save.
            CommitGridFromProject(p);
            PushDxUndo();   // one undo step for the whole applied batch (pre-edit snapshot)

            int typeRepairs = 0, labelSets = 0;
            foreach (var fix in accepted)
            {
                var v = _extractionVariables.FirstOrDefault(x => x.Id == fix.VariableId);
                if (v is null) continue;

                string newType = (fix.ProposedType ?? "").Trim();
                bool typeChanged = newType.Length > 0 && !string.Equals(newType, v.VariableType, StringComparison.OrdinalIgnoreCase);
                if (newType.Length > 0)
                {
                    v.VariableType = newType;
                    v.MeasurementLevel = fix.ProposedLevel;   // keep level consistent with type
                }
                if (fix.ChangesCoding)
                {
                    v.Coding = fix.ProposedCoding.Trim();
                    // ParseValueLabels lets ValueLabels override Coding, so a stale
                    // ValueLabels (e.g. from AI extraction) would mask this repaired
                    // coding and the "outside defined labels" readiness warning would
                    // survive the apply. The proposed coding is the authoritative,
                    // complete mapping (the added-labels branch already folds in any
                    // existing labels), so clear the overriding field. Coding — not
                    // ValueLabels — is what the AI prompts already send, so the
                    // repaired mapping stays in the field other callers expect.
                    v.ValueLabels = "";
                    labelSets++;
                }
                if (typeChanged) typeRepairs++;
            }

            // Persist the sheet (CommitGrid + stamp + save). Changing sheet
            // metadata changes the statistics fingerprint, so any prior results
            // become stale automatically.
            PersistExtraction(p);

            MagicFixReviewOverlay.Visibility = Visibility.Collapsed;
            _magicFixReview.Clear();

            // Rerun readiness and refresh the whole dashboard on the new sheet.
            RefreshStatisticsTab();

            int stillNeedReview = _statReadiness?.Blockers.Count ?? 0;
            ShowToast("Magic Fix updated the extraction sheet. Statistics readiness has been refreshed. "
                + $"{typeRepairs} variable{(typeRepairs == 1 ? "" : "s")} repaired, {labelSets} value-label set{(labelSets == 1 ? "" : "s")} updated, "
                + $"{stillNeedReview} issue{(stillNeedReview == 1 ? "" : "s")} still need review.");
        }
        catch (Exception ex) { HandleDxError("apply Magic Fix repairs", ex); }
    }

    // Rebuilds the live grid collection from the project's variables (used after
    // Magic Fix edits the ResearchVariable instances directly).
    private void CommitGridFromProject(ResearchProject p)
    {
        _extractionVariables.Clear();
        foreach (var v in p.Variables ?? new List<ResearchVariable>())
            _extractionVariables.Add(v);
    }

    // =====================================================================
    // Research Lab — generic confirm dialog (reused across 2E/2F/2G flows)
    // =====================================================================

    private Action? _rlConfirmCancelAction;

    private void ShowRlConfirm(string title, string message, string okLabel, Action onConfirm, Action? onCancel = null, string cancelLabel = "Cancel")
    {
        RlConfirmTitle.Text = title;
        RlConfirmMessage.Text = message;
        RlConfirmOkText.Text = okLabel;
        RlConfirmCancelText.Text = cancelLabel;
        _rlConfirmAction = onConfirm;
        _rlConfirmCancelAction = onCancel;
        RlConfirmOverlay.Visibility = Visibility.Visible;
        FadeIn(RlConfirmOverlay, 150);
    }

    private void RlConfirmOk_Click(object sender, RoutedEventArgs e)
    {
        RlConfirmOverlay.Visibility = Visibility.Collapsed;
        var action = _rlConfirmAction;
        _rlConfirmAction = null;
        _rlConfirmCancelAction = null;
        action?.Invoke();
    }

    private void RlConfirmCancel_Click(object sender, RoutedEventArgs e)
    {
        RlConfirmOverlay.Visibility = Visibility.Collapsed;
        var cancelAction = _rlConfirmCancelAction;
        _rlConfirmAction = null;
        _rlConfirmCancelAction = null;
        cancelAction?.Invoke();
    }

    // ---- Small helpers ----------------------------------------------------

    private static string JoinLines(List<string> items)
        => items is { Count: > 0 } ? string.Join(Environment.NewLine, items) : "";

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (var v in values)
            if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
        return "";
    }

    private readonly struct ActivationInfo
    {
        public ActivationInfo(string plan, int days, bool lifetime)
        {
            Plan = plan;
            Days = days;
            Lifetime = lifetime;
        }

        public string Plan { get; }
        public int Days { get; }
        public bool Lifetime { get; }
    }

    private ActivationInfo? GetActivationInfo(string code)
    {
        return code switch
        {
            "FLASH-MONTH-2026" => new ActivationInfo("Monthly", 30, false),
            "FLASH-YEAR-2026" => new ActivationInfo("Yearly", 365, false),
            "FLASH-LIFE-2026" => new ActivationInfo("Lifetime", 0, true),
            _ => null
        };
    }

    private bool IsCodeUsed(string code)
    {
        return _store.UsedCodes.Any(x =>
            string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase));
    }

    private string GetAccountSummary()
    {
        if (_currentUser is null)
            return "";

        // Phase 3A UI/stub: product-appropriate account chip. Real plan/expiry wording
        // returns when licensing is wired (Phase 6-7); account fields/persistence unchanged.
        return $"{Branding.CommercialBetaPlan}  •  Active";
    }

    private static string FormatExpiry(DateTime expiry)
    {
        if (expiry.Year > 9000)
            return "Lifetime";

        return expiry.ToLocalTime().ToString("yyyy-MM-dd");
    }

    private static string NormalizeEmail(string email)
    {
        return email.Trim().ToLowerInvariant();
    }

    private static string NormalizeCode(string code)
    {
        return code.Trim().ToUpperInvariant().Replace(" ", "");
    }

    private static string HashPassword(string email, string password)
    {
        using var sha = SHA256.Create();
        byte[] bytes = Encoding.UTF8.GetBytes(email + "::" + password + "::AIFlashcardMakerLocalDemo");
        return Convert.ToHexString(sha.ComputeHash(bytes));
    }

    private static string SafeComboText(ComboBox combo)
    {
        return combo.SelectedItem?.ToString() ?? "";
    }

    private static string CleanFileName(string value)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            value = value.Replace(c, '_');

        return string.IsNullOrWhiteSpace(value) ? "deck" : value.Trim();
    }

    private void ShowLoading(string message)
    {
        LoadingMessageText.Text = message;

        LoadingOverlay.Visibility = Visibility.Visible;
        LoadingOverlay.Opacity = 0;

        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160));
        LoadingOverlay.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    private void HideLoading()
    {
        if (LoadingOverlay.Visibility != Visibility.Visible)
            return;

        var fade = new DoubleAnimation(LoadingOverlay.Opacity, 0, TimeSpan.FromMilliseconds(140));

        fade.Completed += (_, _) =>
        {
            LoadingOverlay.Visibility = Visibility.Collapsed;
            LoadingOverlay.Opacity = 1;
        };

        LoadingOverlay.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    private static void FadeIn(UIElement element, int milliseconds)
    {
        element.Opacity = 0;

        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = new CubicEase
            {
                EasingMode = EasingMode.EaseOut
            }
        };

        element.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    private void InitializeGifAnimations()
    {
        AnimateGif(SidebarStreakGif, "streakfire.gif", 85);
        AnimateGif(DashboardStudyGif, "Study.gif", 85);
        AnimateGif(DashboardStreakGif, "streakfire.gif", 85);
        AnimateGif(DashboardSuccessGif, "Success.gif", 85);

        AnimateGif(ImportUploadGif, "Upload.gif", 85);
        AnimateGif(LoadingGifImage, "Loading.gif", 75);
        AnimateGif(StudyCompleteGif, "Success.gif", 85);
    }

    private static void AnimateGif(Image image, string fileName, int frameMilliseconds)
    {
        try
        {
            var uri = new Uri(
                $"pack://application:,,,/Assets/Animations/{fileName}",
                UriKind.Absolute);

            var decoder = new GifBitmapDecoder(
                uri,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);

            if (decoder.Frames.Count == 0)
                return;

            if (decoder.Frames.Count == 1)
            {
                image.Source = decoder.Frames[0];
                return;
            }

            var animation = new ObjectAnimationUsingKeyFrames
            {
                RepeatBehavior = RepeatBehavior.Forever,
                Duration = new Duration(TimeSpan.FromMilliseconds(frameMilliseconds * decoder.Frames.Count))
            };

            for (int i = 0; i < decoder.Frames.Count; i++)
            {
                animation.KeyFrames.Add(
                    new DiscreteObjectKeyFrame(
                        decoder.Frames[i],
                        KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(frameMilliseconds * i))));
            }

            image.BeginAnimation(Image.SourceProperty, animation);
        }
        catch
        {
            // If a GIF fails to load, the app should still run.
        }
    }

    private void SetStatus(string message)
    {
        StatusText.Text = message;
    }

    private enum ToastKind { Info, Success, Error }

    private void ShowToast(string message)
    {
        // Keep the status bar in sync so nothing is lost if a toast is missed.
        SetStatus(message);

        try
        {
            ToastKind kind = InferToastKind(message);

            Brush accent = kind switch
            {
                ToastKind.Success => (Brush)FindResource("SuccessBrush"),
                ToastKind.Error => (Brush)FindResource("DangerBrush"),
                _ => (Brush)FindResource("PrimaryBrush")
            };

            var accentBar = new Border
            {
                Width = 4,
                CornerRadius = new CornerRadius(2),
                Background = accent,
                Margin = new Thickness(0, 0, 12, 0)
            };

            var text = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)FindResource("TextBrush"),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(accentBar);
            row.Children.Add(text);

            var card = new Border
            {
                Background = (Brush)FindResource("CardBrush"),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(16, 12, 16, 12),
                Margin = new Thickness(0, 0, 0, 10),
                MinWidth = 240,
                MaxWidth = 380,
                Child = row,
                Effect = new DropShadowEffect
                {
                    Color = Color.FromRgb(0x56, 0x6A, 0x7F),
                    BlurRadius = 18,
                    ShadowDepth = 2,
                    Opacity = 0.18
                }
            };

            ToastHost.Children.Add(card);

            card.Opacity = 0;
            card.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));

            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.4) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();

                var fade = new DoubleAnimation(card.Opacity, 0, TimeSpan.FromMilliseconds(220));
                fade.Completed += (_, _) => ToastHost.Children.Remove(card);
                card.BeginAnimation(UIElement.OpacityProperty, fade);
            };
            timer.Start();
        }
        catch
        {
            // Toasts are cosmetic; never let them break a user flow.
        }
    }

    private static ToastKind InferToastKind(string message)
    {
        string m = message.ToLowerInvariant();

        string[] errorWords =
        {
            "invalid", "wrong", "not found", "expired", "failed", "no cards",
            "no more", "must ", "enter ", "select ", "paste ", "already",
            " first", "valid email", "at least", "no deck", "no card"
        };

        foreach (var w in errorWords)
            if (m.Contains(w))
                return ToastKind.Error;

        string[] successWords =
        {
            "created", "copied", "imported", "generated", "saved",
            "exported", "renamed", "moved", "applied", "success",
            "complete", "deleted"
        };

        foreach (var w in successWords)
            if (m.Contains(w))
                return ToastKind.Success;

        return ToastKind.Info;
    }
}
