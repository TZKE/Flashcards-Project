# AI Flashcard Maker

A Windows desktop app (WPF, .NET 8) for creating and studying flashcards, with AI-assisted card generation and spaced-repetition review.

## Features

- Organize flashcards into decks
- Generate flashcards with AI (configurable provider, model, and API key in Settings)
- Study mode with spaced repetition — rate each card **Again**, **Hard**, **Good**, or **Easy** to schedule its next review
- Track study streaks and review stats (total reviews, success rate, weak cards)
- Local accounts with activation codes/plans, stored on-device

## Getting Started

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Windows (the app targets `net8.0-windows` and uses WPF)

### Run locally

```
dotnet restore FlashcardMaker.csproj
dotnet run --project FlashcardMaker.csproj
```

### Build a standalone EXE

```
dotnet publish FlashcardMaker.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
```

The same steps run in CI via [`.github/workflows/build-windows.yml`](.github/workflows/build-windows.yml), which can be triggered manually from the Actions tab and uploads the built EXE as an artifact.

## Configuration

On first run, open **Settings** in the app to enter your AI provider's API key. Keys are stored locally and are never committed to this repository.

## Project Structure

| File | Purpose |
|---|---|
| `App.xaml` / `App.xaml.cs` | WPF application entry point |
| `MainWindow.xaml` / `MainWindow.xaml.cs` | Main UI and app logic (decks, cards, study session, stats, settings) |
| `Assets/` | Images and animations used by the UI |
| `FlashcardMaker.csproj` | Project file (.NET 8, WPF) |
