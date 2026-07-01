using System.Drawing;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

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

public sealed class MainForm : Form
{
    private readonly string dataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AIFlashcardMaker");

    private string StorePath => Path.Combine(dataDir, "accounts.json");

    private readonly List<Flashcard> _cards = new();
    private LocalStore _store = new();
    private LocalAccount? _currentUser;
    private int _currentIndex = -1;

    private Panel _content = new();

    private readonly TextBox txtLoginEmail = new();
    private readonly TextBox txtLoginPassword = new();

    private readonly TextBox txtSignupEmail = new();
    private readonly TextBox txtSignupPassword = new();
    private readonly TextBox txtSignupCode = new();

    private readonly TextBox txtSource = new();
    private readonly TextBox txtPrompt = new();
    private readonly TextBox txtImport = new();

    private readonly TextBox txtFront = new();
    private readonly TextBox txtBack = new();
    private readonly TextBox txtTags = new();

    private readonly ComboBox cboMode = new();
    private readonly ComboBox cboDifficulty = new();
    private readonly ComboBox cboAnswerLength = new();
    private readonly ComboBox cboCount = new();
    private readonly ComboBox cboLanguage = new();

    private readonly ListBox lstCards = new();
    private readonly Label lblStatus = new();
    private readonly Label lblCounter = new();

    private readonly Color bg = Color.FromArgb(18, 22, 33);
    private readonly Color panel = Color.FromArgb(28, 34, 49);
    private readonly Color panel2 = Color.FromArgb(35, 42, 60);
    private readonly Color accent = Color.FromArgb(91, 141, 239);
    private readonly Color accent2 = Color.FromArgb(94, 210, 171);
    private readonly Color text = Color.FromArgb(235, 239, 245);
    private readonly Color muted = Color.FromArgb(165, 174, 190);
    private readonly Color input = Color.FromArgb(12, 16, 24);

    public MainForm()
    {
        Text = "AI Flashcard Maker";
        Width = 1280;
        Height = 860;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1100, 720);

        LoadStore();
        BuildLoginUi();
    }

    private void BuildLoginUi()
    {
        Controls.Clear();
        BackColor = bg;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(28),
            BackColor = bg
        };

        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));

        Controls.Add(root);

        var hero = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(28),
            BackColor = bg
        };

        var heroLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 7,
            ColumnCount = 1,
            BackColor = bg
        };

        heroLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 18));
        heroLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
        heroLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 90));
        heroLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
        heroLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
        heroLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
        heroLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 82));

        hero.Controls.Add(heroLayout);

        heroLayout.Controls.Add(new Label
        {
            Text = "AI Flashcard Maker",
            ForeColor = text,
            Font = new Font("Segoe UI", 30, FontStyle.Bold),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.BottomLeft
        }, 0, 1);

        heroLayout.Controls.Add(new Label
        {
            Text = "Create Anki-ready flashcards from notes, screenshots, lectures, and AI responses.",
            ForeColor = muted,
            Font = new Font("Segoe UI", 13),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.TopLeft
        }, 0, 2);

        heroLayout.Controls.Add(MakeFeatureLabel("✓ Manual AI mode: no API key required"), 0, 3);
        heroLayout.Controls.Add(MakeFeatureLabel("✓ Preview, edit, delete, copy, export"), 0, 4);
        heroLayout.Controls.Add(MakeFeatureLabel("✓ Account screen + activation code demo"), 0, 5);

        root.Controls.Add(hero, 0, 0);

        var cardOuter = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(40, 70, 40, 70),
            BackColor = bg
        };

        var card = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(28),
            BackColor = panel
        };

        cardOuter.Controls.Add(card);
        root.Controls.Add(cardOuter, 1, 0);

        var tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 11),
            BackColor = panel,
            ForeColor = text
        };

        var loginTab = new TabPage("Login");
        var signupTab = new TabPage("Create Account");

        loginTab.BackColor = panel;
        signupTab.BackColor = panel;

        tabs.TabPages.Add(loginTab);
        tabs.TabPages.Add(signupTab);
        card.Controls.Add(tabs);

        BuildLoginTab(loginTab);
        BuildSignupTab(signupTab);
    }

    private Label MakeFeatureLabel(string value)
    {
        return new Label
        {
            Text = value,
            ForeColor = accent2,
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
            RowCount = 8,
            ColumnCount = 1,
            Padding = new Padding(18),
            BackColor = panel
        };

        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 65));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        tab.Controls.Add(layout);

        layout.Controls.Add(new Label
        {
            Text = "Welcome back",
            ForeColor = text,
            Font = new Font("Segoe UI", 22, FontStyle.Bold),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);

        ConfigureInput(txtLoginEmail, "Email");
        ConfigureInput(txtLoginPassword, "Password", password: true);

        layout.Controls.Add(MakeSmallLabel("Email"), 0, 1);
        layout.Controls.Add(txtLoginEmail, 0, 2);
        layout.Controls.Add(MakeSmallLabel("Password"), 0, 3);
        layout.Controls.Add(txtLoginPassword, 0, 4);

        var btnLogin = MakeButton("Login", (_, _) => Login(), accent);
        btnLogin.Height = 44;
        layout.Controls.Add(btnLogin, 0, 5);

        layout.Controls.Add(new Label
        {
            Text = "New users must create an account using an activation code.",
            ForeColor = muted,
            Font = new Font("Segoe UI", 10),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.TopLeft
        }, 0, 6);
    }

    private void BuildSignupTab(TabPage tab)
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 10,
            ColumnCount = 1,
            Padding = new Padding(18),
            BackColor = panel
        };

        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 65));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 110));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        tab.Controls.Add(layout);

        layout.Controls.Add(new Label
        {
            Text = "Create account",
            ForeColor = text,
            Font = new Font("Segoe UI", 22, FontStyle.Bold),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);

        ConfigureInput(txtSignupEmail, "Email");
        ConfigureInput(txtSignupPassword, "Password", password: true);
        ConfigureInput(txtSignupCode, "Activation code");

        layout.Controls.Add(MakeSmallLabel("Email"), 0, 1);
        layout.Controls.Add(txtSignupEmail, 0, 2);
        layout.Controls.Add(MakeSmallLabel("Password"), 0, 3);
        layout.Controls.Add(txtSignupPassword, 0, 4);
        layout.Controls.Add(MakeSmallLabel("Activation Code"), 0, 5);
        layout.Controls.Add(txtSignupCode, 0, 6);

        var btnSignup = MakeButton("Create Account", (_, _) => Signup(), accent2);
        btnSignup.Height = 44;
        layout.Controls.Add(btnSignup, 0, 7);

        layout.Controls.Add(new Label
        {
            Text = "Demo activation codes:\nFLASH-MONTH-2026\nFLASH-YEAR-2026\nFLASH-LIFE-2026",
            ForeColor = muted,
            Font = new Font("Consolas", 10),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.TopLeft
        }, 0, 8);
    }

    private void BuildAppUi()
    {
        Controls.Clear();
        BackColor = bg;

        EnsureOptionBoxes();

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 3,
            ColumnCount = 1,
            BackColor = bg
        };

        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));

        Controls.Add(root);

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Padding = new Padding(18, 10, 18, 10),
            BackColor = panel
        };

        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 360));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));

        header.Controls.Add(new Label
        {
            Text = "AI Flashcard Maker",
            ForeColor = text,
            Font = new Font("Segoe UI", 20, FontStyle.Bold),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);

        header.Controls.Add(new Label
        {
            Text = GetAccountSummary(),
            ForeColor = muted,
            Font = new Font("Segoe UI", 10),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleRight
        }, 1, 0);

        header.Controls.Add(MakeButton("Logout", (_, _) =>
        {
            _currentUser = null;
            BuildLoginUi();
        }, Color.FromArgb(70, 78, 98)), 2, 0);

        root.Controls.Add(header, 0, 0);

        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = bg
        };

        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        root.Controls.Add(body, 0, 1);

        var sidebar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            Padding = new Padding(14),
            BackColor = Color.FromArgb(14, 18, 28),
            WrapContents = false
        };

        sidebar.Controls.Add(MakeNavButton("1. Create Prompt", (_, _) => RenderPromptPage()));
        sidebar.Controls.Add(MakeNavButton("2. Import JSON", (_, _) => RenderImportPage()));
        sidebar.Controls.Add(MakeNavButton("3. Preview / Edit", (_, _) => RenderPreviewPage()));
        sidebar.Controls.Add(MakeNavButton("4. Account", (_, _) => RenderAccountPage()));

        body.Controls.Add(sidebar, 0, 0);

        _content = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18),
            BackColor = bg
        };

        body.Controls.Add(_content, 1, 0);

        lblStatus.Text = "Ready.";
        lblStatus.ForeColor = muted;
        lblStatus.BackColor = panel;
        lblStatus.Dock = DockStyle.Fill;
        lblStatus.TextAlign = ContentAlignment.MiddleLeft;
        lblStatus.Padding = new Padding(15, 0, 0, 0);

        root.Controls.Add(lblStatus, 0, 2);

        RenderPromptPage();
    }

    private void RenderPromptPage()
    {
        _content.Controls.Clear();

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 2,
            ColumnCount = 1,
            BackColor = bg
        };

        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 86));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _content.Controls.Add(root);

        root.Controls.Add(BuildOptionsBar(), 0, 0);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterDistance = 520,
            BackColor = bg
        };

        root.Controls.Add(split, 0, 1);

        var left = MakeCardPanel();
        var right = MakeCardPanel();

        split.Panel1.Controls.Add(left);
        split.Panel2.Controls.Add(right);

        var leftLayout = MakeCardLayout("Source Material");
        left.Controls.Add(leftLayout);

        txtSource.Multiline = true;
        txtSource.ScrollBars = ScrollBars.Vertical;
        txtSource.Font = new Font("Segoe UI", 11);
        ConfigureInput(txtSource, "Paste notes here...", multiline: true);
        leftLayout.Controls.Add(txtSource, 0, 1);

        var leftButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = panel };
        leftButtons.Controls.Add(MakeButton("Create Prompt", (_, _) => CreatePrompt(), accent));
        leftButtons.Controls.Add(MakeButton("Clear", (_, _) => txtSource.Clear(), Color.FromArgb(70, 78, 98)));
        leftLayout.Controls.Add(leftButtons, 0, 2);

        var rightLayout = MakeCardLayout("AI Prompt");
        right.Controls.Add(rightLayout);

        txtPrompt.Multiline = true;
        txtPrompt.ScrollBars = ScrollBars.Vertical;
        txtPrompt.Font = new Font("Consolas", 10);
        ConfigureInput(txtPrompt, "Generated prompt will appear here...", multiline: true);
        rightLayout.Controls.Add(txtPrompt, 0, 1);

        var rightButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = panel };
        rightButtons.Controls.Add(MakeButton("Copy Prompt", (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(txtPrompt.Text))
            {
                MessageBox.Show("Create a prompt first.");
                return;
            }

            Clipboard.SetText(txtPrompt.Text);
            SetStatus("Prompt copied. Paste it into ChatGPT/Gemini/Copilot.");
        }, accent2));
        rightLayout.Controls.Add(rightButtons, 0, 2);
    }

    private void RenderImportPage()
    {
        _content.Controls.Clear();

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterDistance = 650,
            BackColor = bg
        };

        _content.Controls.Add(split);

        var left = MakeCardPanel();
        var right = MakeCardPanel();

        split.Panel1.Controls.Add(left);
        split.Panel2.Controls.Add(right);

        var leftLayout = MakeCardLayout("Paste AI JSON Answer");
        left.Controls.Add(leftLayout);

        txtImport.Multiline = true;
        txtImport.ScrollBars = ScrollBars.Vertical;
        txtImport.Font = new Font("Consolas", 10);
        ConfigureInput(txtImport, "Paste JSON here...", multiline: true);
        leftLayout.Controls.Add(txtImport, 0, 1);

        var importButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = panel };
        importButtons.Controls.Add(MakeButton("Import Cards", (_, _) => ImportCards(), accent));
        importButtons.Controls.Add(MakeButton("Clear", (_, _) => txtImport.Clear(), Color.FromArgb(70, 78, 98)));
        leftLayout.Controls.Add(importButtons, 0, 2);

        var rightLayout = MakeCardLayout("Imported Cards");
        right.Controls.Add(rightLayout);

        lstCards.BackColor = input;
        lstCards.ForeColor = text;
        lstCards.BorderStyle = BorderStyle.FixedSingle;
        lstCards.Font = new Font("Segoe UI", 10);
        lstCards.Dock = DockStyle.Fill;
        lstCards.SelectedIndexChanged -= LstCards_SelectedIndexChanged;
        lstCards.SelectedIndexChanged += LstCards_SelectedIndexChanged;

        rightLayout.Controls.Add(lstCards, 0, 1);

        var exportButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = panel };
        exportButtons.Controls.Add(MakeButton("Copy All For Anki", (_, _) => CopyAll(), accent2, 165));
        exportButtons.Controls.Add(MakeButton("Export .txt", (_, _) => ExportTxt(), Color.FromArgb(70, 78, 98)));
        rightLayout.Controls.Add(exportButtons, 0, 2);
    }

    private void RenderPreviewPage()
    {
        _content.Controls.Clear();

        var main = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 6,
            ColumnCount = 1,
            BackColor = bg
        };

        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
        main.RowStyles.Add(new RowStyle(SizeType.Percent, 38));
        main.RowStyles.Add(new RowStyle(SizeType.Percent, 38));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        main.RowStyles.Add(new RowStyle(SizeType.Percent, 24));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));

        _content.Controls.Add(main);

        lblCounter.TextAlign = ContentAlignment.MiddleLeft;
        lblCounter.Font = new Font("Segoe UI", 14, FontStyle.Bold);
        lblCounter.ForeColor = text;
        lblCounter.Dock = DockStyle.Fill;
        main.Controls.Add(lblCounter, 0, 0);

        ConfigureInput(txtFront, "Front", multiline: true);
        ConfigureInput(txtBack, "Back", multiline: true);
        ConfigureInput(txtTags, "Tags");

        txtFront.Font = new Font("Segoe UI", 12);
        txtBack.Font = new Font("Segoe UI", 12);
        txtTags.Font = new Font("Segoe UI", 11);

        main.Controls.Add(Wrap("Front", txtFront), 0, 1);
        main.Controls.Add(Wrap("Back", txtBack), 0, 2);

        var navButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = bg };

        navButtons.Controls.Add(MakeButton("Previous", (_, _) =>
        {
            SaveCurrentEdits();
            if (_currentIndex > 0) _currentIndex--;
            SyncListSelection();
            UpdatePreview();
        }, Color.FromArgb(70, 78, 98)));

        navButtons.Controls.Add(MakeButton("Next", (_, _) =>
        {
            SaveCurrentEdits();
            if (_currentIndex < _cards.Count - 1) _currentIndex++;
            SyncListSelection();
            UpdatePreview();
        }, Color.FromArgb(70, 78, 98)));

        navButtons.Controls.Add(MakeButton("Save Card", (_, _) =>
        {
            SaveCurrentEdits();
            RefreshCardList();
            SetStatus("Card saved.");
        }, accent));

        navButtons.Controls.Add(MakeButton("Delete Card", (_, _) => DeleteCurrentCard(), Color.FromArgb(160, 68, 78)));

        navButtons.Controls.Add(MakeButton("Copy Current", (_, _) => CopyCurrent(), accent2, 130));

        main.Controls.Add(navButtons, 0, 3);
        main.Controls.Add(Wrap("Tags", txtTags), 0, 4);

        var bottomButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = bg };
        bottomButtons.Controls.Add(MakeButton("Copy All For Anki", (_, _) => CopyAll(), accent2, 165));
        bottomButtons.Controls.Add(MakeButton("Export .txt", (_, _) => ExportTxt(), Color.FromArgb(70, 78, 98)));
        main.Controls.Add(bottomButtons, 0, 5);

        UpdatePreview();
    }

    private void RenderAccountPage()
    {
        _content.Controls.Clear();

        var card = MakeCardPanel();
        _content.Controls.Add(card);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 8,
            ColumnCount = 1,
            Padding = new Padding(24),
            BackColor = panel
        };

        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        card.Controls.Add(layout);

        layout.Controls.Add(new Label
        {
            Text = "Account",
            ForeColor = text,
            Font = new Font("Segoe UI", 22, FontStyle.Bold),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);

        layout.Controls.Add(MakeInfoLabel("Email: " + (_currentUser?.Email ?? "")), 0, 1);
        layout.Controls.Add(MakeInfoLabel("Plan: " + (_currentUser?.Plan ?? "")), 0, 2);
        layout.Controls.Add(MakeInfoLabel("Expires: " + FormatExpiry(_currentUser?.SubscriptionExpiresAt ?? DateTime.UtcNow)), 0, 3);

        layout.Controls.Add(MakeSmallLabel("Apply new activation code"), 0, 4);

        var txtNewCode = new TextBox();
        ConfigureInput(txtNewCode, "Activation code");
        layout.Controls.Add(txtNewCode, 0, 5);

        layout.Controls.Add(MakeButton("Apply Code", (_, _) =>
        {
            ApplyActivationCodeToCurrentUser(txtNewCode.Text);
        }, accent2, 140), 0, 6);

        layout.Controls.Add(new Label
        {
            Text = "Note: this login/activation is a local demo. For real monthly/yearly subscriptions, we will connect this to an online backend later.",
            ForeColor = muted,
            Font = new Font("Segoe UI", 10),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.TopLeft
        }, 0, 7);
    }

    private TableLayoutPanel BuildOptionsBar()
    {
        var box = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 10,
            RowCount = 1,
            Padding = new Padding(14),
            BackColor = panel
        };

        box.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60));
        box.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        box.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
        box.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 17));
        box.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        box.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 17));
        box.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 65));
        box.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 14));
        box.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
        box.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 27));

        box.Controls.Add(MakeSmallLabel("Mode"), 0, 0);
        box.Controls.Add(cboMode, 1, 0);
        box.Controls.Add(MakeSmallLabel("Difficulty"), 2, 0);
        box.Controls.Add(cboDifficulty, 3, 0);
        box.Controls.Add(MakeSmallLabel("Answer"), 4, 0);
        box.Controls.Add(cboAnswerLength, 5, 0);
        box.Controls.Add(MakeSmallLabel("Count"), 6, 0);
        box.Controls.Add(cboCount, 7, 0);
        box.Controls.Add(MakeSmallLabel("Language"), 8, 0);
        box.Controls.Add(cboLanguage, 9, 0);

        return box;
    }

    private Panel MakeCardPanel()
    {
        return new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            BackColor = panel
        };
    }

    private TableLayoutPanel MakeCardLayout(string title)
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 3,
            ColumnCount = 1,
            BackColor = panel
        };

        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));

        layout.Controls.Add(new Label
        {
            Text = title,
            ForeColor = text,
            Font = new Font("Segoe UI", 13, FontStyle.Bold),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);

        return layout;
    }

    private GroupBox Wrap(string title, Control control)
    {
        var box = new GroupBox
        {
            Text = title,
            Dock = DockStyle.Fill,
            Padding = new Padding(10),
            ForeColor = text,
            BackColor = panel
        };

        control.Dock = DockStyle.Fill;
        box.Controls.Add(control);
        return box;
    }

    private Label MakeSmallLabel(string value)
    {
        return new Label
        {
            Text = value,
            ForeColor = muted,
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        };
    }

    private Label MakeInfoLabel(string value)
    {
        return new Label
        {
            Text = value,
            ForeColor = text,
            Font = new Font("Segoe UI", 12),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        };
    }

    private Button MakeButton(string label, EventHandler click, Color color, int width = 120)
    {
        var btn = new Button
        {
            Text = label,
            Width = width,
            Height = 38,
            FlatStyle = FlatStyle.Flat,
            BackColor = color,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false
        };

        btn.FlatAppearance.BorderSize = 0;
        btn.Click += click;
        return btn;
    }

    private Button MakeNavButton(string label, EventHandler click)
    {
        var btn = MakeButton(label, click, panel2, 190);
        btn.Height = 46;
        btn.TextAlign = ContentAlignment.MiddleLeft;
        return btn;
    }

    private void ConfigureInput(TextBox tb, string placeholder, bool password = false, bool multiline = false)
    {
        tb.PlaceholderText = placeholder;
        tb.UseSystemPasswordChar = password;
        tb.Multiline = multiline;
        tb.ScrollBars = multiline ? ScrollBars.Vertical : ScrollBars.None;
        tb.BackColor = input;
        tb.ForeColor = text;
        tb.BorderStyle = BorderStyle.FixedSingle;
        tb.Font = new Font("Segoe UI", 11);
        tb.Dock = DockStyle.Fill;
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
            cboDifficulty.Items.AddRange(new object[] { "Easy", "Medium", "Hard", "Exam Style" });
            cboDifficulty.SelectedIndex = 3;
        }

        if (cboAnswerLength.Items.Count == 0)
        {
            cboAnswerLength.Items.AddRange(new object[] { "Very Short", "Normal", "Detailed" });
            cboAnswerLength.SelectedIndex = 0;
        }

        if (cboCount.Items.Count == 0)
        {
            cboCount.Items.AddRange(new object[] { "Auto", "5", "10", "20", "30", "40" });
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
            cb.BackColor = input;
            cb.ForeColor = text;
            cb.Font = new Font("Segoe UI", 10);
            cb.Dock = DockStyle.Fill;
            cb.DropDownStyle = ComboBoxStyle.DropDownList;
        }
    }

    private void Login()
    {
        string email = NormalizeEmail(txtLoginEmail.Text);
        string password = txtLoginPassword.Text;

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
            MessageBox.Show("Your activation expired. Apply a new activation code.");
            return;
        }

        _currentUser = user;
        BuildAppUi();
    }

    private void Signup()
    {
        string email = NormalizeEmail(txtSignupEmail.Text);
        string password = txtSignupPassword.Text;
        string code = NormalizeCode(txtSignupCode.Text);

        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
        {
            MessageBox.Show("Enter a valid email.");
            return;
        }

        if (password.Length < 4)
        {
            MessageBox.Show("Password should be at least 4 characters for this demo.");
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
        BuildAppUi();
    }

    private void ApplyActivationCodeToCurrentUser(string rawCode)
    {
        if (_currentUser is null) return;

        string code = NormalizeCode(rawCode);
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
        MessageBox.Show("Activation applied.");
        BuildAppUi();
        RenderAccountPage();
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

        return _currentUser.Email + " • " + _currentUser.Plan + " • " + FormatExpiry(_currentUser.SubscriptionExpiresAt);
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

    private void CreatePrompt()
    {
        string source = txtSource.Text.Trim();

        if (string.IsNullOrWhiteSpace(source))
        {
            source = "[Paste or upload the source material here. If this is an image, analyze the uploaded image.]";
        }

        txtPrompt.Text = BuildManualPrompt(source);
        Clipboard.SetText(txtPrompt.Text);
        SetStatus("Prompt created and copied. Paste it into ChatGPT/Gemini/Copilot.");
    }

    private string BuildManualPrompt(string source)
    {
        string mode = cboMode.Text;
        string difficulty = cboDifficulty.Text;
        string answerLength = cboAnswerLength.Text;
        string count = cboCount.Text;
        string language = cboLanguage.Text;

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
            "- If the source is an image, analyze the image and make cards from the visible text/diagram/table.",
            "- If the mode is Cloze Deletion, put the cloze deletion in the front field.",
            "- Keep tags short and useful.",
            "",
            "OPTIONS:",
            "- Mode: " + mode,
            "- Difficulty: " + difficulty,
            "- Answer length: " + answerLength,
            "- Number of cards: " + count,
            "- Language: " + language,
            "",
            "SOURCE MATERIAL:",
            source
        });
    }

    private void ImportCards()
    {
        try
        {
            string textToImport = txtImport.Text.Trim();

            if (string.IsNullOrWhiteSpace(textToImport))
            {
                MessageBox.Show("Paste the AI JSON first.");
                return;
            }

            var parsed = ParseFlashcards(textToImport);

            if (parsed.Count == 0)
            {
                MessageBox.Show("No cards found. Make sure the AI returned JSON with front, back, and tags.");
                return;
            }

            _cards.Clear();
            _cards.AddRange(parsed);
            _currentIndex = 0;

            RefreshCardList();
            SyncListSelection();
            UpdatePreview();

            SetStatus("Imported " + _cards.Count + " cards.");
            RenderPreviewPage();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not import cards:\n\n" + ex.Message);
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
            var list = new List<Flashcard>();

            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return TryParseTabSeparated(aiText);

            foreach (var el in doc.RootElement.EnumerateArray())
            {
                string front = GetPropertyText(el, "front", "question", "q");
                string back = GetPropertyText(el, "back", "answer", "a");
                string tags = GetPropertyText(el, "tags", "tag");

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
        catch
        {
            return TryParseTabSeparated(aiText);
        }
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
        if (lstCards.SelectedIndex >= 0)
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
            if (front.Length > 75) front = front.Substring(0, 75) + "...";
            lstCards.Items.Add((i + 1) + ". " + front);
        }
    }

    private void UpdatePreview()
    {
        if (_cards.Count == 0 || _currentIndex < 0 || _currentIndex >= _cards.Count)
        {
            lblCounter.Text = "No card selected.";
            txtFront.Text = "";
            txtBack.Text = "";
            txtTags.Text = "";
            return;
        }

        var card = _cards[_currentIndex];
        lblCounter.Text = "Card " + (_currentIndex + 1) + " / " + _cards.Count;
        txtFront.Text = card.Front;
        txtBack.Text = card.Back;
        txtTags.Text = card.Tags;
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

    private void SyncListSelection()
    {
        if (_currentIndex >= 0 && _currentIndex < lstCards.Items.Count)
            lstCards.SelectedIndex = _currentIndex;
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
        SetStatus("All cards copied for Anki.");
    }

    private void ExportTxt()
    {
        SaveCurrentEdits();

        if (_cards.Count == 0)
        {
            MessageBox.Show("No cards to export.");
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

    private void SetStatus(string message)
    {
        lblStatus.Text = message;
    }
}
