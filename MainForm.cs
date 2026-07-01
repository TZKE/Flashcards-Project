using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIFlashcardMaker;

public sealed class Flashcard
{
    public string Front { get; set; } = "";
    public string Back { get; set; } = "";
    public string Tags { get; set; } = "";
}

public sealed class AppSettings
{
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "gpt-4o-mini";
}

public sealed class MainForm : Form
{
    private readonly List<Flashcard> _cards = [];
    private int _currentIndex = -1;
    private string? _imagePath;

    private readonly TextBox txtApiKey = new();
    private readonly TextBox txtModel = new();
    private readonly TextBox txtInput = new();
    private readonly TextBox txtFront = new();
    private readonly TextBox txtBack = new();
    private readonly TextBox txtTags = new();

    private readonly ComboBox cboMode = new();
    private readonly ComboBox cboDifficulty = new();
    private readonly ComboBox cboAnswerLength = new();
    private readonly ComboBox cboCount = new();

    private readonly ListBox lstCards = new();
    private readonly Label lblStatus = new();
    private readonly Label lblCounter = new();
    private readonly Label lblImage = new();

    private readonly string settingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AIFlashcardMaker");

    private string SettingsPath =>
        Path.Combine(settingsDir, "settings.json");

    public MainForm()
    {
        Text = "AI Flashcard Maker";
        Width = 1250;
        Height = 850;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1050, 700);

        BuildUi();
        LoadSettings();
        UpdatePreview();
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(10)
        };

        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 95));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));

        Controls.Add(root);

        var settingsBox = new GroupBox
        {
            Text = "Settings",
            Dock = DockStyle.Fill
        };

        var settingsGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 7,
            RowCount = 2,
            Padding = new Padding(10)
        };

        settingsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 85));
        settingsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        settingsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        settingsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        settingsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        settingsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 125));
        settingsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 135));

        settingsBox.Controls.Add(settingsGrid);
        root.Controls.Add(settingsBox, 0, 0);

        txtApiKey.UseSystemPasswordChar = true;
        txtApiKey.PlaceholderText = "Paste your OpenAI API key here";
        txtApiKey.Dock = DockStyle.Fill;

        txtModel.Text = "gpt-4o-mini";
        txtModel.Dock = DockStyle.Fill;

        var btnSaveSettings = new Button
        {
            Text = "Save",
            Dock = DockStyle.Fill
        };
        btnSaveSettings.Click += (_, _) => SaveSettings();

        settingsGrid.Controls.Add(new Label { Text = "API Key:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        settingsGrid.Controls.Add(txtApiKey, 1, 0);
        settingsGrid.SetColumnSpan(txtApiKey, 5);
        settingsGrid.Controls.Add(btnSaveSettings, 6, 0);

        settingsGrid.Controls.Add(new Label { Text = "Model:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        settingsGrid.Controls.Add(txtModel, 1, 1);

        cboMode.Items.AddRange([
            "Step 1 High-Yield",
            "Basic Q/A",
            "Cloze Deletion",
            "Image/OCR",
            "English + Arabic Explanation"
        ]);
        cboMode.SelectedIndex = 0;
        cboMode.Dock = DockStyle.Fill;

        cboDifficulty.Items.AddRange(["Easy", "Medium", "Hard", "Exam Style"]);
        cboDifficulty.SelectedIndex = 3;
        cboDifficulty.Dock = DockStyle.Fill;

        cboAnswerLength.Items.AddRange(["Very Short", "Normal", "Detailed"]);
        cboAnswerLength.SelectedIndex = 0;
        cboAnswerLength.Dock = DockStyle.Fill;

        cboCount.Items.AddRange(["Auto", "5", "10", "20", "30", "40"]);
        cboCount.SelectedIndex = 0;
        cboCount.Dock = DockStyle.Fill;

        settingsGrid.Controls.Add(cboMode, 3, 1);
        settingsGrid.Controls.Add(cboDifficulty, 4, 1);
        settingsGrid.Controls.Add(cboAnswerLength, 5, 1);
        settingsGrid.Controls.Add(cboCount, 6, 1);

        var tabs = new TabControl
        {
            Dock = DockStyle.Fill
        };

        var tabGenerate = new TabPage("Generate");
        var tabPreview = new TabPage("Preview / Edit");

        tabs.TabPages.Add(tabGenerate);
        tabs.TabPages.Add(tabPreview);
        root.Controls.Add(tabs, 0, 1);

        BuildGenerateTab(tabGenerate);
        BuildPreviewTab(tabPreview);

        lblStatus.Text = "Ready.";
        lblStatus.Dock = DockStyle.Fill;
        root.Controls.Add(lblStatus, 0, 2);
    }

    private void BuildGenerateTab(TabPage tab)
    {
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterDistance = 720
        };

        tab.Controls.Add(split);

        var left = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 3,
            ColumnCount = 1,
            Padding = new Padding(10)
        };

        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 55));

        split.Panel1.Controls.Add(left);

        left.Controls.Add(new Label
        {
            Text = "Paste notes, UWorld explanation, lecture text, or textbook paragraph:",
            Dock = DockStyle.Fill
        }, 0, 0);

        txtInput.Multiline = true;
        txtInput.ScrollBars = ScrollBars.Vertical;
        txtInput.Font = new Font("Segoe UI", 11);
        txtInput.Dock = DockStyle.Fill;
        left.Controls.Add(txtInput, 0, 1);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight
        };

        var btnGenerateText = new Button
        {
            Text = "Generate From Text",
            Width = 170,
            Height = 38
        };
        btnGenerateText.Click += async (_, _) => await GenerateAsync(useImage: false);

        var btnLoadImage = new Button
        {
            Text = "Select Image",
            Width = 140,
            Height = 38
        };
        btnLoadImage.Click += (_, _) => SelectImage();

        var btnGenerateImage = new Button
        {
            Text = "Generate From Image",
            Width = 180,
            Height = 38
        };
        btnGenerateImage.Click += async (_, _) => await GenerateAsync(useImage: true);

        buttons.Controls.Add(btnGenerateText);
        buttons.Controls.Add(btnLoadImage);
        buttons.Controls.Add(btnGenerateImage);

        left.Controls.Add(buttons, 0, 2);

        var right = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 4,
            ColumnCount = 1,
            Padding = new Padding(10)
        };

        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 65));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 55));

        split.Panel2.Controls.Add(right);

        right.Controls.Add(new Label
        {
            Text = "Generated Cards",
            Font = new Font("Segoe UI", 12, FontStyle.Bold),
            Dock = DockStyle.Fill
        }, 0, 0);

        lblImage.Text = "No image selected.";
        lblImage.Dock = DockStyle.Fill;
        right.Controls.Add(lblImage, 0, 1);

        lstCards.Dock = DockStyle.Fill;
        lstCards.SelectedIndexChanged += (_, _) =>
        {
            if (lstCards.SelectedIndex >= 0)
            {
                SaveCurrentEdits();
                _currentIndex = lstCards.SelectedIndex;
                UpdatePreview();
            }
        };
        right.Controls.Add(lstCards, 0, 2);

        var exportButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill
        };

        var btnCopyAll = new Button
        {
            Text = "Copy All For Anki",
            Width = 160,
            Height = 38
        };
        btnCopyAll.Click += (_, _) => CopyAll();

        var btnExport = new Button
        {
            Text = "Export .txt",
            Width = 120,
            Height = 38
        };
        btnExport.Click += (_, _) => ExportTxt();

        exportButtons.Controls.Add(btnCopyAll);
        exportButtons.Controls.Add(btnExport);
        right.Controls.Add(exportButtons, 0, 3);
    }

    private void BuildPreviewTab(TabPage tab)
    {
        var main = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6,
            Padding = new Padding(10)
        };

        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        main.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
        main.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 55));
        main.RowStyles.Add(new RowStyle(SizeType.Percent, 20));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 55));

        tab.Controls.Add(main);

        lblCounter.TextAlign = ContentAlignment.MiddleLeft;
        lblCounter.Font = new Font("Segoe UI", 12, FontStyle.Bold);
        main.Controls.Add(lblCounter, 0, 0);

        txtFront.Multiline = true;
        txtFront.ScrollBars = ScrollBars.Vertical;
        txtFront.Font = new Font("Segoe UI", 12);
        txtFront.Dock = DockStyle.Fill;

        txtBack.Multiline = true;
        txtBack.ScrollBars = ScrollBars.Vertical;
        txtBack.Font = new Font("Segoe UI", 12);
        txtBack.Dock = DockStyle.Fill;

        txtTags.Font = new Font("Segoe UI", 11);
        txtTags.Dock = DockStyle.Fill;

        main.Controls.Add(Wrap("Front", txtFront), 0, 1);
        main.Controls.Add(Wrap("Back", txtBack), 0, 2);
        main.Controls.Add(Wrap("Tags", txtTags), 0, 4);

        var navButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill
        };

        var btnPrev = new Button { Text = "Previous", Width = 110, Height = 38 };
        btnPrev.Click += (_, _) =>
        {
            SaveCurrentEdits();
            if (_currentIndex > 0) _currentIndex--;
            SyncListSelection();
            UpdatePreview();
        };

        var btnNext = new Button { Text = "Next", Width = 110, Height = 38 };
        btnNext.Click += (_, _) =>
        {
            SaveCurrentEdits();
            if (_currentIndex < _cards.Count - 1) _currentIndex++;
            SyncListSelection();
            UpdatePreview();
        };

        var btnSave = new Button { Text = "Save Card", Width = 110, Height = 38 };
        btnSave.Click += (_, _) =>
        {
            SaveCurrentEdits();
            RefreshCardList();
            SetStatus("Card saved.");
        };

        var btnDelete = new Button { Text = "Delete Card", Width = 120, Height = 38 };
        btnDelete.Click += (_, _) => DeleteCurrentCard();

        var btnCopyCurrent = new Button { Text = "Copy Current", Width = 130, Height = 38 };
        btnCopyCurrent.Click += (_, _) => CopyCurrent();

        navButtons.Controls.Add(btnPrev);
        navButtons.Controls.Add(btnNext);
        navButtons.Controls.Add(btnSave);
        navButtons.Controls.Add(btnDelete);
        navButtons.Controls.Add(btnCopyCurrent);

        main.Controls.Add(navButtons, 0, 3);

        var bottomButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill
        };

        var btnCopyAll = new Button { Text = "Copy All For Anki", Width = 160, Height = 38 };
        btnCopyAll.Click += (_, _) => CopyAll();

        var btnExport = new Button { Text = "Export .txt", Width = 120, Height = 38 };
        btnExport.Click += (_, _) => ExportTxt();

        bottomButtons.Controls.Add(btnCopyAll);
        bottomButtons.Controls.Add(btnExport);

        main.Controls.Add(bottomButtons, 0, 5);
    }

    private static GroupBox Wrap(string title, Control control)
    {
        var box = new GroupBox
        {
            Text = title,
            Dock = DockStyle.Fill,
            Padding = new Padding(8)
        };

        box.Controls.Add(control);
        return box;
    }

    private void SelectImage()
    {
        using var ofd = new OpenFileDialog
        {
            Title = "Select image",
            Filter = "Images|*.png;*.jpg;*.jpeg;*.webp;*.bmp"
        };

        if (ofd.ShowDialog() == DialogResult.OK)
        {
            _imagePath = ofd.FileName;
            lblImage.Text = "Selected: " + Path.GetFileName(_imagePath);
            SetStatus("Image selected.");
        }
    }

    private async Task GenerateAsync(bool useImage)
    {
        SaveSettings(silent: true);

        if (string.IsNullOrWhiteSpace(txtApiKey.Text))
        {
            MessageBox.Show("Paste your OpenAI API key first.", "Missing API key");
            return;
        }

        if (!useImage && string.IsNullOrWhiteSpace(txtInput.Text))
        {
            MessageBox.Show("Paste text first.", "Missing text");
            return;
        }

        if (useImage && string.IsNullOrWhiteSpace(_imagePath))
        {
            MessageBox.Show("Select an image first.", "Missing image");
            return;
        }

        try
        {
            SetStatus("Generating flashcards...");
            UseWaitCursor = true;

            string output = await CallOpenAiAsync(useImage);
            var parsed = ParseFlashcards(output);

            if (parsed.Count == 0)
            {
                MessageBox.Show("The AI response could not be converted into flashcards. Try again with shorter input.", "No cards");
                return;
            }

            _cards.Clear();
            _cards.AddRange(parsed);
            _currentIndex = 0;

            RefreshCardList();
            SyncListSelection();
            UpdatePreview();

            SetStatus($"Generated {_cards.Count} flashcards.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Error");
            SetStatus("Error.");
        }
        finally
        {
            UseWaitCursor = false;
        }
    }

    private async Task<string> CallOpenAiAsync(bool useImage)
    {
        string systemPrompt = BuildSystemPrompt();
        string userText = txtInput.Text.Trim();

        var inputArray = new JsonArray();

        inputArray.Add(new JsonObject
        {
            ["role"] = "system",
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "input_text",
                    ["text"] = systemPrompt
                }
            }
        });

        var userContent = new JsonArray();

        if (!string.IsNullOrWhiteSpace(userText))
        {
            userContent.Add(new JsonObject
            {
                ["type"] = "input_text",
                ["text"] = userText
            });
        }

        if (useImage && !string.IsNullOrWhiteSpace(_imagePath))
        {
            string mime = GetMimeType(_imagePath);
            string base64 = Convert.ToBase64String(File.ReadAllBytes(_imagePath));

            userContent.Add(new JsonObject
            {
                ["type"] = "input_image",
                ["image_url"] = $"data:{mime};base64,{base64}"
            });
        }

        inputArray.Add(new JsonObject
        {
            ["role"] = "user",
            ["content"] = userContent
        });

        var body = new JsonObject
        {
            ["model"] = txtModel.Text.Trim(),
            ["input"] = inputArray,
            ["max_output_tokens"] = 6000
        };

        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(3)
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", txtApiKey.Text.Trim());
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request);
        string json = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new Exception("OpenAI API error:\n\n" + TrimForMessage(json));
        }

        string text = ExtractOutputText(json);

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new Exception("No text was returned by the AI.");
        }

        return text;
    }

   private string BuildSystemPrompt()
{
    string mode = cboMode.Text;
    string difficulty = cboDifficulty.Text;
    string answerLength = cboAnswerLength.Text;
    string count = cboCount.Text;

    return $$"""
You are an expert Anki flashcard creator for medical students.

Create flashcards from the user's text and/or image.

Return ONLY valid JSON.
Do not use markdown.
Do not explain outside the JSON.

JSON format:
[
  {
    "front": "question or cloze text",
    "back": "answer",
    "tags": "Step1::Topic"
  }
]

Rules:
- One concept per card.
- Make cards exam-focused and high-yield.
- Avoid long paragraphs.
- Paraphrase the source; do not copy long passages.
- If the material is medical, keep it educational.
- If using cloze mode, put the cloze deletion in the front field.
- Use clean Anki style.
- Tags should be short and useful.
- Mode: {{mode}}
- Difficulty: {{difficulty}}
- Answer length: {{answerLength}}
- Number of cards: {{count}}
""";
}

    private static string ExtractOutputText(string json)
    {
        var root = JsonNode.Parse(json);

        string? direct = root?["output_text"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(direct))
            return direct;

        var output = root?["output"]?.AsArray();
        if (output is null) return "";

        var sb = new StringBuilder();

        foreach (var item in output)
        {
            var content = item?["content"]?.AsArray();
            if (content is null) continue;

            foreach (var c in content)
            {
                string? type = c?["type"]?.GetValue<string>();
                if (type == "output_text" || type == "text")
                {
                    string? text = c?["text"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(text))
                        sb.AppendLine(text);
                }
            }
        }

        return sb.ToString().Trim();
    }

    private static List<Flashcard> ParseFlashcards(string aiText)
    {
        string cleaned = aiText.Trim();

        if (cleaned.StartsWith("```"))
        {
            cleaned = cleaned.Replace("```json", "", StringComparison.OrdinalIgnoreCase)
                             .Replace("```", "")
                             .Trim();
        }

        int start = cleaned.IndexOf('[');
        int end = cleaned.LastIndexOf(']');

        if (start >= 0 && end > start)
            cleaned = cleaned[start..(end + 1)];

        using var doc = JsonDocument.Parse(cleaned);
        var list = new List<Flashcard>();

        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var el in doc.RootElement.EnumerateArray())
        {
            string front = GetFlexibleString(el, "front");
            string back = GetFlexibleString(el, "back");
            string tags = GetFlexibleString(el, "tags");

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

    private static string GetFlexibleString(JsonElement el, string property)
    {
        if (!el.TryGetProperty(property, out var value))
            return "";

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Array => string.Join(" ", value.EnumerateArray().Select(x => x.ToString())),
            _ => value.ToString()
        };
    }

    private void RefreshCardList()
    {
        lstCards.Items.Clear();

        for (int i = 0; i < _cards.Count; i++)
        {
            string front = _cards[i].Front.Replace("\r", " ").Replace("\n", " ");
            if (front.Length > 75) front = front[..75] + "...";
            lstCards.Items.Add($"{i + 1}. {front}");
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
        lblCounter.Text = $"Card {_currentIndex + 1} / {_cards.Count}";
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
            return;

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

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;

            string json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json);

            if (settings is null) return;

            txtApiKey.Text = settings.ApiKey;
            txtModel.Text = string.IsNullOrWhiteSpace(settings.Model)
                ? "gpt-4o-mini"
                : settings.Model;
        }
        catch
        {
            // ignore broken settings file
        }
    }

    private void SaveSettings(bool silent = false)
    {
        try
        {
            Directory.CreateDirectory(settingsDir);

            var settings = new AppSettings
            {
                ApiKey = txtApiKey.Text.Trim(),
                Model = txtModel.Text.Trim()
            };

            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions
            {
                WriteIndented = true
            }));

            if (!silent)
                SetStatus("Settings saved locally.");
        }
        catch (Exception ex)
        {
            if (!silent)
                MessageBox.Show(ex.Message, "Could not save settings");
        }
    }

    private static string GetMimeType(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();

        return ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            _ => "application/octet-stream"
        };
    }

    private void SetStatus(string message)
    {
        lblStatus.Text = message;
    }

    private static string TrimForMessage(string text)
    {
        if (text.Length <= 1500) return text;
        return text[..1500] + "...";
    }
}
