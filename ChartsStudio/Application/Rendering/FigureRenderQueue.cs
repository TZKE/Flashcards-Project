using System.Collections.Concurrent;

namespace AIFlashcardMaker.ChartsStudio.Application.Rendering;

/// <summary>
/// Charts Studio Phase 2 — schedules figure renders off the UI thread, with caching and
/// cancellation.
///
/// WHY THIS IS ITS OWN TYPE, separate from the renderer: scheduling is an application concern
/// (how many at once, in what order, cancel what) while drawing is infrastructure. Splitting
/// them means the renderer stays trivially testable and replaceable, and the queue can be
/// reasoned about without a charting library in scope.
///
/// WHY IT MATTERS HERE PARTICULARLY: this codebase has already had to fix scrolling that felt
/// "laggy and not easy to handle" (commit b31415c), and a contact sheet is a scrollable grid of
/// rendered cards — exactly the structure that regressed. Three rules protect it:
///
///   1. NOTHING RENDERS ON THE UI THREAD. Every render runs on the thread pool.
///   2. BOUNDED CONCURRENCY. A burst of cards cannot saturate the pool and starve the app.
///   3. SUPERSEDING CANCELS. Reopening a project or changing data abandons in-flight renders
///      instead of letting stale figures arrive after the ones that replaced them.
///
/// Cached by the render key, which is the spec's visual identity plus output size — so two
/// figures that are the same picture share one render rather than each paying to draw it.
/// </summary>
public sealed class FigureRenderQueue : IDisposable
{
    private readonly IFigureRenderer _renderer;
    private readonly SemaphoreSlim _concurrency;
    private readonly ConcurrentDictionary<string, byte[]> _cache = new(StringComparer.Ordinal);

    /// <summary>
    /// Cancelled and replaced whenever the sheet is superseded. Held here rather than by the
    /// view model so a single call abandons every in-flight render at once.
    /// </summary>
    private CancellationTokenSource _generation = new();

    private bool _disposed;

    /// <summary>
    /// Phase 5 — the renderer's version participates in every cache key. An engine upgrade may
    /// draw the same spec differently, and a cache that survived it would show stale pictures
    /// with no way to tell.
    /// </summary>
    private readonly string _versionPrefix;

    /// <param name="renderer">The drawing engine; its version becomes part of every cache key.</param>
    /// <param name="maxConcurrency">
    /// Default 2. Deliberately low: a contact sheet renders a handful of small figures, and
    /// leaving cores free keeps the UI responsive while they draw. Raising this trades scroll
    /// smoothness for throughput.
    /// </param>
    public FigureRenderQueue(IFigureRenderer renderer, int maxConcurrency = 2)
    {
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _concurrency = new SemaphoreSlim(Math.Max(1, maxConcurrency));
        _versionPrefix = renderer.RendererVersion + "|";
    }

    /// <summary>Renders cached so far. Diagnostics only.</summary>
    public int CachedCount => _cache.Count;

    /// <summary>
    /// Abandons every in-flight render and starts a new generation. Called when the project
    /// changes or the context is rebuilt — a figure from the previous project must never land
    /// on the new sheet.
    /// </summary>
    public void CancelAll()
    {
        var previous = Interlocked.Exchange(ref _generation, new CancellationTokenSource());
        try { previous.Cancel(); } catch (ObjectDisposedException) { /* already gone */ }
        previous.Dispose();
    }

    /// <summary>Empties the render cache. Called when the underlying data changed.</summary>
    public void ClearCache() => _cache.Clear();

    /// <summary>
    /// Renders one figure, returning a cached image immediately when there is one.
    ///
    /// Never throws for an ordinary failure: a bad spec or a renderer fault comes back as a
    /// RenderResult carrying a reason, so one broken figure degrades to one explaining card
    /// rather than taking down the sheet.
    /// </summary>
    public async Task<RenderResult> RenderAsync(RenderRequest request, CancellationToken caller = default)
    {
        if (_disposed) return RenderResult.Failure("Charts Studio is closing.");

        string key = _versionPrefix + request.CacheKey;

        if (_cache.TryGetValue(key, out byte[]? cached))
            return RenderResult.Success(cached);

        var generationToken = _generation.Token;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(generationToken, caller);
        var token = linked.Token;

        try
        {
            await _concurrency.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return RenderResult.Failure("");   // superseded: silent, not an error the user sees
        }

        try
        {
            // Re-check: the wait may have been long enough for another card to render the same
            // picture, and drawing it twice would be pure waste.
            if (_cache.TryGetValue(key, out cached))
                return RenderResult.Success(cached);

            var result = await Task.Run(() => _renderer.Render(request, token), token)
                                   .ConfigureAwait(false);

            if (result.Succeeded && result.PngBytes is not null)
                _cache[key] = result.PngBytes;

            return result;
        }
        catch (OperationCanceledException)
        {
            return RenderResult.Failure("");
        }
        catch (Exception ex)
        {
            return RenderResult.Failure($"This figure could not be drawn ({ex.GetType().Name}).");
        }
        finally
        {
            _concurrency.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _generation.Cancel(); } catch (ObjectDisposedException) { }
        _generation.Dispose();
        _concurrency.Dispose();
        _cache.Clear();
    }
}
