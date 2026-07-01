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

    // Modern Professional Dark Palette
    private readonly Color bg = Color.FromArgb(15, 23, 42);           // Slate 900
    private readonly Color panel = Color.FromArgb(30, 41, 59);        // Slate 800
    private readonly Color panel2 = Color.FromArgb(51, 65, 85);       // Slate 700
    private readonly Color accent = Color.FromArgb(59, 130, 246);     // Blue 500
    private readonly Color accentSuccess = Color.FromArgb(16, 185, 129); // Emerald 500
    private readonly Color accentDanger = Color.FromArgb(239, 68, 68);   // Red 500
    private readonly Color text = Color.FromArgb(248, 250, 252);      // Slate 50
    private readonly Color muted = Color.FromArgb(148, 163, 184);     // Slate 400
    private readonly Color input = Color.FromArgb(15, 23, 42);        // Slate 900

    public MainForm()
    {
        Text = "AI Flashcard Maker";
        Width = 1380;
        Height = 900;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1200, 800);
        BackColor = bg;

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
            BackColor = bg
        };

        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));

        Controls.Add(root);

        var hero = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(60),
            BackColor = panel
        };

        var heroLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 7,
            ColumnCount = 1,
            BackColor = panel
        };

        heroLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 15));
        heroLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 90));
        heroLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 100));
        heroLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        heroLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        heroLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        heroLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 85));

        hero.Controls.Add(heroLayout);

        heroLayout.Controls.Add(new Label
        {
            Text = "AI Flashcard Maker",
            ForeColor = text,
            Font = new Font("Segoe UI", 36, FontStyle.Bold),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.BottomLeft
        }, 0, 1);

        heroLayout.Controls.Add(new Label
        {
            Text = "Create high-yield Anki flashcards from your medical notes, textbooks, and AI responses in seconds.",
            ForeColor = muted,
            Font = new Font("Segoe UI", 14),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.TopLeft
        }, 0, 2);

        heroLayout.Controls.Add(MakeFeatureLabel("✓ Manual AI mode: No API key required"), 0, 3);
        heroLayout.Controls.Add(MakeFeatureLabel("✓ Preview, edit, and bulk export to Anki"), 0, 4);
        heroLayout.Controls.Add(MakeFeatureLabel("✓ Fast, clean, and distraction-free workflow"), 0, 5);

        root.Controls.Add(hero, 0, 0);

        var cardOuter = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(80, 100, 80, 100),
            BackColor = bg
        };

        var tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 12),
            ItemSize = new Size(150, 40)
        };

        var loginTab = new TabPage("Login") { BackColor = panel2 };
        var signupTab = new TabPage("Create Account") { BackColor = panel2 };

        tabs.TabPages.Add(loginTab);
        tabs.TabPages.Add(signupTab);
        cardOuter.Controls.Add(tabs);

        root.Controls.Add(cardOuter, 1, 0);

        BuildLoginTab(loginTab);
        BuildSignupTab(signupTab);
    }

    private Label MakeFeatureLabel(string value)
    {
        return new Label
        {
            Text = value,
            ForeColor = accentSuccess,
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
            Padding = new Padding(40),
            BackColor = panel2
        };

        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        tab.Controls.Add(layout);

        layout.Controls.Add(new Label
        {
            Text = "Welcome Back",
            ForeColor = text,
            Font = new Font("Segoe UI", 24, FontStyle.Bold),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);

        ConfigureInput(txtLoginEmail, "Enter your email");
        ConfigureInput(txtLoginPassword, "Enter your password", password: true);

        layout.Controls.Add(MakeSmallLabel("Email Address"), 0, 1);
        layout.Controls.Add(txtLoginEmail, 0, 2);
        layout.Controls.Add(MakeSmallLabel("Password"), 0, 3);
        layout.Controls.Add(txtLoginPassword, 0, 4);

        var btnLogin = MakeButton("Sign In", (_, _) => Login(), accent, 180);
        btnLogin.Height = 50;
        btnLogin.Font = new Font("Segoe UI", 12, FontStyle.Bold);
        
        var btnPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
        btnPanel.Controls.Add(btnLogin);
        layout.Controls.Add(btnPanel, 0, 5);

        layout.Controls.Add(new Label
        {
            Text = "New users must create an account using an activation code on the next tab.",
            ForeColor = muted,
            Font = new Font("Segoe UI", 11),
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
            Padding = new Padding(40),
            BackColor = panel2
        };

        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 55));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 55));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 55));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 110));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        tab.Controls.Add(layout);

        layout.Controls.Add(new Label
        {
            Text = "Create Account",
            ForeColor = text,
            Font = new Font("Segoe UI", 24, FontStyle.Bold),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);

        ConfigureInput(txtSignupEmail, "Enter email address");
        ConfigureInput(txtSignupPassword, "Choose a password", password: true);
        ConfigureInput(txtSignupCode, "e.g., FLASH-MONTH-2026");

        layout.Controls.Add(MakeSmallLabel("Email"), 0, 1);
        layout.Controls.Add(txtSignupEmail, 0, 2);
        layout.Controls.Add(MakeSmallLabel("Password"), 0, 3);
        layout.Controls.Add(txtSignupPassword, 0, 4);
        layout.Controls.Add(MakeSmallLabel("Activation Code"), 0, 5);
        layout.Controls.Add(txtSignupCode, 0, 6);

        var btnSignup = MakeButton("Create Account", (_, _) => Signup(), accentSuccess, 180);
        btnSignup.Height = 50;
        btnSignup.Font = new Font("Segoe UI", 12, FontStyle.Bold);

        var btnPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
        btnPanel.Controls.Add(btnSignup);
        layout.Controls.Add(btnPanel, 0, 7);

        layout.Controls.Add(new Label
        {
            Text = "Demo activation codes available:\n• FLASH-MONTH-2026\n• FLASH-YEAR-2026\n• FLASH-LIFE-2026",
            ForeColor = muted,
            Font = new Font("Consolas", 11),
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

        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));

        Controls.Add(root);

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Padding = new Padding(24, 15, 24, 15),
            BackColor = panel
        };

        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 400));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));

        header.Controls.Add(new Label
        {
            Text = "AI Flashcard Maker",
            ForeColor = text,
            Font = new Font("Segoe UI", 22, FontStyle.Bold),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);

        header.Controls.Add(new Label
        {
            Text = GetAccountSummary(),
            ForeColor = muted,
            Font = new Font("Segoe UI", 11),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleRight
        }, 1, 0);

        var btnLogout = MakeButton("Logout", (_, _) =>
        {
            _currentUser = null;
            BuildLoginUi();
        }, panel2, 130);
        
        header.Controls.Add(btnLogout, 2, 0);
        root.Controls.Add(header, 0, 0);

        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = bg
        };

        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        root.Controls.Add(body, 0, 1);

        var sidebar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            Padding = new Padding(15, 30, 15, 15),
            BackColor = input,
            WrapContents = false
        };

        sidebar.Controls.Add(MakeNavButton("1. Create Prompt", (_, _) => RenderPromptPage()));
        sidebar.Controls.Add(MakeNavButton("2. Import JSON", (_, _) => RenderImportPage()));
        sidebar.Controls.Add(MakeNavButton("3. Preview & Edit", (_, _) => RenderPreviewPage()));
        sidebar.Controls.Add(new Panel { Height = 20, BackColor = Color.Transparent }); // Spacer
        sidebar.Controls.Add(MakeNavButton("Manage Account", (_, _) => RenderAccountPage(), isAccent: false));

        body.Controls.Add(sidebar, 0, 0);

        _content = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(24),
            BackColor = bg
        };

        body.Controls.Add(_content, 1, 0);

        lblStatus.Text = " Ready.";
        lblStatus.ForeColor = muted;
        lblStatus.BackColor = panel;
        lblStatus.Dock = DockStyle.Fill;
        lblStatus.TextAlign = ContentAlignment.MiddleLeft;
        lblStatus.Font = new Font("Segoe UI", 10);
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
            RowCount = 3,
            ColumnCount = 1,
            BackColor = bg
        };

        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _content.Controls.Add(root);

        root.Controls.Add(MakePageHeader("Step 1: Generate AI Prompt", "Configure your settings, paste your notes, and generate a prompt for ChatGPT/Gemini."), 0, 0);

        var optionsContainer = new Panel { Dock = DockStyle.Fill, BackColor = panel, Padding = new Padding(10) };
        optionsContainer.Controls.Add(BuildOptionsBar());
        root.Controls.Add(optionsContainer, 0, 1);

        var split = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = bg
        };
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        root.Controls.Add(split, 0, 2);

        var left = MakeCardPanel();
        var right = MakeCardPanel();
        split.Controls.Add(left, 0, 0);
        split.Controls.Add(right, 1, 0);

        var leftLayout = MakeCardLayout("Source Material");
        left.Controls.Add(leftLayout);

        txtSource.Multiline = true;
        txtSource.ScrollBars = ScrollBars.Vertical;
        txtSource.Font = new Font("Segoe UI", 11);
        ConfigureInput(txtSource, "Paste notes, lectures, or image descriptions here...", multiline: true);
        leftLayout.Controls.Add(txtSource, 0, 1);

        var leftButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = panel, Padding = new Padding(0, 10, 0, 0) };
        leftButtons.Controls.Add(MakeButton("Generate Prompt", (_, _) => CreatePrompt(), accent, 160));
        leftButtons.Controls.Add(MakeButton("Clear", (_, _) => txtSource.Clear(), panel2, 100));
        leftLayout.Controls.Add(leftButtons, 0, 2);

        var rightLayout = MakeCardLayout("Generated AI Prompt");
        right.Controls.Add(rightLayout);

        txtPrompt.Multiline = true;
        txtPrompt.ScrollBars = ScrollBars.Vertical;
        txtPrompt.Font = new Font("Consolas", 11);
        ConfigureInput(txtPrompt, "Your generated prompt will appear here...", multiline: true);
        rightLayout.Controls.Add(txtPrompt, 0, 1);

        var rightButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = panel, Padding = new Padding(0, 10, 0, 0) };
        rightButtons.Controls.Add(MakeButton("Copy Prompt", (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(txtPrompt.Text))
            {
                MessageBox.Show("Create a prompt first.", "Notice", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            Clipboard.SetText(txtPrompt.Text);
            SetStatus("Prompt copied. Paste it into ChatGPT/Gemini/Copilot.");
            MessageBox.Show("Prompt copied! Now paste this into your AI of choice, then copy its JSON response.", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }, accentSuccess, 160));
        rightButtons.Controls.Add(MakeButton("Next Step ➔", (_, _) => RenderImportPage(), panel2, 140));
        
        rightLayout.Controls.Add(rightButtons, 0, 2);
    }

    private void RenderImportPage()
    {
        _content.Controls.Clear();

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 2,
            ColumnCount = 1,
            BackColor = bg
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _content.Controls.Add(root);

        root.Controls.Add(MakePageHeader("Step 2: Import AI JSON", "Paste the JSON array output from the AI here to create your flashcards."), 0, 0);

        var split = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = bg
        };
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        root.Controls.Add(split, 0, 1);

        var left = MakeCardPanel();
        var right = MakeCardPanel();
        split.Controls.Add(left, 0, 0);
        split.Controls.Add(right, 1, 0);

        var leftLayout = MakeCardLayout("Paste AI Output");
        left.Controls.Add(leftLayout);

        txtImport.Multiline = true;
        txtImport.ScrollBars = ScrollBars.Vertical;
        txtImport.Font = new Font("Consolas", 11);
        ConfigureInput(txtImport, "Paste the resulting JSON here...", multiline: true);
        leftLayout.Controls.Add(txtImport, 0, 1);

        var importButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = panel, Padding = new Padding(0, 10, 0, 0) };
        importButtons.Controls.Add(MakeButton("Import Cards", (_, _) => ImportCards(), accent, 140));
        importButtons.Controls.Add(MakeButton("Clear", (_, _) => txtImport.Clear(), panel2, 100));
        leftLayout.Controls.Add(importButtons, 0, 2);

        var rightLayout = MakeCardLayout("Imported Overview");
        right.Controls.Add(rightLayout);

        lstCards.BackColor = input;
        lstCards.ForeColor = text;
        lstCards.BorderStyle = BorderStyle.FixedSingle;
        lstCards.Font = new Font("Segoe UI", 11);
        lstCards.Dock = DockStyle.Fill;
        lstCards.SelectedIndexChanged -= LstCards_SelectedIndexChanged;
        lstCards.SelectedIndexChanged += LstCards_SelectedIndexChanged;

        rightLayout.Controls.Add(lstCards, 0, 1);

        var rightButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = panel, Padding = new Padding(0, 10, 0, 0) };
        rightButtons.Controls.Add(MakeButton("Next Step: Preview & Edit ➔", (_, _) => RenderPreviewPage(), accentSuccess, 240));
        rightLayout.Controls.Add(rightButtons, 0, 2);
    }

    private void RenderPreviewPage()
    {
        _content.Controls.Clear();

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 2,
            ColumnCount = 1,
            BackColor = bg
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _content.Controls.Add(root);

        root.Controls.Add(MakePageHeader("Step 3: Preview, Edit & Export", "Review your cards, make edits, and copy them directly to Anki."), 0, 0);

        var main = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 6,
            ColumnCount = 1,
            BackColor = panel,
            Padding = new Padding(20)
        };

        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        main.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
        main.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));

        root.Controls.Add(main, 0, 1);

        lblCounter.TextAlign = ContentAlignment.MiddleLeft;
        lblCounter.Font = new Font("Segoe UI", 16, FontStyle.Bold);
        lblCounter.ForeColor = accent;
        lblCounter.Dock = DockStyle.Fill;
        main.Controls.Add(lblCounter, 0, 0);

        ConfigureInput(txtFront, "Front of card", multiline: true);
        ConfigureInput(txtBack, "Back of card", multiline: true);
        ConfigureInput(txtTags, "Tags (e.g. Step1::Cardiology)");

        txtFront.Font = new Font("Segoe UI", 13);
        txtFront.BackColor = bg; // Make field stand out
        txtBack.Font = new Font("Segoe UI", 13);
        txtBack.BackColor = bg;
        txtTags.Font = new Font("Segoe UI", 12);
        txtTags.BackColor = bg;

        main.Controls.Add(Wrap("Front", txtFront), 0, 1);
        main.Controls.Add(Wrap("Back", txtBack), 0, 2);

        main.Controls.Add(Wrap("Tags", txtTags), 0, 3);

        var navButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = panel, Padding = new Padding(0, 10, 0, 0) };
        navButtons.Controls.Add(MakeButton("⬅ Previous", (_, _) =>
        {
            SaveCurrentEdits();
            if (_currentIndex > 0) _currentIndex--;
            SyncListSelection();
            UpdatePreview();
        }, panel2, 130));

        navButtons.Controls.Add(MakeButton("Next ➡", (_, _) =>
        {
            SaveCurrentEdits();
            if (_currentIndex < _cards.Count - 1) _currentIndex++;
            SyncListSelection();
            UpdatePreview();
        }, panel2, 110));

        navButtons.Controls.Add(new Panel { Width = 30, Height = 1, BackColor = Color.Transparent }); // Spacer

        navButtons.Controls.Add(MakeButton("Save Edit", (_, _) =>
        {
            SaveCurrentEdits();
            RefreshCardList();
            SetStatus("Card saved.");
        }, accent, 120));

        navButtons.Controls.Add(MakeButton("Delete Card", (_, _) => DeleteCurrentCard(), accentDanger, 130));
        navButtons.Controls.Add(MakeButton("Copy Current", (_, _) => CopyCurrent(), accentSuccess, 150));

        main.Controls.Add(navButtons, 0, 4);

        var bottomButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = panel, Padding = new Padding(0, 10, 0, 0) };
        bottomButtons.Controls.Add(MakeButton("Copy ALL For Anki", (_, _) => CopyAll(), accentSuccess, 200));
        bottomButtons.Controls.Add(MakeButton("Export .txt File", (_, _) => ExportTxt(), panel2, 160));
        main.Controls.Add(bottomButtons, 0, 5);

        UpdatePreview();
    }

    private void RenderAccountPage()
    {
        _content.Controls.Clear();

        var card = MakeCardPanel();
        card.Padding = new Padding(40);
        _content.Controls.Add(card);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 8,
            ColumnCount = 1,
            BackColor = panel
        };

        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        card.Controls.Add(layout);

        layout.Controls.Add(new Label
        {
            Text = "Account Management",
            ForeColor = text,
            Font = new Font("Segoe UI", 24, FontStyle.Bold),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);

        layout.Controls.Add(MakeInfoLabel("Email: " + (_currentUser?.Email ?? "")), 0, 1);
        layout.Controls.Add(MakeInfoLabel("Plan: " + (_currentUser?.Plan ?? "")), 0, 2);
        layout.Controls.Add(MakeInfoLabel("Expires: " + FormatExpiry(_currentUser?.SubscriptionExpiresAt ?? DateTime.UtcNow)), 0, 3);

        layout.Controls.Add(MakeSmallLabel("Apply New Activation Code"), 0, 4);

        var txtNewCode = new TextBox();
        ConfigureInput(txtNewCode, "e.g., FLASH-YEAR-2026");
        layout.Controls.Add(txtNewCode, 0, 5);

        var btnPanel = new FlowLayoutPanel { Dock = DockStyle.Fill };
        btnPanel.Controls.Add(MakeButton("Apply Code", (_, _) => ApplyActivationCodeToCurrentUser(txtNewCode.Text), accentSuccess, 160));
        layout.Controls.Add(btnPanel, 0, 6);

        layout.Controls.Add(new Label
        {
            Text = "Note: This login/activation is a local demo. For real subscriptions, this will connect to a secure backend.",
            ForeColor = muted,
            Font = new Font("Segoe UI", 11),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.TopLeft
        }, 0, 7);
    }

    private Control MakePageHeader(string title, string subtitle)
    {
        var pnl = new Panel { Dock = DockStyle.Fill };
        var lblSub = new Label { Text = subtitle, ForeColor = muted, Font = new Font("Segoe UI", 11), Dock = DockStyle.Bottom, Height = 25 };
        var lblTitle = new Label { Text = title, ForeColor = text, Font = new Font("Segoe UI", 16, FontStyle.Bold), Dock = DockStyle.Top, Height = 30 };
        pnl.Controls.Add(lblSub);
        pnl.Controls.Add(lblTitle);
        return pnl;
    }

    private FlowLayoutPanel BuildOptionsBar()
    {
        var box = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = panel,
            WrapContents = true,
            AutoScroll = true
        };

        void AddOption(string label, Control control)
        {
            var pnl = new FlowLayoutPanel { Width = 170, Height = 60, FlowDirection = FlowDirection.TopDown, BackColor = panel };
            pnl.Controls.Add(MakeSmallLabel(label));
            control.Width = 160;
            pnl.Controls.Add(control);
            box.Controls.Add(pnl);
        }

        AddOption("Mode", cboMode);
        AddOption("Difficulty", cboDifficulty);
        AddOption("Answer Length", cboAnswerLength);
        AddOption("Card Count", cboCount);
        AddOption("Language", cboLanguage);

        return box;
    }

    private Panel MakeCardPanel()
    {
        return new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(10),
            BackColor = bg
        };
    }

    private TableLayoutPanel MakeCardLayout(string title)
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 3,
            ColumnCount = 1,
            BackColor = panel,
            Padding = new Padding(15)
        };

        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));

        layout.Controls.Add(new Label
        {
            Text = title,
            ForeColor = text,
            Font = new Font("Segoe UI", 14, FontStyle.Bold),
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
            Padding = new Padding(15, 20, 15, 15),
            ForeColor = muted,
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
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
            Dock = DockStyle.Top,
            Height = 22,
            TextAlign = ContentAlignment.BottomLeft
        };
    }

    private Label MakeInfoLabel(string value)
    {
        return new Label
        {
            Text = value,
            ForeColor = text,
            Font = new Font("Segoe UI", 13),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        };
    }

    private void AddHoverEffect(Button btn, Color baseColor)
    {
        // Simple brightener for hover
        Color hoverColor = ControlPaint.Light(baseColor, 0.15f);
        btn.MouseEnter += (s, e) => btn.BackColor = hoverColor;
        btn.MouseLeave += (s, e) => btn.BackColor = baseColor;
    }

    private Button MakeButton(string label, EventHandler click, Color color, int width = 120)
    {
        var btn = new Button
        {
            Text = label,
            Width = width,
            Height = 42,
            FlatStyle = FlatStyle.Flat,
            BackColor = color,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 11, FontStyle.Bold),
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false,
            Margin = new Padding(0, 0, 10, 0)
        };

        btn.FlatAppearance.BorderSize = 0;
        btn.Click += click;
        AddHoverEffect(btn, color);
        
        return btn;
    }

    private Button MakeNavButton(string label, EventHandler click, bool isAccent = true)
    {
        Color btnColor = isAccent ? panel2 : bg;
        var btn = MakeButton(label, click, btnColor, 230);
        btn.Height = 50;
        btn.TextAlign = ContentAlignment.MiddleLeft;
        btn.Padding = new Padding(10, 0, 0, 0);
        btn.Margin = new Padding(0, 0, 0, 10);
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
        tb.Font = new Font("Segoe UI", 12);
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
            cb.Font = new Font("Segoe UI", 11);
            cb.Dock = DockStyle.Fill;
            cb.DropDownStyle = ComboBoxStyle.DropDownList;
            cb.FlatStyle = FlatStyle.Flat;
        }
    }

    private void Login()
    {
        string email = NormalizeEmail(txtLoginEmail.Text);
        string password = txtLoginPassword.Text;

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            MessageBox.Show("Please enter your email and password.", "Required", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var user = _store.Accounts.FirstOrDefault(x =>
            string.Equals(x.Email, email, StringComparison.OrdinalIgnoreCase));

        if (user is null)
        {
            MessageBox.Show("Account not found. Please check your email or create a new account.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        if (user.PasswordHash != HashPassword(email, password))
        {
            MessageBox.Show("Incorrect password.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        if (DateTime.UtcNow > user.SubscriptionExpiresAt)
        {
            MessageBox.Show("Your activation has expired. Please apply a new activation code on the Account page.", "Expired", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            // Still log them in so they can access the account page
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
            MessageBox.Show("Please enter a valid email address.", "Invalid Email", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (password.Length < 4)
        {
            MessageBox.Show("Password should be at least 4 characters.", "Weak Password", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (_store.Accounts.Any(x => string.Equals(x.Email, email, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show("This email is already registered. Please login instead.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var activation = GetActivationInfo(code);

        if (activation is null)
        {
            MessageBox.Show("Invalid activation code. Try 'FLASH-MONTH-2026'.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        if (IsCodeUsed(code))
        {
            MessageBox.Show("This activation code has already been used.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
        MessageBox.Show("Account created successfully!", "Welcome", MessageBoxButtons.OK, MessageBoxIcon.Information);
        BuildAppUi();
    }

    private void ApplyActivationCodeToCurrentUser(string rawCode)
    {
        if (_currentUser is null) return;

        string code = NormalizeCode(rawCode);
        var activation = GetActivationInfo(code);

        if (activation is null)
        {
            MessageBox.Show("Invalid activation code.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        if (IsCodeUsed(code))
        {
            MessageBox.Show("This activation code has already been used.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
        MessageBox.Show("Activation applied successfully!", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
        if (_currentUser is null) return "";
        return $"{_currentUser.Email} | {_currentUser.Plan} Plan";
    }

    private static string FormatExpiry(DateTime expiry)
    {
        if (expiry.Year > 9000) return "Lifetime";
        return expiry.ToLocalTime().ToString("MMM dd, yyyy");
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
        File.WriteAllText(StorePath, JsonSerializer.Serialize(_store, new JsonSerializerOptions { WriteIndented = true }));
    }

    private void CreatePrompt()
    {
        string source = txtSource.Text.Trim();

        if (string.IsNullOrWhiteSpace(source))
        {
            source = "[Paste or upload the source material here. If this is an image, analyze the uploaded image.]";
        }

        txtPrompt.Text = BuildManualPrompt(source);
        
        try 
        { 
            Clipboard.SetText(txtPrompt.Text); 
            SetStatus("Prompt created and copied to clipboard.");
        } 
        catch 
        {
            SetStatus("Prompt created. (Could not auto-copy, please copy manually).");
        }
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
            "You are an expert Anki flashcard creator for students.",
            "",
            "Create high-yield flashcards from the source material I provide.",
            "",
            "IMPORTANT OUTPUT RULES:",
            "- Return ONLY valid JSON.",
            "- Do not use markdown around the JSON.",
            "- Do not use code fences.",
            "- Do not explain anything outside the JSON.",
            "- Use this exact JSON structure:",
            "[",
            "  {",
            "    \"front\": \"question or cloze text\",",
            "    \"back\": \"answer\",",
            "    \"tags\": \"Topic::Subtopic\"",
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
            "- Keep tags short and useful without spaces.",
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
                MessageBox.Show("Please paste the AI JSON output first.", "Empty Input", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var parsed = ParseFlashcards(textToImport);

            if (parsed.Count == 0)
            {
                MessageBox.Show("No cards found. Make sure the AI returned proper JSON.", "Parse Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            _cards.Clear();
            _cards.AddRange(parsed);
            
            RefreshCardList();
            
            // Force selection to first item and trigger preview update
            _currentIndex = 0;
            if (lstCards.Items.Count > 0)
            {
                lstCards.SelectedIndex = -1; // reset
                lstCards.SelectedIndex = 0;  // trigger event
            }
            
            UpdatePreview();

            SetStatus($"Successfully imported {_cards.Count} cards.");
            MessageBox.Show($"Successfully parsed {_cards.Count} cards. You can now Preview and Edit them.", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
            RenderPreviewPage();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not import cards:\n\n" + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
        if (el.ValueKind != JsonValueKind.Object) return "";

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
        var lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.StartsWith("Front", StringComparison.OrdinalIgnoreCase) && line.Contains("Back", StringComparison.OrdinalIgnoreCase)) continue;

            string[] parts = line.Split('\t');
            if (parts.Length >= 2)
            {
                list.Add(new Flashcard
                {
                    Front = parts[0].Trim(),
                    Back = parts[1].Trim(),
                    Tags = parts.Length >= 3 && !string.IsNullOrWhiteSpace(parts[2]) ? parts[2].Trim() : "AIFlashcards"
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
            lstCards.Items.Add($"{i + 1}. {front}");
        }
    }

    private void UpdatePreview()
    {
        if (_cards.Count == 0 || _currentIndex < 0 || _currentIndex >= _cards.Count)
        {
            lblCounter.Text = "No cards available.";
            txtFront.Text = "";
            txtBack.Text = "";
            txtTags.Text = "";
            return;
        }

        var card = _cards[_currentIndex];
        lblCounter.Text = $"Card {_currentIndex + 1} of {_cards.Count}";
        txtFront.Text = card.Front;
        txtBack.Text = card.Back;
        txtTags.Text = card.Tags;
    }

    private void SaveCurrentEdits()
    {
        if (_currentIndex < 0 || _currentIndex >= _cards.Count) return;

        _cards[_currentIndex].Front = txtFront.Text.Trim();
        _cards[_currentIndex].Back = txtBack.Text.Trim();
        _cards[_currentIndex].Tags = txtTags.Text.Trim();
    }

    private void DeleteCurrentCard()
    {
        if (_currentIndex < 0 || _currentIndex >= _cards.Count) return;

        _cards.RemoveAt(_currentIndex);

        if (_cards.Count == 0) _currentIndex = -1;
        else if (_currentIndex >= _cards.Count) _currentIndex = _cards.Count - 1;

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
            MessageBox.Show("No card selected.", "Notice", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        
        try
        {
            Clipboard.SetText(ToAnkiLine(_cards[_currentIndex]));
            SetStatus("Current card copied.");
            MessageBox.Show("Card copied to clipboard! Paste directly into Anki.", "Copied", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch { SetStatus("Failed to access clipboard."); }
    }

    private void CopyAll()
    {
        SaveCurrentEdits();
        if (_cards.Count == 0)
        {
            MessageBox.Show("No cards to copy.", "Notice", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            Clipboard.SetText(GetAnkiText());
            SetStatus("All cards copied for Anki.");
            MessageBox.Show($"Successfully copied {_cards.Count} cards!\n\nOpen Anki, go to 'Import', and paste.", "Ready for Anki", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch { SetStatus("Failed to access clipboard."); }
    }

    private void ExportTxt()
    {
        SaveCurrentEdits();
        if (_cards.Count == 0)
        {
            MessageBox.Show("No cards to export.", "Notice", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var sfd = new SaveFileDialog
        {
            Title = "Export Anki Text File",
            Filter = "Text file|*.txt",
            FileName = "anki_flashcards.txt"
        };

        if (sfd.ShowDialog() == DialogResult.OK)
        {
            File.WriteAllText(sfd.FileName, GetAnkiText(), Encoding.UTF8);
            SetStatus("Exported .txt file successfully.");
            MessageBox.Show("File saved successfully. You can import this .txt file directly into Anki.", "Export Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private string GetAnkiText()
    {
        return string.Join(Environment.NewLine, _cards.Select(ToAnkiLine));
    }

    private static string ToAnkiLine(Flashcard card)
    {
        return $"{CleanField(card.Front)}\t{CleanField(card.Back)}\t{CleanField(card.Tags)}";
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
        lblStatus.Text = $" {message}";
    }
}
