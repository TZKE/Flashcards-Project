using Microsoft.Win32;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

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

    private LocalStore _store = new();
    private AppSettings _settings = new();
    private DeckStore _deckStore = new();

    private LocalAccount? _currentUser;
    private string _activeDeckId = "";
    private string _currentCardId = "";

    private readonly List<Flashcard> _studyQueue = new();
    private int _studyIndex = -1;
    private bool _answerShown;
    private bool _suppressSelection;

    public MainWindow()
    {
        InitializeComponent();

        Directory.CreateDirectory(dataDir);

        LoadStore();
        LoadSettings();
        LoadDecks();
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
        AuthGrid.Visibility = Visibility.Visible;
        AppGrid.Visibility = Visibility.Collapsed;
    }

    private void ShowApp()
    {
        AuthGrid.Visibility = Visibility.Collapsed;
        AppGrid.Visibility = Visibility.Visible;

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
        page.Opacity = 0;

        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180));
        page.BeginAnimation(OpacityProperty, fade);

        RefreshAll();
    }

    private void Login_Click(object sender, RoutedEventArgs e)
    {
        string email = NormalizeEmail(LoginEmailBox.Text);
        string password = LoginPasswordBox.Password;

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            MessageBox.Show("Enter email and password.");
            return;
        }

        var user = _store.Accounts.FirstOrDefault(x =>
            string.Equals(x.Email, email, StringComparison.OrdinalIgnoreCase));

        if (user is null)
        {
            MessageBox.Show("Account not found.");
            return;
        }

        if (user.PasswordHash != HashPassword(email, password))
        {
            MessageBox.Show("Wrong password.");
            return;
        }

        if (DateTime.UtcNow > user.SubscriptionExpiresAt)
        {
            MessageBox.Show("Your activation expired.");
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
            MessageBox.Show("Enter a valid email.");
            return;
        }

        if (password.Length < 4)
        {
            MessageBox.Show("Password must be at least 4 characters for this demo.");
            return;
        }

        if (_store.Accounts.Any(x => string.Equals(x.Email, email, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show("This email already has an account.");
            return;
        }

        var activation = GetActivationInfo(code);

        if (activation is null)
        {
            MessageBox.Show("Invalid activation code.");
            return;
        }

        if (IsCodeUsed(code))
        {
            MessageBox.Show("This activation code was already used.");
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
            MessageBox.Show("Paste notes first.");
            return;
        }

        var deck = GetActiveDeck();

        if (deck is null)
        {
            MessageBox.Show("Create or select a deck first.");
            ShowPage(PageDecks);
            return;
        }

        if (string.IsNullOrWhiteSpace(_settings.ApiKey))
        {
            MessageBox.Show("Add your Z.ai API key in AI Settings first.");
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
                MessageBox.Show("Z.ai replied, but no cards were parsed. Response placed in Import JSON.");
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

            MessageBox.Show($"Generated {parsed.Count} cards into deck: {deck.Name}");
            ShowPage(PagePreview);
            SetStatus($"Generated {parsed.Count} cards into {deck.Name}.");
        }
        catch (Exception ex)
        {
            MessageBox.Show("Generation failed:\n\n" + ex.Message);
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
            MessageBox.Show("Paste notes first.");
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
            MessageBox.Show("Create a prompt first.");
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
            MessageBox.Show("Paste JSON first.");
            return;
        }

        var deck = GetActiveDeck();

        if (deck is null)
        {
            MessageBox.Show("Create or select a deck first.");
            ShowPage(PageDecks);
            return;
        }

        var parsed = ParseFlashcards(text);

        if (parsed.Count == 0)
        {
            MessageBox.Show("No cards found. Make sure the AI returned valid JSON with front/back/tags.");
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

        MessageBox.Show($"Imported {parsed.Count} cards into deck: {deck.Name}");
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
            MessageBox.Show("Enter a deck name first.");
            return;
        }

        if (_deckStore.Decks.Any(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show("A deck with this name already exists.");
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

        MessageBox.Show($"Deck created: {deck.Name}");
    }

    private void RenameDeck_Click(object sender, RoutedEventArgs e)
    {
        var deck = GetActiveDeck();

        if (deck is null)
        {
            MessageBox.Show("Select a deck first.");
            return;
        }

        string name = DeckNameBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show("Type the new deck name in the box.");
            return;
        }

        if (_deckStore.Decks.Any(d => d.Id != deck.Id && string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show("Another deck already has this name.");
            return;
        }

        deck.Name = name;
        DeckNameBox.Clear();

        SaveDecks();
        RefreshAll();

        MessageBox.Show("Deck renamed.");
    }

    private void DeleteDeck_Click(object sender, RoutedEventArgs e)
    {
        var deck = GetActiveDeck();

        if (deck is null)
        {
            MessageBox.Show("Select a deck first.");
            return;
        }

        if (_deckStore.Decks.Count <= 1)
        {
            MessageBox.Show("You must keep at least one deck.");
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

        MessageBox.Show("Deck deleted.");
    }

    private void SearchFilter_Changed(object sender, RoutedEventArgs e)
    {
        RefreshCardLists();
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

        MessageBox.Show("Select a card first.");
    }

    private void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        if (CardsList.SelectedItem is Flashcard card)
        {
            _currentCardId = card.Id;
            DeleteCurrentCard();
            return;
        }

        MessageBox.Show("Select a card first.");
    }

    private void MoveSelected_Click(object sender, RoutedEventArgs e)
    {
        var sourceDeck = GetActiveDeck();

        if (sourceDeck is null)
        {
            MessageBox.Show("Select a source deck first.");
            return;
        }

        if (CardsList.SelectedItem is not Flashcard card)
        {
            MessageBox.Show("Select a card first.");
            return;
        }

        if (MoveDeckCombo.SelectedItem is not StudyDeck targetDeck)
        {
            MessageBox.Show("Select target deck.");
            return;
        }

        if (targetDeck.Id == sourceDeck.Id)
        {
            MessageBox.Show("This card is already in that deck.");
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

        MessageBox.Show($"Moved card to {targetDeck.Name}.");
    }

    private void Previous_Click(object sender, RoutedEventArgs e) => NavigateCard(-1);
    private void Next_Click(object sender, RoutedEventArgs e) => NavigateCard(1);
    private void SaveCard_Click(object sender, RoutedEventArgs e) => SaveCard();
    private void DeleteCard_Click(object sender, RoutedEventArgs e) => DeleteCurrentCard();
    private void CopyCurrent_Click(object sender, RoutedEventArgs e) => CopyCurrent();
    private void CopyAll_Click(object sender, RoutedEventArgs e) => CopyAllDecksToClipboard();
    private void ExportTxt_Click(object sender, RoutedEventArgs e) => ExportSelectedDeck_Click(sender, e);

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
            MessageBox.Show("Selected deck has no cards.");
            return;
        }

        Clipboard.SetText(text);
        SetStatus("Selected deck copied.");
        MessageBox.Show("Selected deck copied for Anki.");
    }

    private void CopyAllDecksToClipboard()
    {
        string text = GetAllDecksAnkiText();

        if (string.IsNullOrWhiteSpace(text))
        {
            MessageBox.Show("No cards to copy.");
            return;
        }

        Clipboard.SetText(text);
        SetStatus("All decks copied.");
        MessageBox.Show("All decks copied for Anki.");
    }

    private void ExportSelectedDeck_Click(object sender, RoutedEventArgs e)
    {
        SaveCurrentEdits();

        var deck = GetActiveDeck();

        if (deck is null || deck.Cards.Count == 0)
        {
            MessageBox.Show("Selected deck has no cards.");
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
            MessageBox.Show("Selected deck exported.");
        }
    }

    private void ExportAllDecks_Click(object sender, RoutedEventArgs e)
    {
        SaveCurrentEdits();

        string text = GetAllDecksAnkiText();

        if (string.IsNullOrWhiteSpace(text))
        {
            MessageBox.Show("No cards to export.");
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
            MessageBox.Show("All decks exported.");
        }
    }

    private void StartStudySession(string deckId)
    {
        var deck = GetDeckById(deckId) ?? GetActiveDeck();

        if (deck is null || deck.Cards.Count == 0)
        {
            _studyQueue.Clear();
            _studyIndex = -1;
            StudyProgressText.Text = "No cards available.";
            StudyFrontText.Text = "Generate or import cards first.";
            StudyBackText.Text = "";
            StudyAnswerPanel.Visibility = Visibility.Collapsed;
            StudyHintText.Text = "";
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
        _answerShown = false;

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
            return;
        }

        var card = _studyQueue[_studyIndex];
        var deck = GetActiveDeck();

        StudyProgressText.Text = deck is null
            ? $"Card {_studyIndex + 1} / {_studyQueue.Count}"
            : $"{deck.Name} • Card {_studyIndex + 1} / {_studyQueue.Count}";

        StudyFrontText.Text = card.Front;
        StudyBackText.Text = card.Back;
        StudyAnswerPanel.Visibility = _answerShown ? Visibility.Visible : Visibility.Collapsed;

        StudyHintText.Text = _answerShown
            ? "Choose how well you knew it."
            : "Try to answer before showing the back.";
    }

    private void ShowAnswer_Click(object sender, RoutedEventArgs e)
    {
        if (_studyIndex < 0 || _studyIndex >= _studyQueue.Count)
            return;

        _answerShown = true;
        ShowStudyCard();
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
        MessageBox.Show("AI settings saved locally.");
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
            MessageBox.Show("Invalid activation code.");
            return;
        }

        if (IsCodeUsed(code))
        {
            MessageBox.Show("This activation code was already used.");
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

        MessageBox.Show("Activation applied.");
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
        AccountEmailText.Text = "Email: " + (_currentUser?.Email ?? "");
        AccountPlanText.Text = "Plan: " + (_currentUser?.Plan ?? "");
        AccountExpiryText.Text = "Expires: " + FormatExpiry(_currentUser?.SubscriptionExpiresAt ?? DateTime.UtcNow);
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
            MessageBox.Show("No cards in selected deck.");
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
            MessageBox.Show("No card selected.");
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
            MessageBox.Show("No card selected.");
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

    private void SetStatus(string message)
    {
        StatusText.Text = message;
    }
}
