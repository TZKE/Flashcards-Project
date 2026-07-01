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
    public string Front { get; set; } = "";
    public string Back { get; set; } = "";
    public string Tags { get; set; } = "";
    public DateTime DueAt { get; set; } = DateTime.UtcNow;
    public int Repetitions { get; set; }
    public int StudiedToday { get; set; }
    public DateTime LastStudiedAt { get; set; } = DateTime.MinValue;

    public override string ToString()
    {
        string preview = Front.Replace("\r", " ").Replace("\n", " ");
        return preview.Length > 90 ? preview[..90] + "..." : preview;
    }
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
    private string CardsPath => Path.Combine(dataDir, "cards.json");

    private LocalStore _store = new();
    private AppSettings _settings = new();
    private LocalAccount? _currentUser;
    private readonly List<Flashcard> _cards = new();

    private int _currentIndex = -1;
    private int _studyIndex = -1;
    private bool _answerShown;
    private bool _suppressSelection;
    private int _studiedToday;

    public MainWindow()
    {
        InitializeComponent();

        Directory.CreateDirectory(dataDir);

        LoadStore();
        LoadSettings();
        LoadCards();
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
        ShowPage(PageDashboard);
        RefreshAll();
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
    private void DeckPage_Click(object sender, RoutedEventArgs e) => ShowPage(PageDeck);
    private void StudyPage_Click(object sender, RoutedEventArgs e)
    {
        StartStudySession();
        ShowPage(PageStudy);
    }
    private void PreviewPage_Click(object sender, RoutedEventArgs e) => ShowPage(PagePreview);
    private void ExportPage_Click(object sender, RoutedEventArgs e)
    {
        SaveCurrentEdits();
        ExportPreviewBox.Text = GetAnkiText();
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

        if (string.IsNullOrWhiteSpace(_settings.ApiKey))
        {
            MessageBox.Show("Add your Z.ai API key in AI Settings first.");
            SettingsPage_Click(sender, e);
            return;
        }

        try
        {
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

            _cards.AddRange(parsed);
            _currentIndex = _cards.Count - parsed.Count;

            SaveCards();
            RefreshAll();
            UpdatePreview();

            MessageBox.Show($"Generated {parsed.Count} cards.");
            ShowPage(PagePreview);
            SetStatus($"Generated {parsed.Count} cards.");
        }
        catch (Exception ex)
        {
            MessageBox.Show("Generation failed:\n\n" + ex.Message);
            SetStatus("Generation failed.");
        }
        finally
        {
            IsEnabled = true;
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

        var parsed = ParseFlashcards(text);

        if (parsed.Count == 0)
        {
            MessageBox.Show("No cards found. Make sure the AI returned valid JSON with front/back/tags.");
            return;
        }

        _cards.AddRange(parsed);
        _currentIndex = _cards.Count - parsed.Count;

        SaveCards();
        RefreshAll();
        UpdatePreview();

        MessageBox.Show($"Imported {parsed.Count} cards.");
        ShowPage(PagePreview);
        SetStatus($"Imported {parsed.Count} cards.");
    }

    private void ClearImport_Click(object sender, RoutedEventArgs e) => ImportBox.Clear();

    private void ImportedList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection) return;

        if (ImportedList.SelectedIndex >= 0 && ImportedList.SelectedIndex < _cards.Count)
        {
            SaveCurrentEdits();
            _currentIndex = ImportedList.SelectedIndex;
            UpdatePreview();
        }
    }

    private void DeckList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection) return;

        if (DeckList.SelectedIndex >= 0 && DeckList.SelectedIndex < _cards.Count)
        {
            SaveCurrentEdits();
            _currentIndex = DeckList.SelectedIndex;
            UpdatePreview();
        }
    }

    private void PreviewSelected_Click(object sender, RoutedEventArgs e)
    {
        if (DeckList.SelectedIndex >= 0)
        {
            _currentIndex = DeckList.SelectedIndex;
            UpdatePreview();
            ShowPage(PagePreview);
        }
    }

    private void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        if (DeckList.SelectedIndex < 0)
        {
            MessageBox.Show("Select a card first.");
            return;
        }

        _currentIndex = DeckList.SelectedIndex;
        DeleteCurrentCard();
    }

    private void Previous_Click(object sender, RoutedEventArgs e) => NavigateCard(-1);
    private void Next_Click(object sender, RoutedEventArgs e) => NavigateCard(1);
    private void SaveCard_Click(object sender, RoutedEventArgs e) => SaveCard();
    private void DeleteCard_Click(object sender, RoutedEventArgs e) => DeleteCurrentCard();
    private void CopyCurrent_Click(object sender, RoutedEventArgs e) => CopyCurrent();
    private void CopyAll_Click(object sender, RoutedEventArgs e) => CopyAll();
    private void ExportTxt_Click(object sender, RoutedEventArgs e) => ExportTxt();

    private void RefreshExport_Click(object sender, RoutedEventArgs e)
    {
        SaveCurrentEdits();
        ExportPreviewBox.Text = GetAnkiText();
        SetStatus("Export preview refreshed.");
    }

    private void StartStudySession()
    {
        if (_cards.Count == 0)
        {
            _studyIndex = -1;
            StudyProgressText.Text = "No cards available.";
            StudyFrontText.Text = "Generate or import cards first.";
            StudyBackText.Text = "";
            StudyAnswerPanel.Visibility = Visibility.Collapsed;
            StudyHintText.Text = "";
            return;
        }

        int dueIndex = _cards.FindIndex(c => c.DueAt <= DateTime.UtcNow);
        _studyIndex = dueIndex >= 0 ? dueIndex : 0;
        _answerShown = false;
        ShowStudyCard();
    }

    private void ShowStudyCard()
    {
        if (_studyIndex < 0 || _studyIndex >= _cards.Count)
            return;

        var card = _cards[_studyIndex];

        StudyProgressText.Text = $"Card {_studyIndex + 1} / {_cards.Count}";
        StudyFrontText.Text = card.Front;
        StudyBackText.Text = card.Back;
        StudyAnswerPanel.Visibility = _answerShown ? Visibility.Visible : Visibility.Collapsed;
        StudyHintText.Text = _answerShown
            ? "Choose how well you knew it."
            : "Try to answer before showing the back.";
    }

    private void ShowAnswer_Click(object sender, RoutedEventArgs e)
    {
        if (_studyIndex < 0 || _studyIndex >= _cards.Count) return;

        _answerShown = true;
        ShowStudyCard();
    }

    private void Again_Click(object sender, RoutedEventArgs e) => RateStudyCard(TimeSpan.FromMinutes(10));
    private void Hard_Click(object sender, RoutedEventArgs e) => RateStudyCard(TimeSpan.FromDays(1));
    private void Good_Click(object sender, RoutedEventArgs e) => RateStudyCard(TimeSpan.FromDays(3));
    private void Easy_Click(object sender, RoutedEventArgs e) => RateStudyCard(TimeSpan.FromDays(7));

    private void RateStudyCard(TimeSpan interval)
    {
        if (_studyIndex < 0 || _studyIndex >= _cards.Count) return;

        var card = _cards[_studyIndex];
        card.DueAt = DateTime.UtcNow.Add(interval);
        card.Repetitions++;
        card.LastStudiedAt = DateTime.UtcNow;

        _studiedToday++;

        SaveCards();

        _studyIndex++;
        if (_studyIndex >= _cards.Count)
            _studyIndex = 0;

        _answerShown = false;
        ShowStudyCard();
        RefreshAll();
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
                    Front = front.Trim(),
                    Back = back.Trim(),
                    Tags = string.IsNullOrWhiteSpace(tags) ? "AIFlashcards" : tags.Trim(),
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
                    Front = parts[0].Trim(),
                    Back = parts[1].Trim(),
                    Tags = parts.Length >= 3 && !string.IsNullOrWhiteSpace(parts[2])
                        ? parts[2].Trim()
                        : "AIFlashcards",
                    DueAt = DateTime.UtcNow
                });
            }
        }

        return list;
    }

    private void RefreshAll()
    {
        RefreshCardLists();
        RefreshStats();
        RefreshStudyPage();
        RefreshAccountPage();
    }

    private void RefreshCardLists()
    {
        _suppressSelection = true;

        ImportedList.ItemsSource = null;
        ImportedList.ItemsSource = _cards;

        DeckList.ItemsSource = null;
        DeckList.ItemsSource = _cards;

        ImportSummaryText.Text = _cards.Count == 0 ? "No cards yet." : $"{_cards.Count} cards in local deck.";
        DeckSummaryText.Text = $"{_cards.Count} cards in local deck.";

        if (_currentIndex >= 0 && _currentIndex < _cards.Count)
        {
            ImportedList.SelectedIndex = _currentIndex;
            DeckList.SelectedIndex = _currentIndex;
        }

        _suppressSelection = false;
    }

    private void RefreshStats()
    {
        int due = _cards.Count(c => c.DueAt <= DateTime.UtcNow);

        StatsTotalCards.Text = _cards.Count.ToString();
        StatsDueCards.Text = due.ToString();
        StatsStudiedToday.Text = _studiedToday.ToString();
    }

    private void RefreshStudyPage()
    {
        if (_studyIndex >= 0 && _studyIndex < _cards.Count)
            ShowStudyCard();
    }

    private void RefreshAccountPage()
    {
        AccountEmailText.Text = "Email: " + (_currentUser?.Email ?? "");
        AccountPlanText.Text = "Plan: " + (_currentUser?.Plan ?? "");
        AccountExpiryText.Text = "Expires: " + FormatExpiry(_currentUser?.SubscriptionExpiresAt ?? DateTime.UtcNow);
    }

    private void UpdatePreview()
    {
        if (_cards.Count == 0 || _currentIndex < 0 || _currentIndex >= _cards.Count)
        {
            CardCounterText.Text = "No card selected.";
            FrontBox.Text = "";
            BackBox.Text = "";
            TagsBox.Text = "";
            return;
        }

        var card = _cards[_currentIndex];
        CardCounterText.Text = $"Card {_currentIndex + 1} / {_cards.Count}";
        FrontBox.Text = card.Front;
        BackBox.Text = card.Back;
        TagsBox.Text = card.Tags;
    }

    private void NavigateCard(int direction)
    {
        if (_cards.Count == 0)
        {
            MessageBox.Show("No cards yet.");
            return;
        }

        SaveCurrentEdits();

        _currentIndex += direction;

        if (_currentIndex < 0)
            _currentIndex = 0;

        if (_currentIndex >= _cards.Count)
            _currentIndex = _cards.Count - 1;

        UpdatePreview();
        RefreshCardLists();
    }

    private void SaveCurrentEdits()
    {
        if (_currentIndex < 0 || _currentIndex >= _cards.Count)
            return;

        _cards[_currentIndex].Front = FrontBox.Text.Trim();
        _cards[_currentIndex].Back = BackBox.Text.Trim();
        _cards[_currentIndex].Tags = TagsBox.Text.Trim();

        SaveCards();
    }

    private void SaveCard()
    {
        SaveCurrentEdits();
        RefreshAll();
        SetStatus("Card saved.");
    }

    private void DeleteCurrentCard()
    {
        if (_currentIndex < 0 || _currentIndex >= _cards.Count)
        {
            MessageBox.Show("No card selected.");
            return;
        }

        if (MessageBox.Show("Delete this card?", "Confirm", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
            return;

        _cards.RemoveAt(_currentIndex);

        if (_cards.Count == 0)
            _currentIndex = -1;
        else if (_currentIndex >= _cards.Count)
            _currentIndex = _cards.Count - 1;

        SaveCards();
        RefreshAll();
        UpdatePreview();
    }

    private void CopyCurrent()
    {
        SaveCurrentEdits();

        if (_currentIndex < 0 || _currentIndex >= _cards.Count)
        {
            MessageBox.Show("No card selected.");
            return;
        }

        Clipboard.SetText(ToAnkiLine(_cards[_currentIndex]));
        SetStatus("Current card copied.");
    }

    private void CopyAll()
    {
        SaveCurrentEdits();

        if (_cards.Count == 0)
        {
            MessageBox.Show("No cards to copy.");
            return;
        }

        Clipboard.SetText(GetAnkiText());
        MessageBox.Show("Copied all cards for Anki.");
        SetStatus("All cards copied.");
    }

    private void ExportTxt()
    {
        SaveCurrentEdits();

        if (_cards.Count == 0)
        {
            MessageBox.Show("No cards to export.");
            return;
        }

        var sfd = new SaveFileDialog
        {
            Title = "Export Anki text file",
            Filter = "Text file|*.txt",
            FileName = "anki_flashcards.txt"
        };

        if (sfd.ShowDialog() == true)
        {
            File.WriteAllText(sfd.FileName, GetAnkiText(), Encoding.UTF8);
            MessageBox.Show("Exported successfully.");
        }
    }

    private string GetAnkiText()
    {
        return string.Join(Environment.NewLine, _cards.Select(ToAnkiLine));
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

    private void LoadCards()
    {
        try
        {
            if (!File.Exists(CardsPath))
                return;

            string json = File.ReadAllText(CardsPath);
            var loaded = JsonSerializer.Deserialize<List<Flashcard>>(json);

            if (loaded is not null)
                _cards.AddRange(loaded);
        }
        catch
        {
            // ignore damaged cards file
        }
    }

    private void SaveCards()
    {
        File.WriteAllText(CardsPath, JsonSerializer.Serialize(_cards, new JsonSerializerOptions
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

    private void SetStatus(string message)
    {
        StatusText.Text = message;
    }
}
