namespace AIFlashcardMaker;

/// <summary>
/// Central OrbitLab display-brand constants. Working brand: "OrbitLab by StarshipAI".
///
/// These are user-visible DISPLAY strings only — NOT identifiers. The namespace
/// (<c>AIFlashcardMaker</c>), AssemblyName, exe, folders and storage paths are left
/// unchanged on purpose. Pending final legal / domain / trademark clearance before
/// public launch.
///
/// Use <see cref="ProductName"/> on normal app surfaces (window title, sidebar wordmark,
/// dashboard, nav, About heading). Use <see cref="Lockup"/> on premium / signature
/// surfaces (login/signup, Activation Welcome, About screen, generated documents/exports).
/// </summary>
public static class Branding
{
    /// <summary>Product name for normal app surfaces.</summary>
    public const string ProductName = "OrbitLab";

    /// <summary>Company name.</summary>
    public const string CompanyName = "StarshipAI";

    /// <summary>Full product/company lockup for premium / signature surfaces.</summary>
    public const string Lockup = "OrbitLab by StarshipAI";
}
