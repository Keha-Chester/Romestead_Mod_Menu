using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Candide;
using Candide.Database;
using Candide.Database.Doodad;
using CandideCreator.Shared.Graphics;
using CandideCreator.Shared.Models.SpritesAnimationCollections;
using Microsoft.Xna.Framework.Graphics;
using Shared;
using Shared.Data;
using Shared.Data.Furniture;
using Shared.Entity;
using Shared.Entity.Components;
using Shared.Models.Construction;
using Shared.Models.Items;
using Shared.Text;
using Vec2 = System.Numerics.Vector2;
using XnaColor = Microsoft.Xna.Framework.Color;
using XnaRectangle = Microsoft.Xna.Framework.Rectangle;

namespace RomesteadCheatMenu;

internal enum EntryKind
{
    Item,
    Resource,
    Furniture,
    Creature,
    Citizen
}

internal struct IconRef
{
    public IntPtr Texture;
    public Vec2 Uv0;
    public Vec2 Uv1;
    // Source size in pixels, used to keep the aspect ratio of creature sprites.
    public Vec2 Size;
}

internal sealed class SpawnEntry
{
    public string Key;
    public string Id;
    public EntryKind Kind;
    public string NameEn;
    public string NameLocal;
    // Read from the Russian locale whatever language the game runs in, so Russian search always works.
    public string NameRu;
    public string Description;
    public string SearchText;
    public string IconId;
    public string CategoryLabel;
    public int MaxStack = 1;
    public bool Unique;
    public bool IconResolved;
    public bool HasIcon;
    public IconRef Icon;

    // Creatures and citizens.
    public Guid EntityId;
    public EntityType EntityType;
    public string Aliases;
    public string SpacHint;
    public string CitizenTier;
    public int MaxHealth;
    public string NoteEn;
    public string NoteRu;

    // Furniture: EntityId is the doodad it is drawn with, Frame the piece of its sprite sheet.
    public int Frame = -1;
    public string RecipeBuilding;
    public int RecipeLevel;

    public bool IsEntity => Kind == EntryKind.Creature || Kind == EntryKind.Citizen;
}

internal sealed class Category
{
    public string Name;
    public string Label;
    public int Depth;
    public readonly List<SpawnEntry> Entries = new List<SpawnEntry>();
    public readonly List<Category> Children = new List<Category>();
    private List<SpawnEntry> _allEntries;

    // Own entries followed by all sub-category entries, without duplicates.
    public List<SpawnEntry> AllEntries
    {
        get
        {
            if (_allEntries == null)
            {
                _allEntries = new List<SpawnEntry>();
                Collect(this, new HashSet<string>(), _allEntries);
            }
            return _allEntries;
        }
    }

    private static void Collect(Category category, HashSet<string> seen, List<SpawnEntry> output)
    {
        foreach (SpawnEntry entry in category.Entries)
        {
            if (seen.Add(entry.Key))
            {
                output.Add(entry);
            }
        }
        foreach (Category child in category.Children)
        {
            Collect(child, seen, output);
        }
    }
}

internal sealed class CatalogTab
{
    public string Name;
    public readonly List<Category> Categories = new List<Category>();
    // Every entry of the tab once, sorted by name (search runs over this).
    public List<SpawnEntry> Entries = new List<SpawnEntry>();
    public string LastQuery;
    public List<SpawnEntry> LastResults = new List<SpawnEntry>();
}

internal static class Catalog
{
    private static readonly Dictionary<string, SpawnEntry> EntriesByKey = new Dictionary<string, SpawnEntry>();
    private static readonly Dictionary<Texture2D, IntPtr> BoundTextures = new Dictionary<Texture2D, IntPtr>();
    private static bool _built;

    public static CatalogTab ItemsTab { get; private set; }

    public static CatalogTab CreaturesTab { get; private set; }

    public static CatalogTab FurnitureTab { get; private set; }

    private static string _itemsFailure;
    private static string _creaturesFailure;
    private static string _furnitureFailure;

    public static string FurnitureError => _furnitureFailure == null
        ? null
        : Loc.T("Could not build the furniture list: ", "Не удалось построить список мебели: ") + _furnitureFailure;

    public static string BuildError => _itemsFailure == null
        ? null
        : Loc.T("Could not build the item list: ", "Не удалось построить список предметов: ") + _itemsFailure;

    public static string CreaturesError => _creaturesFailure == null
        ? null
        : Loc.T("Could not build the creature list: ", "Не удалось построить список существ: ") + _creaturesFailure;

    public static void EnsureBuilt()
    {
        if (_built)
        {
            return;
        }
        _built = true;
        try
        {
            BuildItems();
        }
        catch (Exception e)
        {
            _itemsFailure = e.Message;
            Log.Error("Item catalog build failed", e);
        }
        try
        {
            BuildCreatures();
        }
        catch (Exception e)
        {
            _creaturesFailure = e.Message;
            Log.Error("Creature catalog build failed", e);
        }
        try
        {
            BuildFurniture();
        }
        catch (Exception e)
        {
            _furnitureFailure = e.Message;
            Log.Error("Furniture catalog build failed", e);
        }
    }

    private static JsonDocument LoadResource(string name)
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(name + " resource is missing");
        return JsonDocument.Parse(stream);
    }

    // ------------------------------------------------------------------ items

    private static void BuildItems()
    {
        using JsonDocument document = LoadResource("categories.json");
        var tab = new CatalogTab { Name = "Items" };
        var used = new HashSet<string>();
        int missing = 0;
        foreach (JsonElement tabJson in document.RootElement.GetProperty("tabs").EnumerateArray())
        {
            string tabName = tabJson.GetProperty("name").GetString();
            foreach (JsonElement categoryJson in tabJson.GetProperty("categories").EnumerateArray())
            {
                tab.Categories.Add(ParseItemCategory(categoryJson, tabName, 0, used, ref missing));
            }
        }

        // Anything the wiki layout does not know about (new game versions) still shows up.
        var other = new Category { Name = "Other", Label = "Items / Other" };
        foreach (ItemData data in ItemDataBase.DataMap.Values)
        {
            if (!used.Contains(data.Id))
            {
                SpawnEntry entry = GetItem(data.Id, other.Label);
                if (entry != null)
                {
                    other.Entries.Add(entry);
                }
            }
        }
        other.Entries.Sort((a, b) => string.Compare(a.NameEn, b.NameEn, StringComparison.OrdinalIgnoreCase));
        if (other.Entries.Count > 0)
        {
            tab.Categories.Add(other);
        }

        FillEntries(tab);
        ItemsTab = tab;
        Log.Info($"Item catalog built: {tab.Entries.Count} entries ({ItemDataBase.DataMap.Count} game items, {other.Entries.Count} under Other, {missing} layout ids not in game data)");
    }

    private static Category ParseItemCategory(JsonElement json, string parentLabel, int depth, HashSet<string> used, ref int missing)
    {
        string name = json.GetProperty("name").GetString();
        var category = new Category { Name = name, Label = parentLabel + " / " + name, Depth = depth };
        if (json.TryGetProperty("resources", out JsonElement resources))
        {
            foreach (JsonElement id in resources.EnumerateArray())
            {
                SpawnEntry entry = GetResource(id.GetString(), category.Label);
                if (entry == null)
                {
                    missing++;
                    continue;
                }
                category.Entries.Add(entry);
            }
        }
        if (json.TryGetProperty("items", out JsonElement items))
        {
            foreach (JsonElement id in items.EnumerateArray())
            {
                SpawnEntry entry = GetItem(id.GetString(), category.Label);
                if (entry == null)
                {
                    missing++;
                    continue;
                }
                used.Add(entry.Id);
                category.Entries.Add(entry);
            }
        }
        if (json.TryGetProperty("children", out JsonElement children))
        {
            foreach (JsonElement child in children.EnumerateArray())
            {
                category.Children.Add(ParseItemCategory(child, category.Label, depth + 1, used, ref missing));
            }
        }
        return category;
    }

    private static SpawnEntry GetItem(string id, string categoryLabel)
    {
        string key = "item|" + id;
        if (EntriesByKey.TryGetValue(key, out SpawnEntry existing))
        {
            return existing;
        }
        ItemData data = ItemDataBase.GetItemDataOrNull(id);
        if (data == null)
        {
            return null;
        }
        string local = Translate(data.Name);
        var entry = new SpawnEntry
        {
            Key = key,
            Id = id,
            Kind = EntryKind.Item,
            NameEn = LocaleFile.English.Get(id + ItemDataBase.SuffixName) ?? local ?? id,
            NameLocal = local,
            NameRu = LocaleFile.Russian.Get(id + ItemDataBase.SuffixName),
            Description = data.Description.HasValue ? Translate(data.Description.Value) : null,
            IconId = data.Icon,
            MaxStack = Math.Max(1, data.MaxStackSize),
            Unique = data.Unique,
            CategoryLabel = categoryLabel
        };
        entry.SearchText = BuildSearchText(entry.NameEn, entry.NameLocal, entry.NameRu, id);
        EntriesByKey[key] = entry;
        return entry;
    }

    private static SpawnEntry GetResource(string id, string categoryLabel)
    {
        string key = "resource|" + id;
        if (EntriesByKey.TryGetValue(key, out SpawnEntry existing))
        {
            return existing;
        }
        ConstructionResourceDataModel? data = ConstructionResourcesDataBase.GetConstructionResourceOrNull(id);
        if (!data.HasValue)
        {
            return null;
        }
        string local = Translate(data.Value.Name);
        var entry = new SpawnEntry
        {
            Key = key,
            Id = id,
            Kind = EntryKind.Resource,
            NameEn = LocaleFile.English.Get(id + ConstructionResourcesDataBase.SuffixName) ?? local ?? id,
            NameLocal = local,
            NameRu = LocaleFile.Russian.Get(id + ConstructionResourcesDataBase.SuffixName),
            Description = Translate(data.Value.Description),
            IconId = data.Value.Icon,
            CategoryLabel = categoryLabel
        };
        entry.SearchText = BuildSearchText(entry.NameEn, entry.NameLocal, entry.NameRu, id);
        EntriesByKey[key] = entry;
        return entry;
    }

    private static string Translate(StringId id)
    {
        try
        {
            string text = id.GetTranslation();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ creatures and citizens

    private static void BuildCreatures()
    {
        using JsonDocument document = LoadResource("creatures.json");
        var tab = new CatalogTab { Name = "Creatures" };
        int missing = 0;
        foreach (JsonElement categoryJson in document.RootElement.GetProperty("categories").EnumerateArray())
        {
            tab.Categories.Add(ParseCreatureCategory(categoryJson, "Creatures", 0, EntityType.Hostile, ref missing));
        }
        FillEntries(tab);
        CreaturesTab = tab;
        Log.Info($"Creature catalog built: {tab.Entries.Count} entries ({missing} ids not in game data)");
    }

    private static Category ParseCreatureCategory(JsonElement json, string parentLabel, int depth, EntityType type, ref int missing)
    {
        string name = json.GetProperty("name").GetString();
        var category = new Category { Name = name, Label = parentLabel + " / " + name, Depth = depth };
        string typeText = GetString(json, "type");
        if (typeText != null)
        {
            type = ParseEntityType(typeText, type);
        }
        if (json.TryGetProperty("entities", out JsonElement entities))
        {
            foreach (JsonElement entityJson in entities.EnumerateArray())
            {
                SpawnEntry entry = GetCreature(entityJson, category.Label, type);
                if (entry == null)
                {
                    missing++;
                    continue;
                }
                category.Entries.Add(entry);
            }
        }
        if (json.TryGetProperty("citizens", out JsonElement citizens))
        {
            foreach (JsonElement citizenJson in citizens.EnumerateArray())
            {
                category.Entries.Add(GetCitizen(citizenJson, category.Label));
            }
        }
        if (json.TryGetProperty("children", out JsonElement children))
        {
            foreach (JsonElement child in children.EnumerateArray())
            {
                category.Children.Add(ParseCreatureCategory(child, category.Label, depth + 1, type, ref missing));
            }
        }
        return category;
    }

    private static SpawnEntry GetCreature(JsonElement json, string categoryLabel, EntityType type)
    {
        string name = json.GetProperty("name").GetString();
        if (!Guid.TryParse(GetString(json, "id"), out Guid baseId))
        {
            Log.Warn("Bad creature id for " + name);
            return null;
        }
        string key = "creature|" + baseId;
        if (EntriesByKey.TryGetValue(key, out SpawnEntry existing))
        {
            return existing;
        }
        if (!DoodadDatabaseManager.TryGetEntityBaseData(baseId, out EntityWrapper data))
        {
            Log.Warn($"Creature is not in the game data: {name} ({baseId})");
            return null;
        }
        string typeText = GetString(json, "type");
        if (typeText != null)
        {
            type = ParseEntityType(typeText, type);
        }
        string internalName = GetString(json, "key") ?? name;
        var entry = new SpawnEntry
        {
            Key = key,
            Id = internalName,
            Kind = EntryKind.Creature,
            NameEn = name,
            // Creature names that the game shows (bosses, bestiary) use the English name itself as the key.
            NameRu = LocaleFile.Russian.Get(name) ?? LocaleFile.Russian.Get(data.Name),
            NoteEn = GetString(json, "note"),
            NoteRu = GetString(json, "note_ru"),
            Aliases = GetString(json, "aliases"),
            SpacHint = GetString(json, "spac"),
            EntityId = baseId,
            EntityType = type,
            CategoryLabel = categoryLabel
        };
        try
        {
            if (data.Mask.HasFlags(Component.Health))
            {
                entry.MaxHealth = Convert.ToInt32(data.MaxHealth);
            }
        }
        catch
        {
            // Health is only shown in the tooltip.
        }
        entry.SearchText = BuildSearchText(name, entry.NameRu, entry.Aliases, internalName, type == EntityType.Hostile ? "enemy враг" : "creature animal животное");
        EntriesByKey[key] = entry;
        return entry;
    }

    private static SpawnEntry GetCitizen(JsonElement json, string categoryLabel)
    {
        string name = json.GetProperty("name").GetString();
        string tier = GetString(json, "tier") ?? string.Empty;
        string key = "citizen|" + tier;
        if (EntriesByKey.TryGetValue(key, out SpawnEntry existing))
        {
            return existing;
        }
        var entry = new SpawnEntry
        {
            Key = key,
            Id = tier.Length == 0 ? "wild citizen, tier by area difficulty" : "wild citizen, tier " + tier,
            Kind = EntryKind.Citizen,
            NameEn = name,
            NoteEn = GetString(json, "note"),
            NoteRu = GetString(json, "note_ru"),
            Aliases = GetString(json, "aliases"),
            IconId = "ui:citizen",
            EntityId = EntityIds.WildCitizen,
            EntityType = EntityType.Friendly,
            CitizenTier = tier,
            CategoryLabel = categoryLabel
        };
        entry.SearchText = string.Join("\n", name, entry.Aliases, entry.Id, "citizen житель").ToLowerInvariant();
        EntriesByKey[key] = entry;
        return entry;
    }

    private static string GetString(JsonElement json, string property)
    {
        return json.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static EntityType ParseEntityType(string text, EntityType fallback)
    {
        return Enum.TryParse(text, true, out EntityType parsed) ? parsed : fallback;
    }

    private static void FillEntries(CatalogTab tab)
    {
        var seen = new HashSet<string>();
        var entries = new List<SpawnEntry>();
        foreach (Category category in tab.Categories)
        {
            foreach (SpawnEntry entry in category.AllEntries)
            {
                if (seen.Add(entry.Key))
                {
                    entries.Add(entry);
                }
            }
        }
        tab.Entries = entries.OrderBy(e => e.NameEn, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ------------------------------------------------------------------ furniture

    // Tags that only say which biome style a piece has; the first other tag is its kind.
    private static readonly HashSet<string> FurnitureStyleTags = new HashSet<string> { "plains", "forest", "desert", "volcano", "misc" };

    private static readonly (string Tag, string Name)[] FurnitureKinds =
    {
        ("table", "Tables"), ("chair", "Chairs"), ("sofa", "Sofas"), ("bed", "Beds"), ("cupboard", "Cupboards"),
        ("shelf", "Shelves"), ("box", "Boxes"), ("stove", "Stoves"), ("lamp", "Lamps"), ("pot", "Pots"),
        ("plant", "Plants"), ("fence", "Fences")
    };

    private static void BuildFurniture()
    {
        var tab = new CatalogTab { Name = "Furniture" };
        var byTag = new Dictionary<string, Category>(StringComparer.Ordinal);
        foreach ((string tag, string name) in FurnitureKinds)
        {
            var category = new Category { Name = name, Label = "Furniture / " + name };
            byTag[tag] = category;
            tab.Categories.Add(category);
        }
        var other = new Category { Name = "Other", Label = "Furniture / Other" };
        foreach (FurnitureData data in FurnitureDataBase.DataMap.Values)
        {
            string tag = data.Tags?.FirstOrDefault(t => !string.IsNullOrEmpty(t) && !FurnitureStyleTags.Contains(t));
            Category category = other;
            if (tag != null && !byTag.TryGetValue(tag, out category))
            {
                // A kind added in a newer game version.
                string name = char.ToUpperInvariant(tag[0]) + tag.Substring(1);
                category = new Category { Name = name, Label = "Furniture / " + name };
                byTag[tag] = category;
                tab.Categories.Add(category);
            }
            SpawnEntry entry = GetFurniture(data, category.Label);
            if (entry != null)
            {
                category.Entries.Add(entry);
            }
        }
        tab.Categories.Add(other);
        tab.Categories.RemoveAll(c => c.Entries.Count == 0);
        foreach (Category category in tab.Categories)
        {
            category.Entries.Sort((a, b) => string.Compare(a.NameEn, b.NameEn, StringComparison.OrdinalIgnoreCase));
        }
        FillEntries(tab);
        FurnitureTab = tab;
        Log.Info($"Furniture catalog built: {tab.Entries.Count} entries in {tab.Categories.Count} categories");
    }

    private static SpawnEntry GetFurniture(FurnitureData data, string categoryLabel)
    {
        string key = "furniture|" + data.Id;
        if (EntriesByKey.TryGetValue(key, out SpawnEntry existing))
        {
            return existing;
        }
        string local = Translate(data.DisplayNameKey);
        var entry = new SpawnEntry
        {
            Key = key,
            Id = data.Id,
            Kind = EntryKind.Furniture,
            NameEn = LocaleFile.English.Get(data.Id + FurnitureDataBase.SuffixName) ?? local ?? data.Id,
            NameLocal = local,
            NameRu = LocaleFile.Russian.Get(data.Id + FurnitureDataBase.SuffixName),
            Description = data.DescriptionKey.HasValue ? Translate(data.DescriptionKey.Value) : null,
            CategoryLabel = categoryLabel
        };
        if (data.Rotations != null && data.Rotations.Length > 0 && data.Rotations[0] != null && data.Rotations[0].Length > 0)
        {
            var first = data.Rotations[0][0];
            entry.EntityId = first.BaseId;
            if (first.FrameOptions != null && first.FrameOptions.Length > 0 && first.FrameOptions[0] != null && first.FrameOptions[0].Length > 0)
            {
                entry.Frame = first.FrameOptions[0][0];
            }
        }
        if (data.Recipe.HasValue)
        {
            entry.RecipeBuilding = data.Recipe.Value.BuildingTypeId;
            entry.RecipeLevel = data.Recipe.Value.LevelRequirement;
        }
        entry.SearchText = BuildSearchText(entry.NameEn, entry.NameLocal, entry.NameRu, data.Id);
        EntriesByKey[key] = entry;
        return entry;
    }

    public static string BuildingTypeName(string buildingTypeId)
    {
        string key = buildingTypeId + "*building_type:name";
        return (Loc.IsRussian ? LocaleFile.Russian.Get(key) : null) ?? LocaleFile.English.Get(key) ?? buildingTypeId;
    }

    // ------------------------------------------------------------------ search

    private static string BuildSearchText(params string[] parts)
    {
        return string.Join("\n", parts.Where(part => !string.IsNullOrEmpty(part))).ToLowerInvariant();
    }

    public static List<SpawnEntry> Search(CatalogTab tab, string query)
    {
        if (tab == null)
        {
            return new List<SpawnEntry>();
        }
        string normalized = (query ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized == tab.LastQuery)
        {
            return tab.LastResults;
        }
        tab.LastQuery = normalized;
        string[] terms = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        tab.LastResults = tab.Entries
            .Where(e => terms.All(t => e.SearchText.Contains(t, StringComparison.Ordinal)))
            .OrderBy(e => e.NameEn.StartsWith(normalized, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(e => e.NameEn, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return tab.LastResults;
    }

    // ------------------------------------------------------------------ icons

    public static bool TryGetIcon(SpawnEntry entry, out IconRef icon)
    {
        if (!entry.IconResolved)
        {
            entry.IconResolved = true;
            try
            {
                entry.HasIcon = entry.Kind switch
                {
                    EntryKind.Creature => ResolveCreatureIcon(entry, out entry.Icon),
                    EntryKind.Furniture => ResolveFurnitureIcon(entry, out entry.Icon),
                    _ => ResolveIcon(entry.IconId, out entry.Icon)
                };
            }
            catch (Exception e)
            {
                Log.ErrorOnce("Icon " + entry.Key, e);
            }
        }
        icon = entry.Icon;
        return entry.HasIcon;
    }

    // Icons are frames of the game's UI sprite sheets; bind the sheet texture to ImGui
    // and point the UVs at the frame.
    private static bool ResolveIcon(string iconId, out IconRef icon)
    {
        icon = default;
        if (Globals.ImGuiRenderer == null || iconId == null)
        {
            return false;
        }
        IconData data = IconDataBase.GetIconOrMissing(iconId);
        if (data.Variations == null)
        {
            return false;
        }
        Icon? found = data.GetIconOrNull(IconFlag.Large) ?? data.GetIconOrNull(IconFlag.Medium) ?? data.GetIconOrNull(IconFlag.Small);
        SpriteSheet sheet = found?.SpriteSheet;
        Texture2D texture = sheet?.Texture;
        if (texture == null || texture.IsDisposed || texture.Width == 0 || texture.Height == 0)
        {
            return false;
        }
        XnaRectangle frame = sheet.GetFrame(found.Value.Frame);
        SetIcon(ref icon, texture, frame);
        return true;
    }

    // Creatures have no UI icons: use the sprite the doodad is drawn with, cropped to its visible pixels.
    // Animated creatures that only get their sprite from an animation set fall back to its first idle frame.
    private static bool ResolveCreatureIcon(SpawnEntry entry, out IconRef icon)
    {
        icon = default;
        if (Globals.ImGuiRenderer == null)
        {
            return false;
        }
        if (DoodadDatabaseManager.TryGetEntityBaseData(entry.EntityId, out EntityWrapper data) && TryFrameIcon(data.SpriteSheet, data.Frame, out icon))
        {
            return true;
        }
        foreach (Spac spac in SpacCandidates(entry))
        {
            if (TrySpacIcon(spac, out icon))
            {
                Log.Info($"Icon for {entry.NameEn} taken from animation set '{spac.Name}'");
                return true;
            }
        }
        Log.Warn($"No sprite found for {entry.NameEn} ({entry.EntityId})");
        return false;
    }

    // Furniture has no UI icon of its own either: draw the doodad sprite frame it is placed with,
    // or the blueprint frame the game shows for furniture lying on the ground.
    private static bool ResolveFurnitureIcon(SpawnEntry entry, out IconRef icon)
    {
        icon = default;
        if (Globals.ImGuiRenderer == null)
        {
            return false;
        }
        if (entry.EntityId != Guid.Empty
            && DoodadDatabaseManager.TryGetEntityBaseData(entry.EntityId, out EntityWrapper data)
            && TryFrameIcon(data.SpriteSheet, entry.Frame >= 0 ? entry.Frame : data.Frame, out icon))
        {
            return true;
        }
        return ResolveIcon("furniture_frame", out icon);
    }

    private static bool TryFrameIcon(SpriteSheet sheet, int frame, out IconRef icon)
    {
        icon = default;
        if (sheet == null || ReferenceEquals(sheet, CandideCreator.Shared.Content.UndefinedSprite))
        {
            return false;
        }
        Texture2D texture = sheet.Texture;
        if (texture == null || texture.IsDisposed || texture.Width == 0 || texture.Height == 0 || sheet.SpriteWidth <= 0 || sheet.SpriteHeight <= 0)
        {
            return false;
        }
        XnaRectangle rect;
        try
        {
            rect = sheet.GetFrame(Math.Max(0, frame));
        }
        catch
        {
            return false;
        }
        rect = XnaRectangle.Intersect(rect, new XnaRectangle(0, 0, texture.Width, texture.Height));
        if (rect.Width <= 0 || rect.Height <= 0 || !TrimTransparent(texture, ref rect))
        {
            return false;
        }
        SetIcon(ref icon, texture, rect);
        return true;
    }

    // Shrinks the frame to its non-transparent pixels; false when the frame is empty.
    private static bool TrimTransparent(Texture2D texture, ref XnaRectangle rect)
    {
        if (texture.Format != SurfaceFormat.Color)
        {
            return true;
        }
        var pixels = new XnaColor[rect.Width * rect.Height];
        try
        {
            texture.GetData(0, rect, pixels, 0, pixels.Length);
        }
        catch
        {
            return true;
        }
        int minX = rect.Width;
        int minY = rect.Height;
        int maxX = -1;
        int maxY = -1;
        for (int y = 0; y < rect.Height; y++)
        {
            int row = y * rect.Width;
            for (int x = 0; x < rect.Width; x++)
            {
                if (pixels[row + x].A <= 10)
                {
                    continue;
                }
                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y);
                maxY = Math.Max(maxY, y);
            }
        }
        if (maxX < 0)
        {
            return false;
        }
        rect = new XnaRectangle(rect.X + minX, rect.Y + minY, maxX - minX + 1, maxY - minY + 1);
        return true;
    }

    private static void SetIcon(ref IconRef icon, Texture2D texture, XnaRectangle frame)
    {
        if (!BoundTextures.TryGetValue(texture, out IntPtr id))
        {
            id = Globals.ImGuiRenderer.BindTexture(texture);
            BoundTextures[texture] = id;
        }
        icon.Texture = id;
        icon.Uv0 = new Vec2(frame.X / (float)texture.Width, frame.Y / (float)texture.Height);
        icon.Uv1 = new Vec2(frame.Right / (float)texture.Width, frame.Bottom / (float)texture.Height);
        icon.Size = new Vec2(frame.Width, frame.Height);
    }

    private static List<Spac> SpacCandidates(SpawnEntry entry)
    {
        var result = new List<Spac>();
        if (!string.IsNullOrEmpty(entry.SpacHint) && SpacDataBase.GetSpacOrNull(entry.SpacHint) is Spac hinted)
        {
            result.Add(hinted);
        }
        string key = Normalize(entry.Id);
        string first = Normalize((entry.Id ?? string.Empty).Split('_', ' ')[0]);
        if (key.Length == 0)
        {
            return result;
        }
        var ranked = new List<(Spac Spac, int Score)>();
        foreach (Spac spac in SpacDataBase.SpacMap.Values)
        {
            if (spac?.Name == null || result.Contains(spac))
            {
                continue;
            }
            string name = Normalize(StripSpacName(spac.Name));
            int score = name == key ? 0
                : name.StartsWith(key, StringComparison.Ordinal) ? 1
                : name.Length >= 4 && key.StartsWith(name, StringComparison.Ordinal) ? 2
                : first.Length >= 3 && name.StartsWith(first, StringComparison.Ordinal) ? 3
                : -1;
            if (score >= 0)
            {
                ranked.Add((spac, score));
            }
        }
        result.AddRange(ranked.OrderBy(r => r.Score).ThenBy(r => r.Spac.Name.Length).Take(3).Select(r => r.Spac));
        return result;
    }

    private static bool TrySpacIcon(Spac spac, out IconRef icon)
    {
        icon = default;
        if (spac.SpritesAnimations == null || spac.SpritesAnimations.Length == 0 || spac.SpriteSheets == null)
        {
            return false;
        }
        SpacDirectionsAnimation animation = spac.SpritesAnimations[0];
        foreach (SpacDirectionsAnimation candidate in spac.SpritesAnimations)
        {
            if (candidate.AnimationName != null && candidate.AnimationName.Contains("idle", StringComparison.OrdinalIgnoreCase))
            {
                animation = candidate;
                break;
            }
        }
        if (animation.SpritesAnimations == null)
        {
            return false;
        }
        foreach (SpacAnimation direction in animation.SpritesAnimations)
        {
            if (direction.Frames == null || direction.Frames.Length == 0 || direction.Frames[0].Sprites == null)
            {
                continue;
            }
            // Animated sprites are made of parts (body, head, weapon); the biggest part is the body.
            SpacSprite best = default;
            int bestArea = -1;
            foreach (SpacSprite part in direction.Frames[0].Sprites)
            {
                if (part.SpriteSheetIndex < 0 || part.SpriteSheetIndex >= spac.SpriteSheets.Length || spac.SpriteSheets[part.SpriteSheetIndex] == null)
                {
                    continue;
                }
                SpriteSheet sheet = spac.SpriteSheets[part.SpriteSheetIndex];
                int area = sheet.SpriteWidth * sheet.SpriteHeight;
                if (area > bestArea)
                {
                    best = part;
                    bestArea = area;
                }
            }
            if (bestArea >= 0 && TryFrameIcon(spac.SpriteSheets[best.SpriteSheetIndex], best.Frame, out icon))
            {
                return true;
            }
        }
        return false;
    }

    private static string StripSpacName(string name)
    {
        int slash = name.LastIndexOf('/');
        if (slash >= 0)
        {
            name = name.Substring(slash + 1);
        }
        foreach (string suffix in new[] { "_animations", "_new", "_body" })
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                name = name.Substring(0, name.Length - suffix.Length);
            }
        }
        return name;
    }

    private static string Normalize(string text)
    {
        return new string((text ?? string.Empty).ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
    }
}
