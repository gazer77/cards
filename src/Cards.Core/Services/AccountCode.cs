using System.Security.Cryptography;
using System.Text;

namespace Cards.Services;

/// <summary>
/// The code that is an account: six short, common words, easy to read out, write down
/// and type on a phone — "amber otter maple river lamp cocoa". There is no name, email
/// or password behind it; whoever holds the code holds the account. Six words from this
/// list are about 55 bits, far beyond guessing, and the server limits how fast anyone
/// can try.
///
/// The server never keeps a code, only its <see cref="Hash"/>.
/// </summary>
public static class AccountCode
{
    public const int Words = 6;

    /// <summary>A new code, from a cryptographic random source.</summary>
    public static string New()
        => string.Join(' ', Enumerable.Range(0, Words).Select(_ => WordList[RandomNumberGenerator.GetInt32(WordList.Count)]));

    /// <summary>
    /// A code as typed, tidied: any case, words split by spaces, dashes, dots or commas.
    /// Null when it is not six words from the list — a typo is caught here, before it
    /// reaches the server as a failed try.
    /// </summary>
    public static string? Normalize(string? typed)
    {
        if (string.IsNullOrWhiteSpace(typed)) return null;
        var words = typed.ToLowerInvariant()
            .Split([' ', '-', '.', ',', '_', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
        return words.Length == Words && words.All(Known.Contains) ? string.Join(' ', words) : null;
    }

    /// <summary>What the server keeps in place of a code. Codes are random, so a plain digest will do.</summary>
    public static string Hash(string normalized)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("cards-account:" + normalized)));

    private static HashSet<string>? _known;
    private static HashSet<string> Known => _known ??= [.. List];

    /// <summary>Short, plain, inoffensive, and none a near-twin of another.</summary>
    public static IReadOnlyList<string> WordList => List;

    private static readonly string[] List =
    [
        // Animals
        "ant", "bat", "bear", "bee", "bison", "boar", "camel", "cat", "clam", "cobra", "colt", "cow",
        "crab", "crane", "crow", "deer", "dingo", "dog", "dove", "duck", "eagle", "eel", "elk", "emu",
        "falcon", "ferret", "finch", "fox", "frog", "gecko", "goat", "goose", "gull", "hare", "hawk", "heron",
        "hippo", "horse", "hound", "ibis", "koala", "lamb", "lark", "lemur", "lion", "llama", "lobster", "lynx",
        "magpie", "mole", "moose", "moth", "mouse", "mule", "newt", "otter", "owl", "panda", "parrot", "pelican",
        "pony", "puffin", "puma", "quail", "rabbit", "raven", "robin", "salmon", "seal", "shark", "sheep", "shrimp",
        "skunk", "sloth", "snail", "sparrow", "squid", "stork", "swan", "tiger", "toad", "trout", "tuna", "turkey",
        "turtle", "walrus", "wasp", "whale", "wolf", "wombat", "wren", "yak", "zebra", "badger", "beaver", "bunny",
        // Food
        "apple", "apricot", "bagel", "banana", "basil", "bean", "berry", "biscuit", "bread", "butter", "cake", "candy",
        "carrot", "celery", "cheese", "cherry", "chili", "cocoa", "coconut", "cookie", "corn", "cream", "curry", "date",
        "donut", "fig", "garlic", "ginger", "grape", "gravy", "honey", "jam", "jelly", "kiwi", "lemon", "lime",
        "mango", "maple", "melon", "mint", "muffin", "noodle", "nut", "oat", "olive", "onion", "orange", "pasta",
        "peach", "peanut", "pear", "pepper", "pickle", "pie", "pizza", "plum", "popcorn", "potato", "pretzel", "pumpkin",
        "radish", "raisin", "rice", "salad", "salsa", "salt", "soup", "spice", "sugar", "syrup", "taco", "tea",
        "toast", "tofu", "tomato", "waffle", "walnut", "yogurt", "almond", "bacon", "barley", "cashew", "cider", "clove",
        // Nature and places
        "acorn", "beach", "bay", "blossom", "branch", "breeze", "brook", "canyon", "cave", "cedar", "cliff", "cloud",
        "coast", "comet", "coral", "creek", "daisy", "dawn", "desert", "dew", "dune", "dusk", "earth", "fern",
        "field", "flower", "fog", "forest", "frost", "garden", "glacier", "grass", "grove", "harbor", "hill", "island",
        "ivy", "jungle", "lake", "lagoon", "leaf", "lily", "lotus", "marsh", "meadow", "mesa", "moon", "moss",
        "mountain", "oak", "ocean", "orchid", "pebble", "pine", "planet", "pond", "poppy", "prairie", "rain", "rainbow",
        "reef", "ridge", "river", "rock", "rose", "sand", "sky", "snow", "spring", "star", "stone", "storm",
        "stream", "summit", "sun", "sunset", "thunder", "tide", "tulip", "valley", "volcano", "wave", "willow", "wind",
        "winter", "autumn", "summer", "aspen", "birch", "bamboo", "cactus", "clover", "elm", "hazel", "heather", "iris",
        // Things
        "anchor", "arrow", "badge", "bag", "ball", "balloon", "banjo", "barrel", "basket", "bell", "bench", "bicycle",
        "blanket", "boat", "book", "boot", "bottle", "bowl", "box", "brick", "bridge", "broom", "brush", "bucket",
        "button", "cabin", "camera", "candle", "canoe", "cap", "card", "carpet", "castle", "chair", "chalk", "clock",
        "coin", "compass", "crayon", "crown", "cup", "curtain", "desk", "dice", "drum", "feather", "fence", "flag",
        "flute", "fork", "fountain", "frame", "garage", "gate", "glove", "guitar", "hammer", "hat", "helmet", "jacket",
        "jar", "jewel", "kettle", "key", "kite", "ladder", "lamp", "lantern", "lens", "letter", "lock", "magnet",
        "map", "marble", "mask", "mirror", "mitten", "nail", "needle", "net", "notebook", "oar", "paddle", "paint",
        "pan", "paper", "pencil", "piano", "pillow", "pipe", "plate", "pocket", "puzzle", "quilt", "radio", "raft",
        "ribbon", "ring", "robot", "rocket", "rope", "ruler", "saddle", "sail", "scarf", "shell", "shield", "shoe",
        "sled", "sock", "sofa", "spoon", "stamp", "stool", "string", "sweater", "table", "teapot", "tent", "ticket",
        "tile", "torch", "towel", "tower", "toy", "tractor", "train", "trumpet", "tube", "umbrella", "vase", "violin",
        "wagon", "wallet", "wand", "watch", "wheel", "whistle", "window", "yarn", "zipper", "anvil", "axle", "barn",
        // Colors and qualities
        "amber", "azure", "beige", "black", "blue", "bronze", "brown", "copper", "crimson", "cyan", "gold", "golden",
        "gray", "green", "indigo", "ivory", "jade", "lilac", "magenta", "maroon", "navy", "ochre", "khaki", "pink",
        "purple", "red", "ruby", "rust", "scarlet", "silver", "tan", "teal", "violet", "white", "yellow", "bold",
        "brave", "bright", "brisk", "calm", "clever", "cosy", "crisp", "curious", "daring", "eager", "early", "fancy",
        "fast", "fierce", "fluffy", "fresh", "friendly", "gentle", "giant", "glad", "grand", "happy", "honest", "humble",
        "jolly", "keen", "kind", "lively", "loyal", "lucky", "merry", "mighty", "neat", "nimble", "noble", "plucky",
        "polite", "proud", "quick", "quiet", "rapid", "royal", "shiny", "silent", "simple", "sleepy", "smart", "smooth",
        "snowy", "soft", "steady", "sturdy", "sunny", "sweet", "swift", "tidy", "tiny", "warm", "wise", "witty",
        // Doing words
        "bake", "bounce", "build", "carry", "chase", "climb", "dance", "dig", "dive", "draw", "dream", "drift",
        "fetch", "float", "fly", "gather", "glide", "grow", "hike", "hop", "hum", "jog", "juggle", "jump",
        "knit", "laugh", "march", "mend", "doodle", "plant", "play", "race", "read", "ride", "roam", "row",
        "run", "wiggle", "sing", "skate", "ski", "skip", "sleep", "slide", "smile", "sort", "spin", "splash",
        "sprint", "stack", "stroll", "surf", "swim", "swing", "think", "travel", "tumble", "twirl", "wander", "waddle",
        "whisper", "wink", "write", "yodel", "zoom", "giggle", "hatch", "hover", "nap", "nest", "scribble", "pounce",
    ];
}
