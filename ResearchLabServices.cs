using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIFlashcardMaker;

// ---------------------------------------------------------------------------
// Research Lab (Phase 2) — service abstractions.
//
// IMPORTANT: nothing in this file makes a network call. There are no API keys
// anywhere. The interfaces exist so a real backend/proxy can be dropped in
// later without touching the UI. For now the app runs in two safe modes:
//
//   • Manual Claude workflow — build a high-quality prompt, the user pastes it
//     into Claude themselves, then pastes the response back for local parsing.
//   • Offline draft — a deterministic, clearly-labelled draft built ONLY from
//     the project's own fields. It never pretends to be AI and never invents
//     data, results, references, or statistics.
// ---------------------------------------------------------------------------

public interface IResearchAiService
{
    Task<ResearchRecommendations> GenerateRecommendationsAsync(ResearchProject project);
    Task<ResearchProposalDraft> GenerateProposalDraftAsync(ResearchProject project, ResearchRecommendations? recommendations);
}

public interface IResearchPromptBuilder
{
    string BuildRecommendationsPrompt(ResearchProject project);
    string BuildProposalPrompt(ResearchProject project, ResearchRecommendations? recommendations);
}

public interface IResearchRecommendationParser
{
    bool TryParseRecommendations(string pastedText, out ResearchRecommendations recommendations);
    bool TryParseProposal(string pastedText, out ResearchProposalDraft proposal);
}

// ---------------------------------------------------------------------------
// Prompt builder — turns local project fields into a structured, safety-first
// prompt the student can paste into Claude. The prompt explicitly requests a
// JSON block matching the shape ResearchRecommendationParser understands, so a
// clean paste-back can be imported field-by-field.
// ---------------------------------------------------------------------------

public sealed class ResearchPromptBuilder : IResearchPromptBuilder
{
    private static string Val(string s) => string.IsNullOrWhiteSpace(s) ? "(not provided)" : s.Trim();

    private static void AppendProjectContext(StringBuilder sb, ResearchProject p)
    {
        sb.AppendLine("PROJECT DETAILS (provided by the student):");
        sb.AppendLine($"- Working title: {Val(p.Title)}");
        sb.AppendLine($"- Specialty / field: {Val(p.Specialty)}");
        sb.AppendLine($"- Intended study type: {Val(p.StudyType)}");
        sb.AppendLine($"- Research aim: {Val(p.Aim)}");
        sb.AppendLine($"- Target population: {Val(p.Population)}");
        sb.AppendLine($"- Setting: {Val(p.Setting)}");
        sb.AppendLine($"- Time period: {Val(p.TimePeriod)}");
        sb.AppendLine($"- Data currently available: {Val(p.AvailableDataType)}");
        if (p.DesiredOutputs.Count > 0)
            sb.AppendLine($"- Desired outputs: {string.Join(", ", p.DesiredOutputs)}");
        if (!string.IsNullOrWhiteSpace(p.Notes))
            sb.AppendLine($"- Extra notes: {p.Notes.Trim()}");
        sb.AppendLine();
    }

    private static void AppendSafetyRules(StringBuilder sb)
    {
        sb.AppendLine("STRICT RULES — you MUST follow all of these:");
        sb.AppendLine("- Do NOT fabricate data, participant numbers, or measurements.");
        sb.AppendLine("- Do NOT invent results, p-values, effect sizes, or statistics.");
        sb.AppendLine("- Do NOT invent references, citations, DOIs, or author names.");
        sb.AppendLine("- Base everything only on the project details above and sound methodology.");
        sb.AppendLine("- This is for legitimate medical / university research planning.");
        sb.AppendLine("- If important information is missing, clearly state what is needed instead of guessing.");
        sb.AppendLine();
    }

    public string BuildRecommendationsPrompt(ResearchProject project)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are an experienced medical research methodologist helping a student plan a study.");
        sb.AppendLine("Give structured, practical guidance to shape their research.");
        sb.AppendLine();

        AppendProjectContext(sb, project);
        AppendSafetyRules(sb);

        sb.AppendLine("TASK:");
        sb.AppendLine("Recommend how to design and structure this study. Cover each of these:");
        sb.AppendLine("1. A refined, precise research title.");
        sb.AppendLine("2. The most appropriate study design and why it fits.");
        sb.AppendLine("3. A single focused research question.");
        sb.AppendLine("4. One primary objective.");
        sb.AppendLine("5. 2–4 secondary objectives.");
        sb.AppendLine("6. Suggested variables (name, plain-language label, type, role as Exposure/Outcome/Confounder/Demographic/Other, suggested coding).");
        sb.AppendLine("7. Suggested statistical analyses (name, when to use it, variables needed, expected output).");
        sb.AppendLine("8. Inclusion criteria.");
        sb.AppendLine("9. Exclusion criteria.");
        sb.AppendLine("10. Practical data-collection suggestions.");
        sb.AppendLine("11. Likely sources of bias and key limitations.");
        sb.AppendLine("12. Ethics considerations (consent, approvals, confidentiality).");
        sb.AppendLine("13. Concrete next steps.");
        sb.AppendLine();

        sb.AppendLine("OUTPUT FORMAT:");
        sb.AppendLine("First write a short readable summary. Then, at the very end, output ONE JSON code block");
        sb.AppendLine("in exactly this shape (use empty strings/arrays where you have nothing to add):");
        sb.AppendLine("```json");
        sb.AppendLine("{");
        sb.AppendLine("  \"refinedTitle\": \"\",");
        sb.AppendLine("  \"studyDesign\": \"\",");
        sb.AppendLine("  \"researchQuestion\": \"\",");
        sb.AppendLine("  \"primaryObjective\": \"\",");
        sb.AppendLine("  \"secondaryObjectives\": [\"\"],");
        sb.AppendLine("  \"variables\": [{\"name\":\"\",\"label\":\"\",\"type\":\"\",\"role\":\"\",\"coding\":\"\",\"notes\":\"\"}],");
        sb.AppendLine("  \"analyses\": [{\"name\":\"\",\"whenToUse\":\"\",\"variablesNeeded\":\"\",\"outputExpected\":\"\",\"notes\":\"\"}],");
        sb.AppendLine("  \"inclusionCriteria\": [\"\"],");
        sb.AppendLine("  \"exclusionCriteria\": [\"\"],");
        sb.AppendLine("  \"dataCollection\": [\"\"],");
        sb.AppendLine("  \"biasAndLimitations\": [\"\"],");
        sb.AppendLine("  \"ethicsNotes\": [\"\"],");
        sb.AppendLine("  \"nextSteps\": [\"\"]");
        sb.AppendLine("}");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("After pasting your reply back into the app, the JSON block will be imported automatically.");

        return sb.ToString();
    }

    public string BuildProposalPrompt(ResearchProject project, ResearchRecommendations? recommendations)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are helping a medical/university student draft a research PROPOSAL (protocol).");
        sb.AppendLine("Write clear, formal proposal sections suitable for supervisor review.");
        sb.AppendLine();

        AppendProjectContext(sb, project);

        if (recommendations is { HasStructuredContent: true } r)
        {
            sb.AppendLine("ACCEPTED RECOMMENDATIONS (use these as the basis):");
            if (!string.IsNullOrWhiteSpace(r.RefinedResearchTitle)) sb.AppendLine($"- Refined title: {r.RefinedResearchTitle}");
            if (!string.IsNullOrWhiteSpace(r.RecommendedStudyDesign)) sb.AppendLine($"- Study design: {r.RecommendedStudyDesign}");
            if (!string.IsNullOrWhiteSpace(r.ResearchQuestion)) sb.AppendLine($"- Research question: {r.ResearchQuestion}");
            if (!string.IsNullOrWhiteSpace(r.PrimaryObjective)) sb.AppendLine($"- Primary objective: {r.PrimaryObjective}");
            if (r.SecondaryObjectives.Count > 0) sb.AppendLine($"- Secondary objectives: {string.Join("; ", r.SecondaryObjectives)}");
            if (r.InclusionCriteria.Count > 0) sb.AppendLine($"- Inclusion: {string.Join("; ", r.InclusionCriteria)}");
            if (r.ExclusionCriteria.Count > 0) sb.AppendLine($"- Exclusion: {string.Join("; ", r.ExclusionCriteria)}");
            sb.AppendLine();
        }

        AppendSafetyRules(sb);
        sb.AppendLine("- Do NOT write a Results or Discussion section — the study has not been run.");
        sb.AppendLine("- Do NOT add a reference list; leave references for the student to supply and verify.");
        sb.AppendLine();

        sb.AppendLine("TASK:");
        sb.AppendLine("Draft these proposal sections: Title, Background, Rationale, Aim, Objectives, Methods,");
        sb.AppendLine("Study Design, Setting, Population, Inclusion Criteria, Exclusion Criteria, Variables,");
        sb.AppendLine("Data Collection, Statistical Analysis Plan, Ethics, Timeline, Limitations.");
        sb.AppendLine();

        sb.AppendLine("OUTPUT FORMAT:");
        sb.AppendLine("First write the readable proposal. Then output ONE JSON code block in exactly this shape:");
        sb.AppendLine("```json");
        sb.AppendLine("{");
        sb.AppendLine("  \"title\": \"\", \"background\": \"\", \"rationale\": \"\", \"aim\": \"\", \"objectives\": \"\",");
        sb.AppendLine("  \"methods\": \"\", \"studyDesign\": \"\", \"setting\": \"\", \"population\": \"\",");
        sb.AppendLine("  \"inclusionCriteria\": \"\", \"exclusionCriteria\": \"\", \"variables\": \"\",");
        sb.AppendLine("  \"dataCollection\": \"\", \"statisticalAnalysisPlan\": \"\", \"ethics\": \"\",");
        sb.AppendLine("  \"timeline\": \"\", \"limitations\": \"\"");
        sb.AppendLine("}");
        sb.AppendLine("```");

        return sb.ToString();
    }
}

// ---------------------------------------------------------------------------
// Parser — reads a pasted Claude response. It is deliberately forgiving: it
// extracts the first balanced JSON object (even if wrapped in ```json fences
// or surrounded by prose) and maps it into our models. If no usable JSON is
// found it returns false and the caller stores the text verbatim as RawAiText.
// It never throws on bad input.
// ---------------------------------------------------------------------------

public sealed class ResearchRecommendationParser : IResearchRecommendationParser
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public bool TryParseRecommendations(string pastedText, out ResearchRecommendations recommendations)
    {
        recommendations = new ResearchRecommendations { SourceMode = ResearchSourceMode.ManualClaude };

        if (!TryExtractJson(pastedText, out string json))
            return false;

        try
        {
            var dto = JsonSerializer.Deserialize<RecDto>(json, Opts);
            if (dto is null) return false;

            recommendations.RefinedResearchTitle = Clean(dto.RefinedTitle);
            recommendations.RecommendedStudyDesign = Clean(dto.StudyDesign);
            recommendations.ResearchQuestion = Clean(dto.ResearchQuestion);
            recommendations.PrimaryObjective = Clean(dto.PrimaryObjective);
            recommendations.SecondaryObjectives = CleanList(dto.SecondaryObjectives);
            recommendations.InclusionCriteria = CleanList(dto.InclusionCriteria);
            recommendations.ExclusionCriteria = CleanList(dto.ExclusionCriteria);
            recommendations.DataCollectionSuggestions = CleanList(dto.DataCollection);
            recommendations.BiasAndLimitations = CleanList(dto.BiasAndLimitations);
            recommendations.EthicsNotes = CleanList(dto.EthicsNotes);
            recommendations.NextSteps = CleanList(dto.NextSteps);

            if (dto.Variables is not null)
            {
                foreach (var v in dto.Variables)
                {
                    if (v is null) continue;
                    recommendations.SuggestedVariables.Add(new ResearchVariableSuggestion
                    {
                        VariableName = Clean(v.Name),
                        VariableLabel = Clean(v.Label),
                        VariableType = Clean(v.Type),
                        Role = Clean(v.Role),
                        SuggestedCoding = Clean(v.Coding),
                        Notes = Clean(v.Notes)
                    });
                }
            }

            if (dto.Analyses is not null)
            {
                foreach (var a in dto.Analyses)
                {
                    if (a is null) continue;
                    recommendations.SuggestedAnalyses.Add(new ResearchAnalysisSuggestion
                    {
                        AnalysisName = Clean(a.Name),
                        WhenToUse = Clean(a.WhenToUse),
                        VariablesNeeded = Clean(a.VariablesNeeded),
                        OutputExpected = Clean(a.OutputExpected),
                        Notes = Clean(a.Notes)
                    });
                }
            }

            // Keep the raw text too, so nothing is lost.
            recommendations.RawAiText = pastedText.Trim();
            return recommendations.HasStructuredContent;
        }
        catch
        {
            return false;
        }
    }

    public bool TryParseProposal(string pastedText, out ResearchProposalDraft proposal)
    {
        proposal = new ResearchProposalDraft
        {
            SourceMode = ResearchSourceMode.ManualClaude,
            IsTemplateGenerated = false
        };

        if (!TryExtractJson(pastedText, out string json))
            return false;

        try
        {
            var dto = JsonSerializer.Deserialize<ProposalDto>(json, Opts);
            if (dto is null) return false;

            proposal.Title = Clean(dto.Title);
            proposal.Background = Clean(dto.Background);
            proposal.Rationale = Clean(dto.Rationale);
            proposal.Aim = Clean(dto.Aim);
            proposal.Objectives = Clean(dto.Objectives);
            proposal.Methods = Clean(dto.Methods);
            proposal.StudyDesign = Clean(dto.StudyDesign);
            proposal.Setting = Clean(dto.Setting);
            proposal.Population = Clean(dto.Population);
            proposal.InclusionCriteria = Clean(dto.InclusionCriteria);
            proposal.ExclusionCriteria = Clean(dto.ExclusionCriteria);
            proposal.Variables = Clean(dto.Variables);
            proposal.DataCollection = Clean(dto.DataCollection);
            proposal.StatisticalAnalysisPlan = Clean(dto.StatisticalAnalysisPlan);
            proposal.Ethics = Clean(dto.Ethics);
            proposal.Timeline = Clean(dto.Timeline);
            proposal.Limitations = Clean(dto.Limitations);

            bool any =
                !string.IsNullOrWhiteSpace(proposal.Title) || !string.IsNullOrWhiteSpace(proposal.Background) ||
                !string.IsNullOrWhiteSpace(proposal.Aim) || !string.IsNullOrWhiteSpace(proposal.Methods) ||
                !string.IsNullOrWhiteSpace(proposal.Objectives) || !string.IsNullOrWhiteSpace(proposal.StudyDesign);
            return any;
        }
        catch
        {
            return false;
        }
    }

    // Pull the first balanced { ... } object out of arbitrary text. Handles
    // ```json fences implicitly (braces are found regardless) and ignores
    // braces inside strings.
    private static bool TryExtractJson(string text, out string json)
    {
        json = "";
        if (string.IsNullOrWhiteSpace(text)) return false;

        int start = text.IndexOf('{');
        if (start < 0) return false;

        int depth = 0;
        bool inString = false;
        bool escape = false;

        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];

            if (inString)
            {
                if (escape) escape = false;
                else if (c == '\\') escape = true;
                else if (c == '"') inString = false;
                continue;
            }

            if (c == '"') { inString = true; continue; }
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    json = text.Substring(start, i - start + 1);
                    return true;
                }
            }
        }

        return false;
    }

    private static string Clean(string? s) => (s ?? "").Trim();

    private static List<string> CleanList(List<string>? items)
    {
        var list = new List<string>();
        if (items is null) return list;
        foreach (var item in items)
        {
            string t = (item ?? "").Trim();
            if (t.Length > 0) list.Add(t);
        }
        return list;
    }

    // ---- JSON DTOs (loose shapes matching the prompt schema) --------------

    private sealed class RecDto
    {
        [JsonPropertyName("refinedTitle")] public string? RefinedTitle { get; set; }
        [JsonPropertyName("studyDesign")] public string? StudyDesign { get; set; }
        [JsonPropertyName("researchQuestion")] public string? ResearchQuestion { get; set; }
        [JsonPropertyName("primaryObjective")] public string? PrimaryObjective { get; set; }
        [JsonPropertyName("secondaryObjectives")] public List<string>? SecondaryObjectives { get; set; }
        [JsonPropertyName("variables")] public List<VarDto?>? Variables { get; set; }
        [JsonPropertyName("analyses")] public List<AnaDto?>? Analyses { get; set; }
        [JsonPropertyName("inclusionCriteria")] public List<string>? InclusionCriteria { get; set; }
        [JsonPropertyName("exclusionCriteria")] public List<string>? ExclusionCriteria { get; set; }
        [JsonPropertyName("dataCollection")] public List<string>? DataCollection { get; set; }
        [JsonPropertyName("biasAndLimitations")] public List<string>? BiasAndLimitations { get; set; }
        [JsonPropertyName("ethicsNotes")] public List<string>? EthicsNotes { get; set; }
        [JsonPropertyName("nextSteps")] public List<string>? NextSteps { get; set; }
    }

    private sealed class VarDto
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("label")] public string? Label { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("role")] public string? Role { get; set; }
        [JsonPropertyName("coding")] public string? Coding { get; set; }
        [JsonPropertyName("notes")] public string? Notes { get; set; }
    }

    private sealed class AnaDto
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("whenToUse")] public string? WhenToUse { get; set; }
        [JsonPropertyName("variablesNeeded")] public string? VariablesNeeded { get; set; }
        [JsonPropertyName("outputExpected")] public string? OutputExpected { get; set; }
        [JsonPropertyName("notes")] public string? Notes { get; set; }
    }

    private sealed class ProposalDto
    {
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("background")] public string? Background { get; set; }
        [JsonPropertyName("rationale")] public string? Rationale { get; set; }
        [JsonPropertyName("aim")] public string? Aim { get; set; }
        [JsonPropertyName("objectives")] public string? Objectives { get; set; }
        [JsonPropertyName("methods")] public string? Methods { get; set; }
        [JsonPropertyName("studyDesign")] public string? StudyDesign { get; set; }
        [JsonPropertyName("setting")] public string? Setting { get; set; }
        [JsonPropertyName("population")] public string? Population { get; set; }
        [JsonPropertyName("inclusionCriteria")] public string? InclusionCriteria { get; set; }
        [JsonPropertyName("exclusionCriteria")] public string? ExclusionCriteria { get; set; }
        [JsonPropertyName("variables")] public string? Variables { get; set; }
        [JsonPropertyName("dataCollection")] public string? DataCollection { get; set; }
        [JsonPropertyName("statisticalAnalysisPlan")] public string? StatisticalAnalysisPlan { get; set; }
        [JsonPropertyName("ethics")] public string? Ethics { get; set; }
        [JsonPropertyName("timeline")] public string? Timeline { get; set; }
        [JsonPropertyName("limitations")] public string? Limitations { get; set; }
    }
}

// ---------------------------------------------------------------------------
// Offline draft service — builds a safe, deterministic starting point ONLY
// from the project's own fields. It is clearly labelled OfflineMock so the UI
// never presents it as real AI output. It intentionally leaves clinical
// content, references, and specifics for the student to complete.
// ---------------------------------------------------------------------------

public sealed class OfflineResearchAiService : IResearchAiService
{
    private static string Or(string value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    public Task<ResearchRecommendations> GenerateRecommendationsAsync(ResearchProject project)
    {
        string population = Or(project.Population, "the target population");
        string setting = Or(project.Setting, "the study setting");
        string specialty = Or(project.Specialty, "the relevant clinical field");
        string design = SuggestDesign(project);

        var rec = new ResearchRecommendations
        {
            SourceMode = ResearchSourceMode.OfflineMock,
            RefinedResearchTitle = Or(project.Title, "Untitled research project"),
            RecommendedStudyDesign = design,
            ResearchQuestion =
                $"Among {population} in {setting}, what does this study aim to describe or compare?",
            PrimaryObjective = Or(project.Aim,
                $"To describe the primary outcome of interest among {population}."),
            SecondaryObjectives = new List<string>
            {
                "Describe participant characteristics relevant to the aim.",
                "Explore associations between key variables (to be confirmed with your supervisor).",
                "Identify limitations that may affect interpretation."
            },
            InclusionCriteria = new List<string>
            {
                $"Members of {population} meeting the study definition.",
                "Availability of the data required for the primary outcome."
            },
            ExclusionCriteria = new List<string>
            {
                "Incomplete records for the primary outcome.",
                "Cases outside the defined time period or setting."
            },
            DataCollectionSuggestions = new List<string>
            {
                "Define each variable and its source before collection begins.",
                "Pilot the data-collection sheet on a few records first.",
                "Keep an anonymised ID for each record; avoid storing direct identifiers."
            },
            BiasAndLimitations = new List<string>
            {
                "Selection bias if the sample is not representative of " + population + ".",
                "Information/measurement bias from inconsistent data recording.",
                "Confounding — list variables to adjust for with your supervisor."
            },
            EthicsNotes = new List<string>
            {
                "Confirm whether institutional ethics/IRB approval is required.",
                "Plan for informed consent or a documented waiver where applicable.",
                "Store data securely and keep it de-identified."
            },
            NextSteps = new List<string>
            {
                "Review and refine this draft with your supervisor.",
                "Confirm the study design and finalise the variable list.",
                "Prepare the data-extraction sheet in the next phase."
            }
        };

        rec.SuggestedVariables.Add(new ResearchVariableSuggestion
        {
            VariableName = "age",
            VariableLabel = "Age",
            VariableType = "Continuous (years)",
            Role = "Demographic",
            SuggestedCoding = "Whole years",
            Notes = "Common baseline characteristic."
        });
        rec.SuggestedVariables.Add(new ResearchVariableSuggestion
        {
            VariableName = "sex",
            VariableLabel = "Sex",
            VariableType = "Categorical",
            Role = "Demographic",
            SuggestedCoding = "0 = Male, 1 = Female",
            Notes = "Adjust categories to your protocol."
        });
        rec.SuggestedVariables.Add(new ResearchVariableSuggestion
        {
            VariableName = "primary_outcome",
            VariableLabel = "Primary outcome (define)",
            VariableType = "To be defined",
            Role = "Outcome",
            SuggestedCoding = "Define with your supervisor",
            Notes = "This must reflect your actual aim in " + specialty + "."
        });

        rec.SuggestedAnalyses.Add(new ResearchAnalysisSuggestion
        {
            AnalysisName = "Descriptive statistics",
            WhenToUse = "Always, to summarise the sample.",
            VariablesNeeded = "All baseline and outcome variables.",
            OutputExpected = "Means/SD, medians/IQR, counts and percentages.",
            Notes = "Run only on real collected data."
        });
        rec.SuggestedAnalyses.Add(new ResearchAnalysisSuggestion
        {
            AnalysisName = "Group comparison (e.g. chi-square / t-test)",
            WhenToUse = "When comparing an outcome across groups.",
            VariablesNeeded = "One grouping variable and one outcome variable.",
            OutputExpected = "Test statistic and p-value from real data.",
            Notes = "Confirm assumptions before choosing the exact test."
        });

        return Task.FromResult(rec);
    }

    public Task<ResearchProposalDraft> GenerateProposalDraftAsync(ResearchProject project, ResearchRecommendations? recommendations)
    {
        string population = Or(project.Population, "the target population");
        string setting = Or(project.Setting, "the study setting");
        string design = recommendations is not null && !string.IsNullOrWhiteSpace(recommendations.RecommendedStudyDesign)
            ? recommendations.RecommendedStudyDesign
            : SuggestDesign(project);
        string title = recommendations is not null && !string.IsNullOrWhiteSpace(recommendations.RefinedResearchTitle)
            ? recommendations.RefinedResearchTitle
            : Or(project.Title, "Untitled research project");

        string Lines(List<string>? items, string fallback)
            => items is { Count: > 0 } ? string.Join(Environment.NewLine, items.Select(x => "• " + x)) : fallback;

        var draft = new ResearchProposalDraft
        {
            SourceMode = ResearchSourceMode.OfflineMock,
            IsTemplateGenerated = true,
            Title = title,
            Background =
                $"[Template draft] Provide the clinical/academic background for this study in {Or(project.Specialty, "your field")}. " +
                "Summarise what is already known and cite verified references (add these yourself).",
            Rationale =
                "[Template draft] Explain the gap this study addresses and why it matters for " + population + ".",
            Aim = Or(project.Aim, "[Template draft] State the single overall aim of the study."),
            Objectives = recommendations is not null
                ? Lines(recommendations.SecondaryObjectives.Prepend(recommendations.PrimaryObjective).Where(s => !string.IsNullOrWhiteSpace(s)).ToList(),
                    "[Template draft] List the primary and secondary objectives.")
                : "[Template draft] List the primary and secondary objectives.",
            Methods =
                $"[Template draft] Describe how the study will be conducted using a {design}.",
            StudyDesign = design,
            Setting = setting,
            Population = population,
            InclusionCriteria = recommendations is not null
                ? Lines(recommendations.InclusionCriteria, "[Template draft] Define who is eligible.")
                : "[Template draft] Define who is eligible.",
            ExclusionCriteria = recommendations is not null
                ? Lines(recommendations.ExclusionCriteria, "[Template draft] Define who is excluded and why.")
                : "[Template draft] Define who is excluded and why.",
            Variables = recommendations is { SuggestedVariables.Count: > 0 }
                ? string.Join(Environment.NewLine, recommendations.SuggestedVariables.Select(v => "• " + v.HeaderDisplay + " (" + Or(v.Role, "role") + ")"))
                : "[Template draft] List each variable, its type, and its role.",
            DataCollection =
                "[Template draft] Describe the data-collection tool and process. Pilot it before full collection.",
            StatisticalAnalysisPlan =
                "[Template draft] State the planned analyses. Do not report any results here — the study has not been run.",
            Ethics =
                "[Template draft] State ethics/IRB approval status, consent process, and data protection measures.",
            Timeline =
                "[Template draft] Outline the phases and approximate duration of each.",
            Limitations =
                "[Template draft] Note anticipated limitations (design, sampling, measurement)."
        };

        return Task.FromResult(draft);
    }

    private static string SuggestDesign(ResearchProject project)
    {
        string st = (project.StudyType ?? "").Trim().ToLowerInvariant();
        if (st.Contains("cohort")) return "Cohort study";
        if (st.Contains("case")) return "Case–control study";
        if (st.Contains("cross")) return "Cross-sectional study";
        if (st.Contains("trial") || st.Contains("rct")) return "Randomised controlled trial";
        if (st.Contains("review") || st.Contains("meta")) return "Systematic review";
        if (st.Contains("qualitative")) return "Qualitative study";

        // "Not sure" or unspecified: a descriptive cross-sectional design is the
        // safest, most common starting point for a student project.
        return "Cross-sectional (descriptive) study — confirm with your supervisor";
    }
}
