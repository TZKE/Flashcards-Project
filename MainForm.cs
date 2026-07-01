using System.Drawing;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Forms;

namespace AIFlashcardMaker;

public sealed class Flashcard
{
    public string Front { get; set; } = "";
    public string Back { get; set; } = "";
    public string Tags { get; set; } = "";
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

public sealed class MainForm : Form
{
    private readonly string dataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AIFlashcardMaker");

    private string StorePath => Path.Combine(dataDir, "accounts.json");
    private string SettingsPath => Path.Combine(dataDir, "settings.json");

    private readonly List<Flashcard> _cards = new();
    private LocalStore _store = new();
    private AppSettings _settings = new();
    private LocalAccount? _currentUser;
    private int _currentIndex = -1;
    private bool _suppressCardSelection;

    private Panel _content = new();
    private Label _status = new();
    private Label _headerAccount = new();

    private TextBox txtLoginEmail = new();
    private TextBox txtLoginPassword = new();

    private TextBox txtSignupEmail = new();
    private TextBox txtSignupPassword = new();
    private TextBox txtSignupCode = new();

    private readonly TextBox txtSource = new();
    private readonly TextBox txtPrompt = new();
    private readonly TextBox txtImport = new();
    private readonly TextBox txtExportPreview = new();

    private readonly TextBox txtFront = new();
    private readonly TextBox txtBack = new();
    private readonly TextBox txtTags = new();

    private readonly TextBox txtApiKey = new();
    private readonly TextBox txtModel = new();
    private readonly TextBox txtBaseUrl = new();

    private readonly ComboBox cboMode = new();
    private readonly ComboBox cboDifficulty = new();
    private readonly ComboBox cboAnswerLength = new();
    private readonly ComboBox cboCount = new();
    private readonly ComboBox cboLanguage = new();

    private readonly ListBox lstCards = new();
    private readonly Label lblCardCounter = new();
    private readonly Label lblImportSummary = new();

    private readonly Color Bg = Color.FromArgb(13, 17, 27);
    private readonly Color Sidebar = Color.FromArgb(16, 21, 34);
    private readonly Color Panel = Color.FromArgb(25, 32, 48);
    private readonly Color Panel2 = Color.FromArgb(33, 42, 61);
    private readonly Color Input = Color.FromArgb(8, 12, 20);
    private readonly Color TextColor = Color.FromArgb(238, 242, 248);
    private readonly Color Muted = Color.FromArgb(162, 172, 190);
    private readonly Color Blue = Color.FromArgb(92, 142, 255);
    private readonly Color Green = Color.FromArgb(83, 210, 166);
    private readonly Color Red = Color.FromArgb(210, 82, 92);
    private readonly Color Yellow = Color.FromArgb(235, 190, 92);

    public MainForm()
    {
        Text = "AI Flashcard Maker";
        Width = 1320;
        Height = 860;
        MinimumSize = new Size(1120, 720);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Bg;
        DoubleBuffered = true;

        LoadStore();
        LoadSettings();
        EnsureOptionBoxes();
        BuildLoginScreen();
    }

    private void BuildLoginScreen()
    {
        Controls.Clear();
        BackColor = Bg;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(34),
            BackColor = Bg
        };

        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 44));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 56));
        Controls.Add(root);

        var hero = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 8,
            ColumnCount = 1,
            BackColor = Bg,
            Padding = new Padding(20)
        };

        hero.RowStyles.Add(new RowStyle(SizeType.Percent, 22));
        hero.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
        hero.RowStyles.Add(new RowStyle(SizeType.Absolute, 86));
        hero.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        hero.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        hero.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        hero.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));
        hero.RowStyles.Add(new RowStyle(SizeType.Percent, 78));

        hero.Controls.Add(new Label
        {
            Text = "AI Flashcard Maker",
            Dock = DockStyle.Fill,
            ForeColor = TextColor,
            Font = new Font("Segoe UI", 31, FontStyle.Bold),
            TextAlign = ContentAlignment.BottomLeft
        }, 0, 1);

        hero.Controls.Add(new Label
        {
            Text = "A clean Anki workflow for medical students: generate cards with Z.ai, preview, edit, study, and export.",
            Dock = DockStyle.Fill,
            ForeColor = Muted,
            Font = new Font("Segoe UI", 13),
            TextAlign = ContentAlignment.TopLeft
        }, 0, 2);

        hero.Controls.Add(Feature("Automatic Z.ai flashcard generation"), 0, 3);
        hero.Controls.Add(Feature("Preview and edit every card"), 0, 4);
        hero.Controls.Add(Feature("Anki-ready copy and .txt export"), 0, 5);

        hero.Controls.Add(new Label
        {
            Text = "Demo activation codes:\nFLASH-MONTH-2026   FLASH-YEAR-2026   FLASH-LIFE-2026",
            ForeColor = Yellow,
            Font = new Font("Consolas", 10),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.TopLeft
        }, 0, 6);

        root.Controls.Add(hero, 0, 0);

        var authCard = Card();
        authCard.Padding = new Padding(28);
        root.Controls.Add(authCard, 1, 0);

        var tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 11),
            Appearance = TabAppearance.Normal
        };

        var loginTab = new TabPage("Login") { BackColor = Panel };
        var signupTab = new TabPage("Create Account") { BackColor = Panel };

        tabs.TabPages.Add(loginTab);
        tabs.TabPages.Add(signupTab);

        authCard.Controls.Add(tabs);

        BuildLoginTab(loginTab);
        BuildSignupTab(signupTab);
    }

    private Label Feature(string text)
    {
        return new Label
        {
            Text = "✓ " + text,
            ForeColor = Green,
            Font = new Font("Segoe UI", 12, FontStyle.Bold),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        };
    }

    private void BuildLoginTab(TabPage tab)
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 9,
            ColumnCount = 1,
            Padding = new Padding(28),
            BackColor = Panel
        };

        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        tab.Controls.Add(layout);

        layout.Controls.Add(Title("Welcome back"), 0, 0);

        txtLoginEmail = InputBox("Email");
        txtLoginPassword = InputBox("Password", password: true);

        layout.Controls.Add(SmallLabel("Email"), 0, 1);
        layout.Controls.Add(txtLoginEmail, 0, 2);
        layout.Controls.Add(SmallLabel("Password"), 0, 3);
        layout.Controls.Add(txtLoginPassword, 0, 4);

        var btnLogin = Button("Login", Blue);
        btnLogin.Click += (_, _) => Login();
        layout.Controls.Add(btnLogin, 0, 6);

        layout.Controls.Add(new Label
        {
            Text = "New users must create an account and enter an activation code.",
            ForeColor = Muted,
            Font = new Font("Segoe UI", 10),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.TopLeft
        }, 0, 7);
    }

    private void BuildSignupTab(TabPage tab)
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 11,
            ColumnCount = 1,
            Padding = new Padding(28),
            BackColor = Panel
        };

        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 90));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        tab.Controls.Add(layout);

        layout.Controls.Add(Title("Create account"), 0, 0);

        txtSignupEmail = InputBox("Email");
        txtSignupPassword = InputBox("Password", password: true);
        txtSignupCode = InputBox("Activation code");

        layout.Controls.Add(SmallLabel("Email"), 0, 1);
        layout.Controls.Add(txtSignupEmail, 0, 2);

        layout.Controls.Add(SmallLabel("Password"), 0, 3);
        layout.Controls.Add(txtSignupPassword, 0, 4);

        layout.Controls.Add(SmallLabel("Activation code"), 0, 5);
        layout.Controls.Add(txtSignupCode, 0, 6);

        var btnSignup = Button("Create Account", Green);
        btnSignup.Click += (_, _) => Signup();
        layout.Controls.Add(btnSignup, 0, 8);

        layout.Controls.Add(new Label
        {
            Text = "Demo codes:\nFLASH-MONTH-2026\nFLASH-YEAR-2026\nFLASH-LIFE-2026",
            ForeColor = Yellow,
            Font = new Font("Consolas", 10),
            Dock = DockStyle.Fill
        }, 0, 9);
    }

    private void BuildMainApp()
    {
        Controls.Clear();
        BackColor = Bg;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 3,
            ColumnCount = 1,
            BackColor = Bg
        };

        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 74));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));

        Controls.Add(root);

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Padding = new Padding(20, 10, 20, 10),
            BackColor = Panel
        };

        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 430));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));

        header.Controls.Add(new Label
        {
            Text = "AI Flashcard Maker",
            ForeColor = TextColor,
            Font = new Font("Segoe UI", 22, FontStyle.Bold),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);

        _headerAccount = new Label
        {
            Text = GetAccountSummary(),
            ForeColor = Muted,
            Font = new Font("Segoe UI", 10),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleRight
        };

        header.Controls.Add(_headerAccount, 1, 0);

        var btnLogout = Button("Logout", Panel2, 100);
        btnLogout.Click += (_, _) =>
        {
            _currentUser = null;
            BuildLoginScreen();
        };

        header.Controls.Add(btnLogout, 2, 0);
        root.Controls.Add(header, 0, 0);

        var main = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Bg
        };

        main.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230));
        main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        root.Controls.Add(main, 0, 1);

        var sidebar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            Padding = new Padding(14),
            WrapContents = false,
            BackColor = Sidebar
        };

        sidebar.Controls.Add(NavButton("1. Create / Generate", ShowCreatePage));
        sidebar.Controls.Add(NavButton("2. Import JSON", ShowImportPage));
        sidebar.Controls.Add(NavButton("3. Preview / Edit", ShowPreviewPage));
        sidebar.Controls.Add(NavButton("4. Export", ShowExportPage));
        sidebar.Controls.Add(NavButton("5. Account", ShowAccountPage));
        sidebar.Controls.Add(NavButton("6. AI Settings", ShowSettingsPage));

        main.Controls.Add(sidebar, 0, 0);

        _content = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18),
            BackColor = Bg
        };

        main.Controls.Add(_content, 1, 0);

        _status = new Label
        {
            Text = "Ready.",
            ForeColor = Muted,
            BackColor = Panel,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(14, 0, 0, 0),
            Font = new Font("Segoe UI", 9)
        };

        root.Controls.Add(_status, 0, 2);

        ShowCreatePage();
    }

    private void ShowCreatePage()
    {
        _content.Controls.Clear();

        var page = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 3,
            ColumnCount = 1,
            BackColor = Bg
        };

        page.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        page.RowStyles.Add(new RowStyle(SizeType.Absolute, 74));
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _content.Controls.Add(page);

        page.Controls.Add(PageHeader("Step 1 — Create or Generate Flashcards",
            "Paste your notes. Use Generate with Z.ai for automatic cards, or Create Prompt as backup."), 0, 0);

        page.Controls.Add(OptionsBar(), 0, 1);

        var split = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Bg
        };

        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 64));
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 36));

        page.Controls.Add(split, 0, 2);

        var sourceCard = CardWithTitle("Source Material");
        var promptCard = CardWithTitle("Manual AI Prompt Backup");

        split.Controls.Add(sourceCard.Container, 0, 0);
        split.Controls.Add(promptCard.Container, 1, 0);

        StyleLargeTextBox(txtSource, "Paste your notes here...");
        sourceCard.Body.Controls.Add(txtSource);

        var sourceButtons = BottomBar();
        sourceButtons.Controls.Add(Button("Generate with Z.ai", Green, 165, async (_, _) => await GenerateAutomaticallyAsync()));
        sourceButtons.Controls.Add(Button("Create Prompt", Blue, 135, (_, _) => CreatePrompt()));
        sourceButtons.Controls.Add(Button("Clear", Panel2, 85, (_, _) => txtSource.Clear()));
        sourceButtons.Controls.Add(Button("Next: Import", Panel2, 120, (_, _) => ShowImportPage()));
        sourceCard.Footer.Controls.Add(sourceButtons);

        StyleLargeTextBox(txtPrompt, "Manual prompt will appear here...");
        txtPrompt.ReadOnly = false;
        promptCard.Body.Controls.Add(txtPrompt);

        var promptButtons = BottomBar();
        promptButtons.Controls.Add(Button("Copy Prompt", Green, 135, (_, _) => CopyPrompt()));
        promptButtons.Controls.Add(Button("Clear Prompt", Panel2, 125, (_, _) => txtPrompt.Clear()));
        promptCard.Footer.Controls.Add(promptButtons);
    }

    private void ShowImportPage()
    {
        _content.Controls.Clear();

        var page = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 2,
            ColumnCount = 1,
            BackColor = Bg
        };

        page.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _content.Controls.Add(page);

        page.Controls.Add(PageHeader("Step 2 — Import AI JSON",
            "Paste the JSON answer returned by an AI. Automatic Z.ai generation skips this step."), 0, 0);

        var split = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Bg
        };

        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 66));
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));

        page.Controls.Add(split, 0, 1);

        var importCard = CardWithTitle("Paste AI JSON Answer");
        var listCard = CardWithTitle("Imported Cards");

        split.Controls.Add(importCard.Container, 0, 0);
        split.Controls.Add(listCard.Container, 1, 0);

        StyleLargeTextBox(txtImport, "Paste AI JSON here...");
        importCard.Body.Controls.Add(txtImport);

        var importButtons = BottomBar();
        importButtons.Controls.Add(Button("Import Cards", Blue, 140, (_, _) => ImportCards()));
        importButtons.Controls.Add(Button("Clear", Panel2, 95, (_, _) => txtImport.Clear()));
        importButtons.Controls.Add(Button("Next: Preview", Green, 135, (_, _) => ShowPreviewPage()));
        importCard.Footer.Controls.Add(importButtons);

        lstCards.Dock = DockStyle.Fill;
        lstCards.Font = new Font("Segoe UI", 10);
        lstCards.BackColor = Input;
        lstCards.ForeColor = TextColor;
        lstCards.BorderStyle = BorderStyle.FixedSingle;
        lstCards.SelectedIndexChanged -= LstCards_SelectedIndexChanged;
        lstCards.SelectedIndexChanged += LstCards_SelectedIndexChanged;
        listCard.Body.Controls.Add(lstCards);

        lblImportSummary.Text = _cards.Count == 0
            ? "No cards imported yet."
            : $"{_cards.Count} cards imported.";

        lblImportSummary.Dock = DockStyle.Top;
        lblImportSummary.Height = 28;
        lblImportSummary.ForeColor = Muted;
        lblImportSummary.Font = new Font("Segoe UI", 10);
        listCard.Body.Controls.Add(lblImportSummary);
        lblImportSummary.BringToFront();

        var listButtons = BottomBar();
        listButtons.Controls.Add(Button("Copy All", Green, 115, (_, _) => CopyAll()));
        listButtons.Controls.Add(Button("Export .txt", Panel2, 120, (_, _) => ExportTxt()));
        listCard.Footer.Controls.Add(listButtons);
    }

    private void ShowPreviewPage()
    {
        _content.Controls.Clear();

        var page = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 2,
            ColumnCount = 1,
            BackColor = Bg
        };

        page.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _content.Controls.Add(page);

        page.Controls.Add(PageHeader("Step 3 — Preview / Edit Cards",
            "Review each flashcard before exporting. Edits are saved when you click Save or move between cards."), 0, 0);

        var card = Card();
        page.Controls.Add(card, 0, 1);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 7,
            ColumnCount = 1,
            BackColor = Panel,
            Padding = new Padding(14)
        };

        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 74));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 4));

        card.Controls.Add(layout);

        lblCardCounter.Dock = DockStyle.Fill;
        lblCardCounter.ForeColor = TextColor;
        lblCardCounter.Font = new Font("Segoe UI", 16, FontStyle.Bold);
        lblCardCounter.TextAlign = ContentAlignment.MiddleLeft;
        layout.Controls.Add(lblCardCounter, 0, 0);

        StyleLargeTextBox(txtFront, "Front");
        StyleLargeTextBox(txtBack, "Back");
        StyleSingleLineTextBox(txtTags, "Tags");

        layout.Controls.Add(FieldBox("Front", txtFront), 0, 1);
        layout.Controls.Add(FieldBox("Back", txtBack), 0, 2);

        var nav = BottomBar();
        nav.Controls.Add(Button("Previous", Panel2, 110, (_, _) => NavigateCard(-1)));
        nav.Controls.Add(Button("Next", Panel2, 90, (_, _) => NavigateCard(1)));
        nav.Controls.Add(Button("Save", Blue, 90, (_, _) => SaveCard()));
        nav.Controls.Add(Button("Delete", Red, 95, (_, _) => DeleteCurrentCard()));
        nav.Controls.Add(Button("Copy Current", Green, 140, (_, _) => CopyCurrent()));
        layout.Controls.Add(nav, 0, 3);

        layout.Controls.Add(FieldBox("Tags", txtTags), 0, 4);

        var bottom = BottomBar();
        bottom.Controls.Add(Button("Copy All For Anki", Green, 170, (_, _) => CopyAll()));
        bottom.Controls.Add(Button("Export .txt", Panel2, 120, (_, _) => ExportTxt()));
        bottom.Controls.Add(Button("Next: Export Page", Blue, 160, (_, _) => ShowExportPage()));
        layout.Controls.Add(bottom, 0, 5);

        UpdatePreview();
    }

    private void ShowExportPage()
    {
        SaveCurrentEdits();
        _content.Controls.Clear();

        var page = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 3,
            ColumnCount = 1,
            BackColor = Bg
        };

        page.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        page.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));

        _content.Controls.Add(page);

        page.Controls.Add(PageHeader("Step 4 — Export to Anki",
            "Copy the tab-separated cards or export a .txt file. Anki fields: Front, Back, Tags."), 0, 0);

        var card = CardWithTitle("Anki Tab-Separated Preview");
        page.Controls.Add(card.Container, 0, 1);

        StyleLargeTextBox(txtExportPreview, "Export preview");
        txtExportPreview.ReadOnly = true;
        txtExportPreview.Text = GetAnkiText();
        card.Body.Controls.Add(txtExportPreview);

        var buttons = BottomBar();
        buttons.Padding = new Padding(18, 10, 18, 10);
        buttons.BackColor = Bg;
        buttons.Controls.Add(Button("Refresh Preview", Panel2, 150, (_, _) =>
        {
            SaveCurrentEdits();
            txtExportPreview.Text = GetAnkiText();
            SetStatus("Export preview refreshed.");
        }));
        buttons.Controls.Add(Button("Copy All For Anki", Green, 170, (_, _) => CopyAll()));
        buttons.Controls.Add(Button("Export .txt", Blue, 125, (_, _) => ExportTxt()));

        page.Controls.Add(buttons, 0, 2);
    }

    private void ShowAccountPage()
    {
        _content.Controls.Clear();

        var page = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 2,
            ColumnCount = 1,
            BackColor = Bg
        };

        page.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _content.Controls.Add(page);

        page.Controls.Add(PageHeader("Account",
            "Local demo account and activation system. Real subscription codes need an online backend later."), 0, 0);

        var card = Card();
        page.Controls.Add(card, 0, 1);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 370,
            RowCount = 9,
            ColumnCount = 1,
            Padding = new Padding(24),
            BackColor = Panel
        };

        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        card.Controls.Add(layout);

        layout.Controls.Add(Title("Subscription details"), 0, 0);
        layout.Controls.Add(Info("Email: " + (_currentUser?.Email ?? "")), 0, 1);
        layout.Controls.Add(Info("Plan: " + (_currentUser?.Plan ?? "")), 0, 2);
        layout.Controls.Add(Info("Expires: " + FormatExpiry(_currentUser?.SubscriptionExpiresAt ?? DateTime.UtcNow)), 0, 3);

        layout.Controls.Add(SmallLabel("Apply new activation code"), 0, 5);

        var txtNewCode = InputBox("Activation code");
        layout.Controls.Add(txtNewCode, 0, 6);

        var btnApply = Button("Apply Code", Green, 130);
        btnApply.Click += (_, _) => ApplyActivationCodeToCurrentUser(txtNewCode.Text);
        layout.Controls.Add(btnApply, 0, 7);

        layout.Controls.Add(new Label
        {
            Text = "Demo codes are stored locally and are not secure for real selling. Later we should connect this screen to Supabase/Vercel for real monthly/yearly activation.",
            ForeColor = Muted,
            Font = new Font("Segoe UI", 10),
            Dock = DockStyle.Fill
        }, 0, 8);
    }

    private void ShowSettingsPage()
    {
        _content.Controls.Clear();

        var page = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 2,
            ColumnCount = 1,
            BackColor = Bg
        };

        page.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _content.Controls.Add(page);

        page.Controls.Add(PageHeader("AI Settings",
            "Paste your Z.ai API key here. It is saved locally on this computer, not in GitHub."), 0, 0);

        var card = Card();
        page.Controls.Add(card, 0, 1);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 410,
            RowCount = 9,
            ColumnCount = 1,
            Padding = new Padding(24),
            BackColor = Panel
        };

        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        card.Controls.Add(layout);

        layout.Controls.Add(Title("Z.ai API"), 0, 0);

        StyleSingleLineTextBox(txtApiKey, "Paste Z.ai API key");
        txtApiKey.UseSystemPasswordChar = true;
        txtApiKey.Text = _settings.ApiKey;

        StyleSingleLineTextBox(txtModel, "Model");
        txtModel.Text = string.IsNullOrWhiteSpace(_settings.Model) ? "GLM-4.7-FlashX" : _settings.Model;

        StyleSingleLineTextBox(txtBaseUrl, "Base URL");
        txtBaseUrl.Text = string.IsNullOrWhiteSpace(_settings.BaseUrl)
            ? "https://api.z.ai/api/paas/v4"
            : _settings.BaseUrl;

        layout.Controls.Add(SmallLabel("API Key"), 0, 1);
        layout.Controls.Add(txtApiKey, 0, 2);

        layout.Controls.Add(SmallLabel("Model"), 0, 3);
        layout.Controls.Add(txtModel, 0, 4);

        layout.Controls.Add(SmallLabel("Base URL"), 0, 5);
        layout.Controls.Add(txtBaseUrl, 0, 6);

        var buttons = BottomBar();
        buttons.Controls.Add(Button("Save Settings", Green, 150, (_, _) => SaveSettingsFromUi()));
        buttons.Controls.Add(Button("Clear Key", Red, 110, (_, _) =>
        {
            txtApiKey.Clear();
            SaveSettingsFromUi();
        }));

        layout.Controls.Add(buttons, 0, 7);

        layout.Controls.Add(new Label
        {
            Text = "For testing, local key storage is okay. For selling the app later, the API key should move to your backend so users cannot access it.",
            ForeColor = Muted,
            Font = new Font("Segoe UI", 10),
            Dock = DockStyle.Fill
        }, 0, 8);
    }

    private Control OptionsBar()
    {
        var bar = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 10,
            RowCount = 1,
            Padding = new Padding(14, 12, 14, 12),
            BackColor = Panel
        };

        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 55));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 24));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 18));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 115));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 18));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 65));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 12));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 85));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));

        bar.Controls.Add(SmallLabel("Mode"), 0, 0);
        bar.Controls.Add(cboMode, 1, 0);
        bar.Controls.Add(SmallLabel("Difficulty"), 2, 0);
        bar.Controls.Add(cboDifficulty, 3, 0);
        bar.Controls.Add(SmallLabel("Answer"), 4, 0);
        bar.Controls.Add(cboAnswerLength, 5, 0);
        bar.Controls.Add(SmallLabel("Count"), 6, 0);
        bar.Controls.Add(cboCount, 7, 0);
        bar.Controls.Add(SmallLabel("Language"), 8, 0);
        bar.Controls.Add(cboLanguage, 9, 0);

        return bar;
    }

    private (Panel Container, Panel Body, Panel Footer) CardWithTitle(string title)
    {
        var container = Card();

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 3,
            ColumnCount = 1,
            Padding = new Padding(12),
            BackColor = Panel
        };

        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));

        container.Controls.Add(layout);

        layout.Controls.Add(new Label
        {
            Text = title,
            ForeColor = TextColor,
            Font = new Font("Segoe UI", 14, FontStyle.Bold),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);

        var body = new Panel { Dock = DockStyle.Fill, BackColor = Panel };
        var footer = new Panel { Dock = DockStyle.Fill, BackColor = Panel };

        layout.Controls.Add(body, 0, 1);
        layout.Controls.Add(footer, 0, 2);

        return (container, body, footer);
    }

    private Panel Card()
    {
        return new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Panel,
            Padding = new Padding(1)
        };
    }

    private Control PageHeader(string title, string subtitle)
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 2,
            ColumnCount = 1,
            BackColor = Bg
        };

        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));

        layout.Controls.Add(new Label
        {
            Text = title,
            Dock = DockStyle.Fill,
            ForeColor = TextColor,
            Font = new Font("Segoe UI", 18, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);

        layout.Controls.Add(new Label
        {
            Text = subtitle,
            Dock = DockStyle.Fill,
            ForeColor = Muted,
            Font = new Font("Segoe UI", 10),
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 1);

        return layout;
    }

    private GroupBox FieldBox(string title, Control control)
    {
        var box = new GroupBox
        {
            Text = title,
            Dock = DockStyle.Fill,
            ForeColor = Muted,
            BackColor = Panel,
            Padding = new Padding(8),
            Font = new Font("Segoe UI", 9, FontStyle.Bold)
        };

        control.Dock = DockStyle.Fill;
        box.Controls.Add(control);
        return box;
    }

    private FlowLayoutPanel BottomBar()
    {
        return new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Panel,
            Padding = new Padding(0, 10, 0, 0)
        };
    }

    private Label Title(string text)
    {
        return new Label
        {
            Text = text,
            ForeColor = TextColor,
            Font = new Font("Segoe UI", 22, FontStyle.Bold),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        };
    }

    private Label Info(string text)
    {
        return new Label
        {
            Text = text,
            ForeColor = TextColor,
            Font = new Font("Segoe UI", 12),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        };
    }

    private Label SmallLabel(string text)
    {
        return new Label
        {
            Text = text,
            ForeColor = Muted,
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        };
    }

    private TextBox InputBox(string placeholder, bool password = false)
    {
        var tb = new TextBox();
        StyleSingleLineTextBox(tb, placeholder);
        tb.UseSystemPasswordChar = password;
        return tb;
    }

    private void StyleSingleLineTextBox(TextBox tb, string placeholder)
    {
        tb.PlaceholderText = placeholder;
        tb.Multiline = false;
        tb.ScrollBars = ScrollBars.None;
        tb.BackColor = Input;
        tb.ForeColor = TextColor;
        tb.BorderStyle = BorderStyle.FixedSingle;
        tb.Font = new Font("Segoe UI", 11);
        tb.Dock = DockStyle.Fill;
        tb.Margin = new Padding(0, 0, 8, 0);
    }

    private void StyleLargeTextBox(TextBox tb, string placeholder)
    {
        tb.PlaceholderText = placeholder;
        tb.Multiline = true;
        tb.ScrollBars = ScrollBars.Vertical;
        tb.BackColor = Input;
        tb.ForeColor = TextColor;
        tb.BorderStyle = BorderStyle.FixedSingle;
        tb.Font = new Font("Segoe UI", 11);
        tb.Dock = DockStyle.Fill;
        tb.Margin = new Padding(0);
        tb.ReadOnly = false;
    }

    private Button Button(string text, Color color, int width = 120, EventHandler? click = null)
    {
        var btn = new Button
        {
            Text = text,
            Width = width,
            Height = 38,
            BackColor = color,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false,
            Margin = new Padding(0, 0, 8, 0)
        };

        btn.FlatAppearance.BorderSize = 0;

        btn.MouseEnter += (_, _) => btn.BackColor = ControlPaint.Light(color);
        btn.MouseLeave += (_, _) => btn.BackColor = color;

        if (click is not null)
            btn.Click += click;

        return btn;
    }

    private Button NavButton(string text, Action action)
    {
        var btn = Button(text, Panel2, 198);
        btn.Height = 48;
        btn.TextAlign = ContentAlignment.MiddleLeft;
        btn.Padding = new Padding(12, 0, 0, 0);
        btn.Margin = new Padding(0, 0, 0, 8);
        btn.Click += (_, _) => action();
        return btn;
    }

    private void EnsureOptionBoxes()
    {
        if (cboMode.Items.Count == 0)
        {
            cboMode.Items.AddRange(new object[]
            {
                "Step 1 High-Yield",
                "Basic Q/A",
                "Cloze Deletion",
                "Image/OCR",
                "English + Arabic Explanation"
            });
            cboMode.SelectedIndex = 0;
        }

        if (cboDifficulty.Items.Count == 0)
        {
            cboDifficulty.Items.AddRange(new object[]
            {
                "Easy",
                "Medium",
                "Hard",
                "Exam Style"
            });
            cboDifficulty.SelectedIndex = 3;
        }

        if (cboAnswerLength.Items.Count == 0)
        {
            cboAnswerLength.Items.AddRange(new object[]
            {
                "Very Short",
                "Normal",
                "Detailed"
            });
            cboAnswerLength.SelectedIndex = 0;
        }

        if (cboCount.Items.Count == 0)
        {
            cboCount.Items.AddRange(new object[]
            {
                "Auto",
                "5",
                "10",
                "20",
                "30",
                "40"
            });
            cboCount.SelectedIndex = 0;
        }

        if (cboLanguage.Items.Count == 0)
        {
            cboLanguage.Items.AddRange(new object[]
            {
                "English",
                "Arabic",
                "English with Arabic explanation"
            });
            cboLanguage.SelectedIndex = 0;
        }

        foreach (var cb in new[] { cboMode, cboDifficulty, cboAnswerLength, cboCount, cboLanguage })
        {
            cb.Dock = DockStyle.Fill;
            cb.DropDownStyle = ComboBoxStyle.DropDownList;
            cb.BackColor = Input;
            cb.ForeColor = TextColor;
            cb.Font = new Font("Segoe UI", 10);
            cb.FlatStyle = FlatStyle.Flat;
            cb.Margin = new Padding(0, 0, 10, 0);
        }
    }

    private void Login()
    {
        string email = NormalizeEmail(txtLoginEmail.Text);
        string password = txtLoginPassword.Text;

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            MessageBox.Show("Enter email and password.", "Missing information");
            return;
        }

        var user = _store.Accounts.FirstOrDefault(x =>
            string.Equals(x.Email, email, StringComparison.OrdinalIgnoreCase));

        if (user is null)
        {
            MessageBox.Show("Account not found.", "Login failed");
            return;
        }

        if (user.PasswordHash != HashPassword(email, password))
        {
            MessageBox.Show("Wrong password.", "Login failed");
            return;
        }

        if (DateTime.UtcNow > user.SubscriptionExpiresAt)
        {
            MessageBox.Show("Your activation expired. Log in after applying a new activation code.", "Activation expired");
            return;
        }

        _currentUser = user;
        BuildMainApp();
        SetStatus("Logged in successfully.");
    }

    private void Signup()
    {
        string email = NormalizeEmail(txtSignupEmail.Text);
        string password = txtSignupPassword.Text;
        string code = NormalizeCode(txtSignupCode.Text);

        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
        {
            MessageBox.Show("Enter a valid email.", "Invalid email");
            return;
        }

        if (password.Length < 4)
        {
            MessageBox.Show("Password must be at least 4 characters for this demo.", "Weak password");
            return;
        }

        if (_store.Accounts.Any(x => string.Equals(x.Email, email, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show("This email already has an account.", "Account exists");
            return;
        }

        var activation = GetActivationInfo(code);

        if (activation is null)
        {
            MessageBox.Show("Invalid activation code.", "Activation failed");
            return;
        }

        if (IsCodeUsed(code))
        {
            MessageBox.Show("This activation code was already used.", "Activation failed");
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
        BuildMainApp();
        SetStatus("Account created and activated.");
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

    private void ApplyActivationCodeToCurrentUser(string rawCode)
    {
        if (_currentUser is null) return;

        string code = NormalizeCode(rawCode);
        var activation = GetActivationInfo(code);

        if (activation is null)
        {
            MessageBox.Show("Invalid activation code.", "Activation failed");
            return;
        }

        if (IsCodeUsed(code))
        {
            MessageBox.Show("This activation code was already used.", "Activation failed");
            return;
        }

        if (activation.Value.Lifetime)
        {
            _currentUser.SubscriptionExpiresAt = DateTime.MaxValue;
        }
        else
        {
            DateTime start = DateTime.UtcNow;

            if (_currentUser.SubscriptionExpiresAt > start)
                start = _currentUser.SubscriptionExpiresAt;

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
        _headerAccount.Text = GetAccountSummary();
        MessageBox.Show("Activation applied successfully.", "Success");
        ShowAccountPage();
    }

    private bool IsCodeUsed(string code)
    {
        return _store.UsedCodes.Any(x =>
            string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase));
    }

    private void LoadStore()
    {
        try
        {
            Directory.CreateDirectory(dataDir);

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
        Directory.CreateDirectory(dataDir);

        File.WriteAllText(StorePath, JsonSerializer.Serialize(_store, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
    }

    private void LoadSettings()
    {
        try
        {
            Directory.CreateDirectory(dataDir);

            if (!File.Exists(SettingsPath))
            {
                _settings = new AppSettings();
                return;
            }

            string json = File.ReadAllText(SettingsPath);
            _settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();

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

    private void SaveSettingsFromUi()
    {
        _settings.ApiProvider = "Z.ai";
        _settings.ApiKey = txtApiKey.Text.Trim();
        _settings.Model = string.IsNullOrWhiteSpace(txtModel.Text.Trim())
            ? "GLM-4.7-FlashX"
            : txtModel.Text.Trim();
        _settings.BaseUrl = string.IsNullOrWhiteSpace(txtBaseUrl.Text.Trim())
            ? "https://api.z.ai/api/paas/v4"
            : txtBaseUrl.Text.Trim().TrimEnd('/');

        SaveSettings();
        SetStatus("AI settings saved locally.");
        MessageBox.Show("AI settings saved locally.", "Saved");
    }

    private void SaveSettings()
    {
        Directory.CreateDirectory(dataDir);

        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(_settings, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
    }

    private void CreatePrompt()
    {
        string source = txtSource.Text.Trim();

        if (string.IsNullOrWhiteSpace(source))
        {
            MessageBox.Show("Paste notes or source material first.", "Missing source");
            return;
        }

        txtPrompt.Text = BuildManualPrompt(source);
        Clipboard.SetText(txtPrompt.Text);
        SetStatus("Prompt created and copied. Paste it into ChatGPT/Gemini/Copilot.");
    }

    private void CopyPrompt()
    {
        if (string.IsNullOrWhiteSpace(txtPrompt.Text))
        {
            MessageBox.Show("Create a prompt first.", "No prompt");
            return;
        }

        Clipboard.SetText(txtPrompt.Text);
        SetStatus("Prompt copied.");
        MessageBox.Show("Prompt copied. Paste it into ChatGPT, Gemini, Copilot, or any AI chat.", "Copied");
    }

    private string BuildManualPrompt(string source)
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
            "- If the source is an image, analyze the image and make cards from visible text, diagrams, tables, or labels.",
            "- If the mode is Cloze Deletion, put the cloze deletion in the front field.",
            "- Keep tags short and useful without spaces.",
            "",
            "OPTIONS:",
            "- Mode: " + cboMode.Text,
            "- Difficulty: " + cboDifficulty.Text,
            "- Answer length: " + cboAnswerLength.Text,
            "- Number of cards: " + cboCount.Text,
            "- Language: " + cboLanguage.Text,
            "",
            "SOURCE MATERIAL:",
            source
        });
    }

    private string BuildAutomaticPrompt(string source)
    {
        return BuildManualPrompt(source);
    }

    private async Task GenerateAutomaticallyAsync()
    {
        string source = txtSource.Text.Trim();

        if (string.IsNullOrWhiteSpace(source))
        {
            MessageBox.Show("Paste notes or source material first.", "Missing source");
            return;
        }

        if (string.IsNullOrWhiteSpace(_settings.ApiKey))
        {
            MessageBox.Show("Add your Z.ai API key first in AI Settings.", "Missing API key");
            ShowSettingsPage();
            return;
        }

        try
        {
            UseWaitCursor = true;
            SetStatus("Generating flashcards with Z.ai...");

            string prompt = BuildAutomaticPrompt(source);
            string aiText = await CallZaiAsync(prompt);
            var parsed = ParseFlashcards(aiText);

            if (parsed.Count == 0)
            {
                txtImport.Text = aiText;
                MessageBox.Show("Z.ai responded, but no cards were parsed. The response was placed in Import JSON so you can inspect it.", "No cards parsed");
                ShowImportPage();
                return;
            }

            _cards.Clear();
            _cards.AddRange(parsed);
            _currentIndex = 0;

            RefreshCardList();
            SyncListSelection();

            SetStatus($"Generated {_cards.Count} flashcards with Z.ai.");
            MessageBox.Show($"Generated {_cards.Count} flashcards successfully.", "Done");

            ShowPreviewPage();
            UpdatePreview();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Generation failed:\n\n" + ex.Message, "Z.ai error");
            SetStatus("Generation failed.");
        }
        finally
        {
            UseWaitCursor = false;
        }
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
        {
            throw new Exception("Z.ai API error:\n\n" + TrimForMessage(json));
        }

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

    private void ImportCards()
    {
        try
        {
            string textToImport = txtImport.Text.Trim();

            if (string.IsNullOrWhiteSpace(textToImport))
            {
                MessageBox.Show("Paste the AI JSON first.", "Missing JSON");
                return;
            }

            var parsed = ParseFlashcards(textToImport);

            if (parsed.Count == 0)
            {
                MessageBox.Show("No cards found. Make sure the AI returned JSON with front, back, and tags.", "Import failed");
                return;
            }

            _cards.Clear();
            _cards.AddRange(parsed);
            _currentIndex = 0;

            RefreshCardList();
            SyncListSelection();
            UpdatePreview();

            lblImportSummary.Text = $"{_cards.Count} cards imported.";
            SetStatus($"Imported {_cards.Count} cards.");

            MessageBox.Show($"Imported {_cards.Count} cards successfully.", "Import complete");
            ShowPreviewPage();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not import cards:\n\n" + ex.Message, "Import failed");
            SetStatus("Import failed.");
        }
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
                    Tags = string.IsNullOrWhiteSpace(tags) ? "AIFlashcards" : tags.Trim()
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
                        : "AIFlashcards"
                });
            }
        }

        return list;
    }

    private void LstCards_SelectedIndexChanged(object? sender, EventArgs e)
    {
        if (_suppressCardSelection)
            return;

        if (lstCards.SelectedIndex >= 0 && lstCards.SelectedIndex < _cards.Count)
        {
            SaveCurrentEdits();
            _currentIndex = lstCards.SelectedIndex;
            UpdatePreview();
        }
    }

    private void RefreshCardList()
    {
        lstCards.Items.Clear();

        for (int i = 0; i < _cards.Count; i++)
        {
            string front = _cards[i].Front.Replace("\r", " ").Replace("\n", " ");
            if (front.Length > 80) front = front.Substring(0, 80) + "...";
            lstCards.Items.Add($"{i + 1}. {front}");
        }
    }

    private void SyncListSelection()
    {
        _suppressCardSelection = true;

        try
        {
            if (_currentIndex >= 0 && _currentIndex < lstCards.Items.Count)
                lstCards.SelectedIndex = _currentIndex;
            else
                lstCards.ClearSelected();
        }
        finally
        {
            _suppressCardSelection = false;
        }
    }

    private void UpdatePreview()
    {
        if (_cards.Count == 0 || _currentIndex < 0 || _currentIndex >= _cards.Count)
        {
            lblCardCounter.Text = "No card selected.";
            txtFront.Text = "";
            txtBack.Text = "";
            txtTags.Text = "";
            return;
        }

        var card = _cards[_currentIndex];
        lblCardCounter.Text = $"Card {_currentIndex + 1} / {_cards.Count}";
        txtFront.Text = card.Front;
        txtBack.Text = card.Back;
        txtTags.Text = card.Tags;
    }

    private void NavigateCard(int direction)
    {
        if (_cards.Count == 0)
        {
            MessageBox.Show("No cards imported yet.", "No cards");
            return;
        }

        SaveCurrentEdits();

        int next = _currentIndex + direction;

        if (next < 0)
            next = 0;

        if (next >= _cards.Count)
            next = _cards.Count - 1;

        _currentIndex = next;
        SyncListSelection();
        UpdatePreview();
    }

    private void SaveCard()
    {
        SaveCurrentEdits();
        RefreshCardList();
        SyncListSelection();
        SetStatus("Card saved.");
    }

    private void SaveCurrentEdits()
    {
        if (_currentIndex < 0 || _currentIndex >= _cards.Count)
            return;

        _cards[_currentIndex].Front = txtFront.Text.Trim();
        _cards[_currentIndex].Back = txtBack.Text.Trim();
        _cards[_currentIndex].Tags = txtTags.Text.Trim();
    }

    private void DeleteCurrentCard()
    {
        if (_currentIndex < 0 || _currentIndex >= _cards.Count)
        {
            MessageBox.Show("No card selected.", "No card");
            return;
        }

        var result = MessageBox.Show("Delete this card?", "Confirm delete", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

        if (result != DialogResult.Yes)
            return;

        _cards.RemoveAt(_currentIndex);

        if (_cards.Count == 0)
            _currentIndex = -1;
        else if (_currentIndex >= _cards.Count)
            _currentIndex = _cards.Count - 1;

        RefreshCardList();
        SyncListSelection();
        UpdatePreview();
        SetStatus("Card deleted.");
    }

    private void CopyCurrent()
    {
        SaveCurrentEdits();

        if (_currentIndex < 0 || _currentIndex >= _cards.Count)
        {
            MessageBox.Show("No card selected.", "No card");
            return;
        }

        Clipboard.SetText(ToAnkiLine(_cards[_currentIndex]));
        SetStatus("Current card copied.");
        MessageBox.Show("Current card copied in Anki format.", "Copied");
    }

    private void CopyAll()
    {
        SaveCurrentEdits();

        if (_cards.Count == 0)
        {
            MessageBox.Show("No cards to copy.", "No cards");
            return;
        }

        Clipboard.SetText(GetAnkiText());
        SetStatus("All cards copied for Anki.");
        MessageBox.Show("All cards copied. Paste/import them into Anki as tab-separated fields.", "Copied");
    }

    private void ExportTxt()
    {
        SaveCurrentEdits();

        if (_cards.Count == 0)
        {
            MessageBox.Show("No cards to export.", "No cards");
            return;
        }

        using var sfd = new SaveFileDialog
        {
            Title = "Export Anki text file",
            Filter = "Text file|*.txt",
            FileName = "anki_flashcards.txt"
        };

        if (sfd.ShowDialog() == DialogResult.OK)
        {
            File.WriteAllText(sfd.FileName, GetAnkiText(), Encoding.UTF8);
            SetStatus("Exported .txt file.");
            MessageBox.Show("File exported successfully.", "Export complete");
        }
    }

    private string GetAnkiText()
    {
        if (_cards.Count == 0)
            return "";

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

    private string GetAccountSummary()
    {
        if (_currentUser is null)
            return "";

        return _currentUser.Email + "  •  " + _currentUser.Plan + "  •  " + FormatExpiry(_currentUser.SubscriptionExpiresAt);
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

    private static string TrimForMessage(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        return text.Length <= 1600 ? text : text.Substring(0, 1600) + "...";
    }

    private void SetStatus(string message)
    {
        _status.Text = message;
    }
}
