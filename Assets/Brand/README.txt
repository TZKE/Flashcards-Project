OrbitLab brand assets
=====================

Place the real PNG assets in this folder, using these EXACT filenames:

  orbitlab-logo.png        - OrbitLab logo lockup (transparent PNG-24).
  orbitlab-login-hero.png  - login-screen hero illustration (transparent PNG-24).

The auth (login/Create Account) screen loads these automatically if present;
until then it shows a neutral "coming soon" fallback. The build never breaks
whether or not these files exist.

IMPORTANT: the PNGs will NOT display until they are included as WPF Resources
in FlashcardMaker.csproj. See the one-line change the assistant provided:
    <ItemGroup>
      <Resource Include="Assets\Brand\*.png" />
    </ItemGroup>

Recommended export sizes (transparent, 3x for high-DPI sharpness):
  logo : ~600 x 600 px square lockup
  hero : ~1400 x 920 px landscape (or ~1280 x 1280 px square)

Display in-app uses Stretch="Uniform" + RenderOptions.BitmapScalingMode="HighQuality",
so aspect ratio is preserved (no distortion).
