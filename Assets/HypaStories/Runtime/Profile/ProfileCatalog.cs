using UnityEngine;

namespace HypaStories
{
    /// <summary>One choice in the character studio. <see cref="Id"/> names the part's mesh (and its textures) in the character model.</summary>
    public sealed class ProfileOption
    {
        public readonly string Label, Id;
        public readonly Color Color;
        /// <summary>Skin: which base texture (0 light, 1 medium, 2 deep) the tone tints. Outfit colours: unused.</summary>
        public readonly int Band;
        /// <summary>Outfit colour: keep the garment's own colours.</summary>
        public readonly bool Original;
        Texture2D _thumb; bool _looked;

        public ProfileOption(string label, string id = null, string hex = null, int band = 0, bool original = false)
        {
            Label = label; Id = id; Band = band; Original = original;
            Color = Color.white;
            if (hex != null) ColorUtility.TryParseHtmlString("#" + hex, out Color);
        }

        /// <summary>A picture rendered with the model (Resources/HypaAvatar/Thumbs), or null for a word-only tile.</summary>
        public Texture2D Thumb
        {
            get
            {
                if (!_looked && Id != null) { _thumb = Resources.Load<Texture2D>("HypaAvatar/Thumbs/" + Id); _looked = true; }
                return _thumb;
            }
        }
    }

    /// <summary>
    /// Everything the studio offers. Meshes and textures come from the MakeHuman CC0 system assets, built into
    /// Resources/HypaAvatar by tools/avatar/build_avatar.py. Changing an Id here means rebuilding the model.
    /// </summary>
    public static class ProfileCatalog
    {
        public static readonly ProfileOption[] SkinTones =
        {
            new ProfileOption("Porcelain", hex: "F2D3BF", band: 0), new ProfileOption("Ivory", hex: "E8C1A2", band: 0),
            new ProfileOption("Sand", hex: "D9A983", band: 0), new ProfileOption("Honey", hex: "C99470", band: 1),
            new ProfileOption("Golden", hex: "B57F57", band: 1), new ProfileOption("Caramel", hex: "9E6845", band: 1),
            new ProfileOption("Bronze", hex: "855337", band: 2), new ProfileOption("Chestnut", hex: "6C432D", band: 2),
            new ProfileOption("Espresso", hex: "553425", band: 2), new ProfileOption("Ebony", hex: "3E261C", band: 2),
        };

        public static readonly ProfileOption[] EyeColors = { new ProfileOption("Dark brown", "eyes_brown", "4A2C1C") };

        public static readonly ProfileOption[] Brows = { new ProfileOption("Natural", "eyebrow001") };

        public static readonly ProfileOption[] HairStyles =
        {
            new ProfileOption("Curls", "afro01"), new ProfileOption("Short", "short02"), new ProfileOption("Textured", "short01"),
            new ProfileOption("Bob", "bob01"), new ProfileOption("Pulled back", "ponytail01"), new ProfileOption("Long", "long01"),
        };

        public static readonly ProfileOption[] HairColors =
        {
            new ProfileOption("Black", hex: "1B1614"), new ProfileOption("Dark brown", hex: "4A3021"), new ProfileOption("Chestnut", hex: "6E4128"),
            new ProfileOption("Auburn", hex: "8A3E22"), new ProfileOption("Blonde", hex: "D8B983"), new ProfileOption("Silver", hex: "BDBAB5"),
        };

        public static readonly ProfileOption[] Outfits =
        {
            new ProfileOption("Denim shirt", "male_casualsuit01"), new ProfileOption("Plain tee", "male_casualsuit06"),
            new ProfileOption("Striped shirt", "male_casualsuit03"), new ProfileOption("Jacket", "male_casualsuit05"),
            new ProfileOption("Blouse", "female_elegantsuit01"), new ProfileOption("Sport", "female_sportsuit01"),
        };

        public static readonly ProfileOption[] OutfitColors = { new ProfileOption("Original", original: true, hex: "3A3632") };

        public static readonly ProfileOption[] Shoes = { new ProfileOption("Brown leather", "shoes01") };

        /// <summary>The body choice: the <see cref="StoryProfile.frame"/> slider at three settings.</summary>
        public static readonly (string label, float frame)[] Frames = { ("Feminine", 0.15f), ("Neutral", 0.5f), ("Masculine", 0.85f) };

        public static int NearestFrame(float frame)
        {
            int best = 0;
            for (int i = 1; i < Frames.Length; i++) if (Mathf.Abs(Frames[i].frame - frame) < Mathf.Abs(Frames[best].frame - frame)) best = i;
            return best;
        }

        /// <summary>Version 1 styles: coils, locs, sweep, bun, crop.</summary>
        public static int HairFromVersion1(int style)
        {
            string id = style switch { 0 => "afro01", 1 => "short01", 2 => "bob01", 3 => "ponytail01", _ => "short02" };
            for (int i = 0; i < HairStyles.Length; i++) if (HairStyles[i].Id == id) return i;
            return 0;
        }
    }
}
