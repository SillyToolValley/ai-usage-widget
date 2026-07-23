using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QuotaDeck;

// Character overlay themes. Each pack ships a static panel image (skinned via
// 9-slice so it fits any widget size) plus a sprite sheet of animation frames
// drawn above the panel, left-aligned and matching the panel width.
public sealed class OverlayTheme
{
    public const string GeneralCategory = "General";
    public const string UncategorizedCategory = "Uncategorized";
    public const string VocaloidCategory = "VOCALOID";
    public const string VioletEvergardenCategory = "Violet Evergarden";
    public const string SpyFamilyCategory = "SPY×FAMILY";
    public const string GintamaCategory = "Gintama";
    public const string UmamusumeCategory = "Umamusume: Pretty Derby";

    public string Id { get; }
    public string DisplayName { get; }
    public string Category { get; }
    public int FrameDurationMs { get; }
    // Source-pixel size of the panel's fixed 9-slice corners (large enough to
    // cover the frame's corner ornament so it never gets stretched).
    public int PanelCornerPx { get; }
    // Source-pixel horizontal inset that keeps header controls clear of the
    // artwork inside the fixed top corners. It is scaled with the displayed
    // 9-slice corner rather than with the whole window.
    public int HeaderSafeSideInsetPx { get; }
    // Source-pixel vertical clearance between the panel crown and its header.
    // Like the side inset, it scales with the displayed fixed corner.
    public int HeaderSafeTopInsetPx { get; }
    // How many source pixels of the overlay's bottom edge sit ON TOP of the
    // panel (frameHeight + overlayOffsetFromBaseTop.y in the pack's json).
    public int OverlayOverlapPx { get; }
    public int PanelPixelWidth => _frameWidth;

    readonly string _sheetFile;
    readonly string _panelFile;
    readonly int _columns;
    readonly int _frameCount;
    readonly int _frameWidth;
    readonly int _frameHeight;

    IReadOnlyList<BitmapSource>? _frames;
    BitmapSource? _panel;
    BitmapSource[,]? _slices;

    OverlayTheme(string id, string displayName, string sheetFile, string panelFile,
        int columns, int frameCount, int frameWidth, int frameHeight, int frameDurationMs,
        int panelCornerPx = 44, int overlayOverlapPx = 1,
        int headerSafeSideInsetPx = 0, int headerSafeTopInsetPx = 0,
        string category = UncategorizedCategory)
    {
        Id = id;
        DisplayName = displayName;
        Category = category;
        _sheetFile = sheetFile;
        _panelFile = panelFile;
        _columns = columns;
        _frameCount = frameCount;
        _frameWidth = frameWidth;
        _frameHeight = frameHeight;
        FrameDurationMs = frameDurationMs;
        PanelCornerPx = panelCornerPx;
        OverlayOverlapPx = overlayOverlapPx;
        HeaderSafeSideInsetPx = headerSafeSideInsetPx;
        HeaderSafeTopInsetPx = headerSafeTopInsetPx;
    }

    // Fallbacks keep existing installations usable if the generated registry
    // is absent or malformed. Audited exports replace matching ids at build
    // time through Assets/overlay_themes.json and append new themes without a
    // hand edit to this source file.
    // Hatsune Miku © Crypton Future Media, INC. www.piapro.net (PCL).
    static readonly OverlayTheme[] BuiltInFallbacks =
    {
        new("miku", "Hatsune Miku",
            "miku_digital_concert_top_overlay_32f_v2.png", "miku_digital_concert_panel_base_v2_9slice.png",
            8, 32, 1536, 538, 125, panelCornerPx: 150, overlayOverlapPx: 1,
            headerSafeSideInsetPx: 112, category: VocaloidCategory),
        new("bourbon", "Bourbon (Valentine)",
            "mihono_bourbon_chocolate_patisserie_top_overlay_32f_v13.png", "mihono_bourbon_chocolate_patisserie_panel_base_v13_9slice.png",
            8, 32, 1536, 564, 125, panelCornerPx: 150, overlayOverlapPx: 1,
            headerSafeSideInsetPx: 112, headerSafeTopInsetPx: 20,
            category: UmamusumeCategory),
        new("violet", "Violet",
            "violet_evergarden_top_overlay_24f_v5.png", "violet_evergarden_panel_base_v5.png",
            8, 24, 1286, 494, 125, category: VioletEvergardenCategory),
        new("anya", "Anya & Bond",
            "anya_bond_top_overlay_24f.png", "anya_bond_panel_base.png",
            8, 24, 1498, 405, 125, panelCornerPx: 140, overlayOverlapPx: 8,
            category: SpyFamilyCategory),
        new("kagura", "Kagura",
            "kagura_top_overlay_24f.png", "kagura_panel_base_9slice.png",
            8, 24, 1498, 500, 125, panelCornerPx: 150, overlayOverlapPx: 43,
            category: GintamaCategory),
        new("elizabeth", "Elizabeth",
            "elizabeth_top_overlay_24f.png", "elizabeth_panel_base_9slice.png",
            8, 24, 1536, 500, 125, panelCornerPx: 115, overlayOverlapPx: 5,
            category: GintamaCategory),
        new("rice_shower", "Rice Shower (Halloween)",
            "rice_shower_halloween_top_overlay_32f.png", "rice_shower_halloween_panel_base_9slice.png",
            8, 32, 1536, 542, 125, panelCornerPx: 150, overlayOverlapPx: 1,
            headerSafeSideInsetPx: 120, category: UmamusumeCategory),
    };

    public static readonly OverlayTheme[] All = LoadRegisteredThemes();

    static OverlayTheme[] LoadRegisteredThemes()
    {
        var themes = new List<OverlayTheme>(BuiltInFallbacks);
        try
        {
            using var stream = typeof(OverlayTheme).Assembly.GetManifestResourceStream(
                "QuotaDeck.Assets.overlay_themes.json");
            if (stream is null) return themes.ToArray();
            var registry = JsonSerializer.Deserialize<ThemeRegistry>(stream);
            if (registry?.SchemaVersion != 1) return themes.ToArray();
            foreach (var entry in registry.Themes)
            {
                if (!entry.IsValid) continue;
                var theme = new OverlayTheme(
                    entry.Id, entry.DisplayName, entry.SheetFile, entry.PanelFile,
                    entry.Columns, entry.FrameCount, entry.FrameWidth, entry.FrameHeight,
                    entry.FrameDurationMs, entry.PanelCornerPx, entry.OverlayOverlapPx,
                    entry.HeaderSafeSideInsetPx, entry.HeaderSafeTopInsetPx,
                    NormalizeCategory(entry.Category, entry.Id));
                int index = themes.FindIndex(value => value.Id == theme.Id);
                if (index >= 0) themes[index] = theme;
                else themes.Add(theme);
            }
        }
        catch (JsonException)
        {
            // A malformed generated registry must not prevent the tray app
            // from starting; the immutable built-in fallbacks remain usable.
        }
        return themes.ToArray();
    }

    static string NormalizeCategory(string? category, string id)
    {
        if (!string.IsNullOrWhiteSpace(category))
            return category.Trim();
        return id switch
        {
            "miku" => VocaloidCategory,
            "violet" => VioletEvergardenCategory,
            "anya" => SpyFamilyCategory,
            "kagura" or "elizabeth" => GintamaCategory,
            "bourbon" or "rice_shower" or "oguri_cap_xmas"
                or "nice_nature_cheer" or "maruzensky_summer_night"
                or "curren_chan_wedding" => UmamusumeCategory,
            _ => UncategorizedCategory,
        };
    }

    sealed class ThemeRegistry
    {
        [JsonPropertyName("schema_version")]
        public int SchemaVersion { get; set; }

        [JsonPropertyName("themes")]
        public List<ThemeRegistryEntry> Themes { get; set; } = [];
    }

    sealed class ThemeRegistryEntry
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("display_name")]
        public string DisplayName { get; set; } = "";

        [JsonPropertyName("category")]
        public string Category { get; set; } = "";

        [JsonPropertyName("sheet_file")]
        public string SheetFile { get; set; } = "";

        [JsonPropertyName("panel_file")]
        public string PanelFile { get; set; } = "";

        [JsonPropertyName("columns")]
        public int Columns { get; set; }

        [JsonPropertyName("frame_count")]
        public int FrameCount { get; set; }

        [JsonPropertyName("frame_width")]
        public int FrameWidth { get; set; }

        [JsonPropertyName("frame_height")]
        public int FrameHeight { get; set; }

        [JsonPropertyName("frame_duration_ms")]
        public int FrameDurationMs { get; set; }

        [JsonPropertyName("panel_corner_px")]
        public int PanelCornerPx { get; set; }

        [JsonPropertyName("overlay_overlap_px")]
        public int OverlayOverlapPx { get; set; }

        [JsonPropertyName("header_safe_side_inset_px")]
        public int HeaderSafeSideInsetPx { get; set; }

        [JsonPropertyName("header_safe_top_inset_px")]
        public int HeaderSafeTopInsetPx { get; set; }

        [JsonIgnore]
        public bool IsValid =>
            !string.IsNullOrWhiteSpace(Id) &&
            !string.IsNullOrWhiteSpace(DisplayName) &&
            SheetFile.EndsWith(".png", StringComparison.OrdinalIgnoreCase) &&
            PanelFile.EndsWith(".png", StringComparison.OrdinalIgnoreCase) &&
            Columns > 0 && FrameCount > 0 && FrameWidth > 0 && FrameHeight > 0 &&
            FrameDurationMs > 0 && PanelCornerPx > 0 && OverlayOverlapPx > 0 &&
            HeaderSafeSideInsetPx >= 0 && HeaderSafeTopInsetPx >= 0;
    }

    public static OverlayTheme? Find(string? id)
    {
        foreach (var theme in All)
            if (theme.Id == id) return theme;
        return null;
    }

    public IReadOnlyList<BitmapSource> Frames => _frames ??= LoadFrames();
    public BitmapSource[,] PanelSlices => _slices ??= BuildSlices();

    BitmapSource PanelBase => _panel ??= LoadImage(_panelFile);

    static BitmapImage LoadImage(string name)
    {
        var img = new BitmapImage(new Uri($"pack://application:,,,/Assets/{name}"));
        img.Freeze();
        return img;
    }

    IReadOnlyList<BitmapSource> LoadFrames()
    {
        var sheet = LoadImage(_sheetFile);
        var frames = new List<BitmapSource>(_frameCount);
        for (int i = 0; i < _frameCount; i++)
        {
            int col = i % _columns;
            int row = i / _columns;
            var crop = new CroppedBitmap(sheet,
                new Int32Rect(col * _frameWidth, row * _frameHeight, _frameWidth, _frameHeight));
            // Materialize each frame at half size (still 2x+ the on-screen size)
            // so the ~50 MB decoded sheet can be garbage-collected.
            var scaled = new WriteableBitmap(
                new TransformedBitmap(crop, new ScaleTransform(0.5, 0.5)));
            scaled.Freeze();
            frames.Add(scaled);
        }
        return frames;
    }

    // 3x3 slices of the panel frame: fixed corners, edges stretched along one
    // axis, center stretched freely.
    BitmapSource[,] BuildSlices()
    {
        int corner = PanelCornerPx;
        var src = PanelBase;
        int w = src.PixelWidth, h = src.PixelHeight;
        corner = Math.Min(corner, Math.Min(w, h) / 3);
        int[] xs = { 0, corner, w - corner };
        int[] ws = { corner, w - 2 * corner, corner };
        int[] ys = { 0, corner, h - corner };
        int[] hs = { corner, h - 2 * corner, corner };
        var slices = new BitmapSource[3, 3];
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++)
            {
                var piece = new CroppedBitmap(src, new Int32Rect(xs[c], ys[r], ws[c], hs[r]));
                piece.Freeze();
                slices[r, c] = piece;
            }
        return slices;
    }
}
