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

public sealed class AppSettings
{
    public string ApiProvider { get; set; } = "Z.ai";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "GLM-4.7-FlashX";
    public string BaseUrl { get; set; } = "https://api.z.ai/api/paas/v4";
}

public sealed partial class MainWindow : Window
{
    private readonly string dataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AIFlashcardMaker");

    private string StorePath => Path.Combine(dataDir, "accounts.json");
    private string SettingsPath => Path.Combine(dataDir, "settings.json");
    private string DecksPath => Path.Combine(dataDir, "decks.json");
    private string ResearchPath => Path.Combine(dataDir, "research_projects.json");

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
    private string ResearchAiConfigPath => Path.Combine(dataDir, "research_ai_config.json");

    // Research Lab (Phase 2E/F) — import + generic confirm state.
    // Guards for pasted/uploaded proposal text length (protects the model call
    // and keeps the UI responsive). The proposal text is never logged or printed.
    private const int ImportProposalMaxChars = 24000;
    private const int ImportProposalMinChars = 40;
    private const long ImportProposalMaxFileBytes = 5 * 1024 * 1024;   // 5 MB
    private ProposalExtractionResult? _currentExtraction;
    private string _lastImportText = "";
    // Text read from a chosen file, kept separate from the paste box so that a
    // non-empty paste always takes precedence at analyze time.
    private string _importedFileText = "";
    private string _importedFileError = "";
    private Action? _rlConfirmAction;

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

        // Never open larger than the usable screen area (keeps the window on
        // small laptops and clear of the taskbar).
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);

        BuildNavMap();
        BuildResearchTabMap();
        InitializeGifAnimations();

        Directory.CreateDirectory(dataDir);

        LoadStore();
        LoadSettings();
        LoadDecks();
        LoadResearch();
        LoadResearchAiConfig();
        // The development provider (dev-only) reuses the API key the user already
        // saved for flashcards. It is read live and never modified, logged, or
        // shown. In production the app uses the backend endpoint instead.
        _researchAi = new ResearchAiService(
            () => _researchAiOptions,
            () => new ZaiDevCredentials
            {
                ApiKey = _settings.ApiKey,
                BaseUrl = _settings.BaseUrl,
                Model = _settings.Model
            });
        PopulateResearchAiSettings();
        EnsureDefaultDeck();
        SetupCombos();

        ShowAuth();
        RefreshAll();
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

    private void ShowApp()
    {
        HideLoading();

        AuthGrid.Visibility = Visibility.Collapsed;
        AppGrid.Visibility = Visibility.Visible;

        FadeIn(AppGrid, 240);

        UserSummaryText.Text = GetAccountSummary();

        RefreshAll();
        ShowPage(PageDashboard);
        SetStatus("Logged in successfully.");
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

    private void Login_Click(object sender, RoutedEventArgs e)
    {
        string email = NormalizeEmail(LoginEmailBox.Text);
        string password = LoginPasswordBox.Password;

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            ShowToast("Enter email and password.");
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

    private void Signup_Click(object sender, RoutedEventArgs e)
    {
        string email = NormalizeEmail(SignupEmailBox.Text);
        string password = SignupPasswordBox.Password;
        string code = NormalizeCode(SignupCodeBox.Text);

        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
        {
            ShowToast("Enter a valid email.");
            return;
        }

        if (password.Length < 4)
        {
            ShowToast("Password must be at least 4 characters for this demo.");
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
    }

    private void Logout_Click(object sender, RoutedEventArgs e)
    {
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
        ApiKeyBox.Password = _settings.ApiKey;
        ModelBox.Text = string.IsNullOrWhiteSpace(_settings.Model) ? "GLM-4.7-FlashX" : _settings.Model;
        BaseUrlBox.Text = string.IsNullOrWhiteSpace(_settings.BaseUrl) ? "https://api.z.ai/api/paas/v4" : _settings.BaseUrl;
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

        if (string.IsNullOrWhiteSpace(_settings.ApiKey))
        {
            ShowToast("Add your Z.ai API key in AI Settings first.");
            SettingsPage_Click(sender, e);
            return;
        }

        try
        {
            ShowLoading("Generating with Z.ai...");
            SetStatus("Generating flashcards with Z.ai...");
            IsEnabled = false;

            string prompt = BuildPrompt(source);
            string aiText = await CallZaiAsync(prompt);
            var parsed = ParseFlashcards(aiText);

            if (parsed.Count == 0)
            {
                ImportBox.Text = aiText;
                ShowToast("Z.ai replied, but no cards were parsed. Response placed in Import JSON.");
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

    // Global top-bar search: mirror the query into the My Decks card filter so results
    // are live, and jump to My Decks on Enter. Never mutates data, only filters.
    private void TopSearch_Changed(object sender, RoutedEventArgs e)
    {
        if (_currentUser is null)
            return;

        // Setting SearchBox.Text raises SearchFilter_Changed, which refreshes the lists.
        if (!string.Equals(SearchBox.Text, TopSearchBox.Text, StringComparison.Ordinal))
            SearchBox.Text = TopSearchBox.Text;
    }

    private void TopSearch_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter)
            return;

        if (string.IsNullOrWhiteSpace(TopSearchBox.Text))
        {
            ShowToast("Type something to search decks and cards.");
            return;
        }

        SearchBox.Text = TopSearchBox.Text;
        ShowPage(PageDecks);
        RefreshCardLists();
        SetStatus($"Showing cards matching “{TopSearchBox.Text.Trim()}”.");
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

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        _settings.ApiKey = ApiKeyBox.Password.Trim();
        _settings.Model = string.IsNullOrWhiteSpace(ModelBox.Text) ? "GLM-4.7-FlashX" : ModelBox.Text.Trim();
        _settings.BaseUrl = string.IsNullOrWhiteSpace(BaseUrlBox.Text)
            ? "https://api.z.ai/api/paas/v4"
            : BaseUrlBox.Text.Trim().TrimEnd('/');

        SaveSettings();
        ShowToast("AI settings saved locally.");
        SetStatus("AI settings saved.");
    }

    private void ClearKey_Click(object sender, RoutedEventArgs e)
    {
        ApiKeyBox.Clear();
        _settings.ApiKey = "";
        SaveSettings();
        SetStatus("API key cleared.");
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

    private async Task<string> CallZaiAsync(string prompt)
    {
        string baseUrl = string.IsNullOrWhiteSpace(_settings.BaseUrl)
            ? "https://api.z.ai/api/paas/v4"
            : _settings.BaseUrl.Trim().TrimEnd('/');

        string model = string.IsNullOrWhiteSpace(_settings.Model)
            ? "GLM-4.7-FlashX"
            : _settings.Model.Trim();

        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "system",
                    ["content"] = "You create JSON flashcards only. Return valid JSON only."
                },
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = prompt
                }
            },
            ["temperature"] = 0.2,
            ["max_tokens"] = 6000,
            ["stream"] = false
        };

        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(3)
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey.Trim());
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request);
        string json = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            throw new Exception("Z.ai API error:\n\n" + TrimForMessage(json));

        string text = ExtractChatCompletionText(json);

        if (string.IsNullOrWhiteSpace(text))
            throw new Exception("Z.ai returned no usable text:\n\n" + TrimForMessage(json));

        return text;
    }

    private static string ExtractChatCompletionText(string json)
    {
        var root = JsonNode.Parse(json);
        var choices = root?["choices"]?.AsArray();

        if (choices is null || choices.Count == 0)
            return "";

        string? content = choices[0]?["message"]?["content"]?.GetValue<string>();

        return content?.Trim() ?? "";
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
        AccountPlanText.Text = string.IsNullOrWhiteSpace(_currentUser?.Plan) ? "—" : _currentUser!.Plan;
        AccountExpiryText.Text = FormatExpiry(_currentUser?.SubscriptionExpiresAt ?? DateTime.UtcNow);
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

            if (string.Equals(_settings.Model, "gpt-4o-mini", StringComparison.OrdinalIgnoreCase))
                _settings.Model = "GLM-4.7-FlashX";

            if (string.IsNullOrWhiteSpace(_settings.Model))
                _settings.Model = "GLM-4.7-FlashX";

            if (string.IsNullOrWhiteSpace(_settings.BaseUrl))
                _settings.BaseUrl = "https://api.z.ai/api/paas/v4";
        }
        catch
        {
            _settings = new AppSettings();
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
                return;
            }

            string json = File.ReadAllText(ResearchPath);
            _researchData = JsonSerializer.Deserialize<ResearchLabData>(json) ?? new ResearchLabData();
            _researchData.Projects ??= new List<ResearchProject>();
        }
        catch
        {
            // Corrupt or unreadable file: fail safely with an empty list and
            // never touch the user's flashcard data.
            _researchData = new ResearchLabData();
            SetStatus("Research projects file could not be read — starting with an empty list.");
        }
    }

    private void SaveResearch()
    {
        try
        {
            File.WriteAllText(ResearchPath, JsonSerializer.Serialize(_researchData, new JsonSerializerOptions
            {
                WriteIndented = true
            }));
        }
        catch (Exception ex)
        {
            ShowToast("Could not save research projects: " + ex.Message);
        }
    }

    private void ResearchPage_Click(object sender, RoutedEventArgs e)
    {
        CreateProjectOverlay.Visibility = Visibility.Collapsed;
        DeleteConfirmOverlay.Visibility = Visibility.Collapsed;
        RefreshResearchProjects();
        ShowPage(PageResearch);
    }

    private void RefreshResearchProjects()
    {
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
        if (_researchData.Projects.Count >= ResearchProjectLimit)
        {
            ShowToast("You reached the current limit of 2 research projects. Additional research projects will be available with an upgrade later.");
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

    private void CreateProject_Click(object sender, RoutedEventArgs e)
    {
        // Safety re-check of the limit before committing.
        if (_researchData.Projects.Count >= ResearchProjectLimit)
        {
            CreateProjectOverlay.Visibility = Visibility.Collapsed;
            ShowToast("You reached the current limit of 2 research projects. Additional research projects will be available with an upgrade later.");
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
            ProgressPercent = 15,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _researchData.Projects.Add(project);
        SaveResearch();

        CreateProjectOverlay.Visibility = Visibility.Collapsed;
        RefreshResearchProjects();
        ShowToast($"Research project created: {project.Title}");
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

    private void DeleteProject_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string id) return;

        var project = _researchData.Projects.FirstOrDefault(p => p.Id == id);
        if (project is null) return;

        _pendingDeleteResearchId = id;
        DeleteConfirmText.Text = $"“{project.Title}” will be permanently removed along with its saved details. This cannot be undone.";
        DeleteConfirmOverlay.Visibility = Visibility.Visible;
        FadeIn(DeleteConfirmOverlay, 150);
    }

    private void CancelDelete_Click(object sender, RoutedEventArgs e)
    {
        _pendingDeleteResearchId = "";
        DeleteConfirmOverlay.Visibility = Visibility.Collapsed;
    }

    private void ConfirmDelete_Click(object sender, RoutedEventArgs e)
    {
        DeleteConfirmOverlay.Visibility = Visibility.Collapsed;

        if (string.IsNullOrEmpty(_pendingDeleteResearchId)) return;

        var project = _researchData.Projects.FirstOrDefault(p => p.Id == _pendingDeleteResearchId);
        _pendingDeleteResearchId = "";
        if (project is null) return;

        _researchData.Projects.Remove(project);
        SaveResearch();
        RefreshResearchProjects();
        ShowToast("Research project deleted.");
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

        AiRecError.Visibility = Visibility.Collapsed;
        AiRecNotConfigured.Visibility = Visibility.Collapsed;
        GenerateRecBtn.IsEnabled = false;
        ShowLoading("Research AI is analyzing your project...");

        try
        {
            var rec = await _researchAi.GenerateRecommendationsAsync(p, CancellationToken.None);
            p.Recommendations = rec;
            // Fresh recommendations now reflect the current details, so clear the
            // "details changed" nudge.
            p.DetailsChangedSinceRecommendations = false;
            p.UpdatedAt = DateTime.UtcNow;
            SaveResearch();

            RenderRecommendations(rec);
            PopulatePlanEditor(p);
            UpdateOverviewProgress(p);
            UpdateProposalPlanSource(p);
            UpdateGenerateRecButton(p);
            UpdateAiRecImportedBadge(p);
            ShowToast("Recommendations generated successfully.");
        }
        catch (ResearchAiNotConfiguredException)
        {
            ShowResearchAiNotConfigured();
            ShowToast("Research AI service is not configured yet.");
        }
        catch (ResearchAiException)
        {
            ShowAiRecError("Research AI could not generate recommendations. Check your Research AI settings and try again.");
        }
        catch (Exception)
        {
            ShowAiRecError("Research AI could not generate recommendations. Check your Research AI settings and try again.");
        }
        finally
        {
            HideLoading();
            GenerateRecBtn.IsEnabled = true;
        }
    }

    private void ShowResearchAiNotConfigured()
    {
        AiRecNotConfigured.Visibility = Visibility.Visible;
        AiRecError.Visibility = Visibility.Collapsed;
    }

    private void ShowAiRecError(string message)
    {
        AiRecErrorText.Text = message;
        AiRecError.Visibility = Visibility.Visible;
        AiRecNotConfigured.Visibility = Visibility.Collapsed;
    }

    private void UpdateGenerateRecButton(ResearchProject p)
    {
        bool has = p.Recommendations is not null &&
                   (p.Recommendations.HasStructuredContent || !string.IsNullOrWhiteSpace(p.Recommendations.RawAiText));
        GenerateRecBtnText.Text = has ? "Regenerate AI Recommendations" : "Generate AI Recommendations";
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
        p.ProgressPercent = Math.Max(p.ProgressPercent, 30);
        p.UpdatedAt = DateTime.UtcNow;
        SaveResearch();

        PopulateOverview(p);
        PopulatePlanEditor(p);
        UpdateOverviewProgress(p);
        UpdateProposalPlanSource(p);
        ShowToast("Recommendations accepted. Your research plan is ready to edit below.");
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
    // "no accepted research plan" status card.
    private async void GenerateBasicProposal_Click(object sender, RoutedEventArgs e)
    {
        var p = CurrentResearchProject();
        if (p is null) { ShowToast("Open a project first."); return; }
        await GenerateProposalCore(p);
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
        ShowLoading("Research AI is drafting your proposal...");

        try
        {
            // Pass the accepted recommendations so the draft is built from the
            // accepted research plan (title, question, design, objectives,
            // variables, analyses, criteria, ethics, limitations, next steps).
            var accepted = HasAcceptedResearchPlan(p) ? p.Recommendations : null;
            var draft = await _researchAi.GenerateProposalDraftAsync(p, accepted, CancellationToken.None);
            p.ProposalDraft = draft;
            p.CurrentStage = "Proposal drafted";
            p.ProgressPercent = Math.Max(p.ProgressPercent, 45);
            p.UpdatedAt = DateTime.UtcNow;
            SaveResearch();

            PopulateProposalEditor(p);
            PopulateOverview(p);
            UpdateOverviewProgress(p);
            ShowToast("Proposal draft generated successfully.");
        }
        catch (ResearchAiNotConfiguredException)
        {
            ShowProposalNotConfigured();
            ShowToast("Research AI service is not configured yet.");
        }
        catch (ResearchAiException)
        {
            ShowProposalError("Research AI could not generate the proposal draft. Check your Research AI settings and try again.");
        }
        catch (Exception)
        {
            ShowProposalError("Research AI could not generate the proposal draft. Check your Research AI settings and try again.");
        }
        finally
        {
            HideLoading();
            // Restore the button to the correct state for the current plan status
            // (enabled only when an accepted research plan exists).
            UpdateProposalPlanSource(p);
        }
    }

    private static bool HasAcceptedResearchPlan(ResearchProject p)
        => p.Recommendations?.AcceptedIntoPlan == true;

    // Shows the correct "Research Plan Source" status card in the Proposal tab
    // and toggles the primary Generate button accordingly.
    private void UpdateProposalPlanSource(ResearchProject p)
    {
        bool accepted = HasAcceptedResearchPlan(p);
        ProposalPlanSourceAccepted.Visibility = accepted ? Visibility.Visible : Visibility.Collapsed;
        ProposalPlanSourceMissing.Visibility = accepted ? Visibility.Collapsed : Visibility.Visible;
        GenerateProposalBtn.IsEnabled = accepted;
        GenerateProposalBtnText.Text = accepted
            ? "Generate Proposal Draft"
            : "Accept a research plan first";
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
        p.ProgressPercent = Math.Max(p.ProgressPercent, 45);
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
        SetListSection(RecVariablesList, RecVariablesSec, rec.SuggestedVariables);
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

    private void PopulateResearchAiSettings()
    {
        RaiEndpointBox.Text = _researchAiOptions.EndpointBaseUrl;
        RaiTimeoutBox.Text = _researchAiOptions.TimeoutSeconds.ToString();
        RaiMockToggle.IsChecked = _researchAiOptions.UseDevelopmentMock;
        RaiDevProviderToggle.IsChecked = _researchAiOptions.UseDevelopmentZaiProvider;
    }

    private void SaveResearchAiConfig_Click(object sender, RoutedEventArgs e)
    {
        _researchAiOptions.EndpointBaseUrl = RaiEndpointBox.Text.Trim();

        if (int.TryParse(RaiTimeoutBox.Text.Trim(), out int seconds))
            _researchAiOptions.TimeoutSeconds = Math.Clamp(seconds, 5, 300);
        else
            _researchAiOptions.TimeoutSeconds = 180;

        _researchAiOptions.UseDevelopmentMock = RaiMockToggle.IsChecked == true;
        _researchAiOptions.UseDevelopmentZaiProvider = RaiDevProviderToggle.IsChecked == true;

        SaveResearchAiConfig();
        PopulateResearchAiSettings();

        // Reflect the new configuration if a project is currently open.
        if (CurrentResearchProject() is not null)
            UpdateResearchAiAvailability();

        ShowToast(_researchAiOptions.IsConfigured
            ? "Research AI settings saved."
            : "Research AI settings saved. Add an endpoint or enable development mock to generate inside the app.");
    }

    private void OpenResearchAiSettings_Click(object sender, RoutedEventArgs e)
    {
        PopulateResearchAiSettings();
        ShowPage(PageSettings);
    }

    // Shows/hides the "not configured" panels for the open project's AI tabs.
    private void UpdateResearchAiAvailability()
    {
        bool configured = _researchAi.IsConfigured;
        AiRecNotConfigured.Visibility = configured ? Visibility.Collapsed : Visibility.Visible;
        ProposalNotConfigured.Visibility = configured ? Visibility.Collapsed : Visibility.Visible;
        AiRecError.Visibility = Visibility.Collapsed;
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

        ImportPasteBox.Text = "";
        ImportFileNameText.Text = "No file chosen";
        _importedFileText = "";
        _importedFileError = "";
        ImportValidationText.Visibility = Visibility.Collapsed;
        ImportProposalOverlay.Visibility = Visibility.Visible;
        FadeIn(ImportProposalOverlay, 160);
        ImportPasteBox.Focus();
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

        // Paragraph and line breaks -> newlines; tabs -> spaces.
        xml = Regex.Replace(xml, "</w:p>", "\n", RegexOptions.IgnoreCase);
        xml = Regex.Replace(xml, "<w:br[^>]*/>", "\n", RegexOptions.IgnoreCase);
        xml = Regex.Replace(xml, "<w:tab[^>]*/>", "\t", RegexOptions.IgnoreCase);

        // Drop every remaining tag, then decode XML entities.
        string text = Regex.Replace(xml, "<[^>]+>", "");
        text = System.Net.WebUtility.HtmlDecode(text);

        // Collapse excessive blank lines.
        text = Regex.Replace(text, "\n{3,}", "\n\n");
        return text.Trim();
    }

    private void ShowImportValidation(string message)
    {
        ImportValidationText.Text = message;
        ImportValidationText.Visibility = Visibility.Visible;
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

        ImportValidationText.Visibility = Visibility.Collapsed;
        AnalyzeProposalBtn.IsEnabled = false;
        ShowLoading("Research AI is analyzing your proposal...");

        try
        {
            var result = await _researchAi.ExtractProposalAsync(text, p, CancellationToken.None);
            _currentExtraction = result;
            _lastImportText = text;

            ImportProposalOverlay.Visibility = Visibility.Collapsed;
            ShowReviewScreen(result);
        }
        catch (ResearchAiNotConfiguredException)
        {
            ShowImportValidation("Research AI is not configured yet. Add an endpoint or enable a development provider in Settings.");
        }
        catch (ResearchAiException ex)
        {
            // Surface the specific, key-free reason (timed out, could not be
            // parsed, provider error) instead of one generic message.
            ShowImportValidation(ex.Message);
        }
        catch (Exception)
        {
            ShowImportValidation("Research AI extraction failed. Please try again.");
        }
        finally
        {
            HideLoading();
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
        // Reopen the import dialog, keeping the last pasted text for a quick retry.
        ReviewExtractionOverlay.Visibility = Visibility.Collapsed;
        ImportPasteBox.Text = _lastImportText;
        ImportFileNameText.Text = "No file chosen";
        ImportValidationText.Visibility = Visibility.Collapsed;
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

        // --- Stage / progress ---
        p.DetailsChangedSinceRecommendations = false;
        if (p.ProposalDraft is not null)
        {
            p.CurrentStage = markAccepted ? "Proposal imported" : "Proposal draft imported";
            p.ProgressPercent = Math.Max(p.ProgressPercent, markAccepted ? 45 : 40);
        }
        else if (p.Recommendations is not null)
        {
            p.CurrentStage = markAccepted ? "Research plan ready" : "Recommendations imported";
            p.ProgressPercent = Math.Max(p.ProgressPercent, markAccepted ? 30 : 25);
        }
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

        return $"{_currentUser.Email}  •  {_currentUser.Plan}  •  {FormatExpiry(_currentUser.SubscriptionExpiresAt)}";
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

    private static string TrimForMessage(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        return text.Length <= 1600 ? text : text[..1600] + "...";
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
