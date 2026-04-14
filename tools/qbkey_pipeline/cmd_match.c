/*
 * cmd_match.c — Stage 2: Fast dictionary-based hash matching.
 *
 * Builds a dictionary from multiple sources, generates variants, and matches
 * against QBKey (reflected CRC-32) and Crc32Neversoft (rotate-left) targets.
 *
 * Usage: qbkey_pipeline match [builds_path]
 */

/* ── Dictionary storage ─────────────────────────────────────────────── */

#define MATCH_MAX_TARGETS   131072
#define MATCH_MAX_DICT      (8 * 1024 * 1024)
#define MATCH_TARGET_BITS   17
#define MATCH_TARGET_SIZE   (1 << MATCH_TARGET_BITS)
#define MATCH_TARGET_MASK   (MATCH_TARGET_SIZE - 1)

static const char **match_dict = NULL;
static size_t match_dict_count = 0;
static strset_t match_strset;

static void match_dict_init(void)
{
    match_dict = (const char **)malloc(MATCH_MAX_DICT * sizeof(const char *));
    if (!match_dict) { fprintf(stderr, "Failed to allocate dictionary\n"); exit(1); }
    match_dict_count = 0;
    strset_init(&match_strset);
}

/* Add a word to the dictionary (lowercased, validated, deduplicated) */
static void match_dict_add(const char *word)
{
    size_t len = strlen(word);
    if (len < 2 || len >= MAX_NAME_LEN) return;
    if (match_dict_count >= MATCH_MAX_DICT) return;

    char buf[MAX_NAME_LEN];
    for (size_t i = 0; i < len; i++) {
        char c = word[i];
        if (c >= 'A' && c <= 'Z') c += 32;
        if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '.' || c == '-'))
            return;
        buf[i] = c;
    }
    buf[len] = '\0';

    if (strset_contains(&match_strset, buf))
        return;
    const char *copy = arena_strdup(buf);
    strset_add(&match_strset, copy);
    match_dict[match_dict_count++] = copy;
}

/* Add a word that's already lowercase and validated */
static void match_dict_add_raw(const char *word)
{
    size_t len = strlen(word);
    if (len < 2 || len >= MAX_NAME_LEN) return;
    if (match_dict_count >= MATCH_MAX_DICT) return;

    if (strset_contains(&match_strset, word))
        return;
    const char *copy = arena_strdup(word);
    strset_add(&match_strset, copy);
    match_dict[match_dict_count++] = copy;
}

static int match_is_token_char(char c)
{
    return (c >= 'a' && c <= 'z') ||
           (c >= 'A' && c <= 'Z') ||
           (c >= '0' && c <= '9');
}

static int match_string_has_alpha(const char *s)
{
    for (; *s; s++) {
        if ((*s >= 'a' && *s <= 'z') || (*s >= 'A' && *s <= 'Z'))
            return 1;
    }
    return 0;
}

static void match_dict_add_tokenized(const char *text)
{
    char token[MAX_NAME_LEN];
    size_t len = 0;

    for (const unsigned char *p = (const unsigned char *)text;; p++) {
        unsigned char c = *p;
        if (match_is_token_char((char)c)) {
            if (len < MAX_NAME_LEN - 1)
                token[len++] = (char)tolower(c);
        } else {
            if (len >= 2) {
                token[len] = '\0';
                match_dict_add_raw(token);
            }
            len = 0;
            if (c == '\0')
                break;
        }
    }
}

static void match_dict_add_stem_chain(const char *name)
{
    char stem[MAX_NAME_LEN];
    path_stem_lower(name, stem, sizeof(stem));
    while (strlen(stem) >= 2) {
        match_dict_add_raw(stem);
        match_dict_add_tokenized(stem);

        char *dot = strrchr(stem, '.');
        if (!dot)
            break;
        *dot = '\0';
    }
}

static void match_dict_add_path_components(const char *path)
{
    size_t len = strlen(path);
    if (len == 0 || len >= MAX_NAME_LEN)
        return;

    char buf[MAX_NAME_LEN];
    memcpy(buf, path, len + 1);

    for (char *part = strtok(buf, "\\/"); part; part = strtok(NULL, "\\/")) {
        if (strlen(part) >= 2) {
            match_dict_add(part);
            match_dict_add_tokenized(part);
            match_dict_add_stem_chain(part);
        }
    }
}

static void match_dict_add_source_text(const char *text)
{
    if (!text || !*text)
        return;

    match_dict_add(text);
    match_dict_add_tokenized(text);

    const char *base = path_basename(text);
    if (base != text)
        match_dict_add_path_components(text);

    if (*base) {
        match_dict_add(base);
        match_dict_add_tokenized(base);
        match_dict_add_stem_chain(base);
    }
}

static int match_path_exists(const char *path)
{
    DWORD attrs = GetFileAttributesA(path);
    return attrs != INVALID_FILE_ATTRIBUTES;
}

static int match_find_repo_relative(const char *relative, char *out, size_t outsize)
{
    char current[MAX_PATH * 2];
    DWORD cwd_len = GetCurrentDirectoryA((DWORD)sizeof(current), current);
    if (cwd_len > 0 && cwd_len < sizeof(current)) {
        for (;;) {
            snprintf(out, outsize, "%s\\%s", current, relative);
            if (match_path_exists(out))
                return 1;

            char *last = strrchr(current, '\\');
            if (!last)
                break;
            *last = '\0';
        }
    }

    get_tools_dir(current, sizeof(current));
    for (;;) {
        snprintf(out, outsize, "%s\\%s", current, relative);
        if (match_path_exists(out))
            return 1;

        char *last = strrchr(current, '\\');
        if (!last)
            break;
        *last = '\0';
    }

    out[0] = '\0';
    return 0;
}

/* ── Target hash set ────────────────────────────────────────────────── */

typedef struct {
    uint32_t hashes[MATCH_MAX_TARGETS];
    const char *names[MATCH_MAX_TARGETS];
    int count;
    int ht[MATCH_TARGET_SIZE];
} match_target_set_t;

static match_target_set_t match_qb_targets;
static match_target_set_t match_hed_targets;

static void match_target_init(match_target_set_t *ts)
{
    ts->count = 0;
    memset(ts->ht, -1, sizeof(ts->ht));
}

static void match_target_add(match_target_set_t *ts, uint32_t hash)
{
    uint32_t idx = hash & MATCH_TARGET_MASK;
    while (ts->ht[idx] != -1) {
        if (ts->hashes[ts->ht[idx]] == hash)
            return;
        idx = (idx + 1) & MATCH_TARGET_MASK;
    }
    if (ts->count >= MATCH_MAX_TARGETS) return;
    int slot = ts->count++;
    ts->hashes[slot] = hash;
    ts->names[slot] = NULL;
    ts->ht[idx] = slot;
}

static int match_target_find(const match_target_set_t *ts, uint32_t hash)
{
    uint32_t idx = hash & MATCH_TARGET_MASK;
    while (ts->ht[idx] != -1) {
        if (ts->hashes[ts->ht[idx]] == hash)
            return ts->ht[idx];
        idx = (idx + 1) & MATCH_TARGET_MASK;
    }
    return -1;
}

/* ── JSON target loader ─────────────────────────────────────────────── */

static void match_extract_hashes(const char *start, const char *end, match_target_set_t *ts)
{
    const char *p = start;
    while (p < end - 12) {
        p = (const char *)memchr(p, '"', end - p);
        if (!p || p >= end - 12) break;
        if (p[1] == '0' && p[2] == 'x') {
            uint32_t hash;
            if (parse_hex8(p + 3, &hash))
                match_target_add(ts, hash);
            p += 12;
        } else {
            p++;
        }
    }
}

static void match_load_targets(const char *path)
{
    size_t size;
    char *data = read_file(path, &size);

    const char *sections[] = {
        "\"mesh_hashes\"",
        "\"texture_hashes\"",
        "\"material_hashes\"",
        "\"material_name_hashes\"",
        "\"hed_hashes\"",
        NULL
    };
    match_target_set_t *targets[] = {
        &match_qb_targets,
        &match_qb_targets,
        &match_qb_targets,
        &match_qb_targets,
        &match_hed_targets
    };

    for (int s = 0; sections[s]; s++) {
        const char *key = strstr(data, sections[s]);
        if (!key) continue;

        const char *brace = strchr(key + strlen(sections[s]), '{');
        if (!brace) continue;

        int depth = 1;
        const char *p = brace + 1;
        while (*p && depth > 0) {
            if (*p == '{') depth++;
            else if (*p == '}') depth--;
            p++;
        }
        match_extract_hashes(brace, p, targets[s]);
    }

    free(data);
}

/* ── Dictionary source: build filenames ─────────────────────────────── */

static int match_builds_file_count = 0;

static void match_on_build_file(const char *filepath, const char *filename, void *ctx)
{
    (void)filepath; (void)ctx;
    match_builds_file_count++;

    match_dict_add_source_text(filename);
}

/* ── Dictionary source: plaintext HED filenames ─────────────────────── */

static int match_hed_names_count = 0;

static void match_parse_hed(const char *filepath)
{
    size_t size;
    uint8_t *data = read_file_bin(filepath, &size);
    if (!data) return;

    if (size < 4 || data[0] < 0x20 || data[0] > 0x7E) {
        free(data);
        return;
    }

    size_t pos = 0;
    while (pos < size - 7) {
        if (data[pos] == 0xFF) break;

        size_t end = pos;
        while (end < size && data[end] != 0) end++;
        if (end >= size) break;

        size_t name_len = end - pos;
        if (name_len >= 2 && name_len < MAX_NAME_LEN) {
            char name[MAX_NAME_LEN];
            memcpy(name, data + pos, name_len);
            name[name_len] = '\0';

            int valid = 1;
            for (size_t i = 0; i < name_len; i++) {
                if (name[i] < 0x20 || (uint8_t)name[i] > 0x7E) { valid = 0; break; }
            }
            if (valid) {
                match_dict_add_source_text(name);
                match_hed_names_count++;
            }
        }

        pos = end + 1;
        if (pos % 4 != 0) pos += 4 - (pos % 4);
        pos += 8;
    }

    free(data);
}

static void match_on_hed_file(const char *filepath, const char *filename, void *ctx)
{
    (void)ctx;
    size_t len = strlen(filename);
    if (len < 4) return;
    if (_stricmp(filename + len - 4, ".hed") == 0)
        match_parse_hed(filepath);
}

/* ── Dictionary source: SYM debug strings ───────────────────────────── */

static int match_sym_count = 0;

static void match_load_sym(const char *sym_path)
{
    size_t size;
    uint8_t *data = read_file_bin(sym_path, &size);
    if (!data) {
        fprintf(stderr, "  Warning: cannot open SYM file: %s\n", sym_path);
        return;
    }

    char current[MAX_NAME_LEN];
    int cur_len = 0;

    for (size_t i = 0; i < size; i++) {
        uint8_t b = data[i];
        if (b >= 0x20 && b <= 0x7E) {
            if (cur_len < MAX_NAME_LEN - 1)
                current[cur_len++] = (char)b;
        } else {
            if (cur_len >= 3 && cur_len <= 20) {
                current[cur_len] = '\0';
                char c0 = current[0];
                if ((c0 >= 'a' && c0 <= 'z') || (c0 >= 'A' && c0 <= 'Z')) {
                    int valid = 1;
                    for (int j = 1; j < cur_len; j++) {
                        char c = current[j];
                        if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                              (c >= '0' && c <= '9') || c == '_')) {
                            valid = 0;
                            break;
                        }
                    }
                    if (valid) {
                        match_dict_add_source_text(current);
                        match_sym_count++;
                    }
                }
            }
            cur_len = 0;
        }
    }

    free(data);
}

/* ── Dictionary source: archive-extracted names (all_names.txt) ──────── */

static int match_archive_count = 0;

static void match_load_archive_names(const char *path)
{
    FILE *f = fopen(path, "r");
    if (!f) {
        fprintf(stderr, "  Warning: cannot open %s\n", path);
        return;
    }

    char line[MAX_NAME_LEN];
    while (fgets(line, sizeof(line), f)) {
        size_t len = strlen(line);
        while (len > 0 && (line[len-1] == '\n' || line[len-1] == '\r'))
            line[--len] = '\0';
        if (len >= 2) {
            match_dict_add_source_text(line);
            match_archive_count++;
        }
    }
    fclose(f);
}

/* ── Dictionary source: known-name tables and prior matches ─────────────── */

static int match_known_name_count = 0;
static int match_known_name_files = 0;
static int match_prior_match_count = 0;

static void match_trim_ascii(char *s)
{
    char *start = s;
    while (*start && isspace((unsigned char)*start))
        start++;

    if (start != s)
        memmove(s, start, strlen(start) + 1);

    size_t len = strlen(s);
    while (len > 0 && isspace((unsigned char)s[len - 1]))
        s[--len] = '\0';
}

static void match_load_known_name_file(const char *path)
{
    FILE *f = fopen(path, "r");
    if (!f) {
        fprintf(stderr, "  Warning: cannot open %s\n", path);
        return;
    }

    char line[1024];
    while (fgets(line, sizeof(line), f)) {
        char *eq;
        size_t len = strlen(line);
        while (len > 0 && (line[len - 1] == '\n' || line[len - 1] == '\r'))
            line[--len] = '\0';

        match_trim_ascii(line);
        if (line[0] == '\0' || line[0] == '#')
            continue;

        eq = strchr(line, '=');
        if (!eq)
            continue;
        *eq = '\0';
        match_trim_ascii(line);
        if (line[0] == '\0')
            continue;

        match_dict_add_source_text(line);
        match_known_name_count++;
    }

    fclose(f);
    match_known_name_files++;
}

static void match_load_known_name_tables(void)
{
    char qbkey_dir[MAX_PATH * 2];
    if (!match_find_repo_relative("src\\NeversoftMultitool\\Core\\QbKey", qbkey_dir, sizeof(qbkey_dir))) {
        fprintf(stderr, "  Warning: could not locate src\\NeversoftMultitool\\Core\\QbKey\n");
        return;
    }

    char pattern[MAX_PATH * 2];
    snprintf(pattern, sizeof(pattern), "%s\\QbKeyNames*.txt", qbkey_dir);

    WIN32_FIND_DATAA fd;
    HANDLE hFind = FindFirstFileA(pattern, &fd);
    if (hFind == INVALID_HANDLE_VALUE) {
        fprintf(stderr, "  Warning: no QbKeyNames*.txt files found in %s\n", qbkey_dir);
        return;
    }

    do {
        char path[MAX_PATH * 2];
        if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY)
            continue;
        snprintf(path, sizeof(path), "%s\\%s", qbkey_dir, fd.cFileName);
        match_load_known_name_file(path);
    } while (FindNextFileA(hFind, &fd));

    FindClose(hFind);
}

static void match_load_prior_matches(const char *path)
{
    FILE *f = fopen(path, "r");
    if (!f)
        return;

    char line[1024];
    while (fgets(line, sizeof(line), f)) {
        char *hash_key = strstr(line, "\"0x");
        if (!hash_key)
            continue;

        char *colon = strchr(hash_key, ':');
        if (!colon)
            continue;

        char *q1 = strchr(colon, '"');
        if (!q1)
            continue;
        q1++;

        char *q2 = strchr(q1, '"');
        if (!q2)
            continue;

        size_t len = (size_t)(q2 - q1);
        if (len < 2 || len >= MAX_NAME_LEN)
            continue;

        char name[MAX_NAME_LEN];
        memcpy(name, q1, len);
        name[len] = '\0';
        match_dict_add_source_text(name);
        match_prior_match_count++;
    }

    fclose(f);
}

/* ── Dictionary source: script-like files ───────────────────────────────── */

static int match_script_file_count = 0;
static int match_script_string_count = 0;
static int match_decompiled_script_count = 0;
static int match_seeded_name_count = 0;
static int match_grammar_name_count = 0;

static int match_has_script_extension(const char *filename)
{
    char lower[MAX_NAME_LEN];
    size_t len = strlen(filename);
    if (len >= sizeof(lower))
        len = sizeof(lower) - 1;
    for (size_t i = 0; i < len; i++)
        lower[i] = (char)tolower((unsigned char)filename[i]);
    lower[len] = '\0';

    if (strstr(lower, ".qb") != NULL)
        return 1;
    if (strstr(lower, ".trg") != NULL)
        return 1;
    if (len >= 2 && strcmp(lower + len - 2, ".q") == 0)
        return 1;
    return 0;
}

static void match_flush_script_run(char *buf, size_t len)
{
    if (len < 2 || len >= MAX_NAME_LEN)
        return;
    buf[len] = '\0';

    if (!match_string_has_alpha(buf))
        return;

    match_dict_add_source_text(buf);
    match_script_string_count++;
}

static void match_scan_script_file(const char *filepath)
{
    size_t size;
    uint8_t *data = read_file_bin(filepath, &size);
    if (!data)
        return;

    match_script_file_count++;

    char buf[MAX_NAME_LEN];
    size_t len = 0;

    for (size_t i = 0; i < size; i++) {
        unsigned char c = data[i];
        if (match_is_token_char((char)c) || c == '_' || c == '.' || c == '-' || c == '\\' || c == '/') {
            if (len < MAX_NAME_LEN - 1)
                buf[len++] = (char)c;
        } else if (len > 0) {
            match_flush_script_run(buf, len);
            len = 0;
        }
    }

    if (len > 0)
        match_flush_script_run(buf, len);

    free(data);
}

static void match_on_script_file(const char *filepath, const char *filename, void *ctx)
{
    (void)ctx;
    if (match_has_script_extension(filename))
        match_scan_script_file(filepath);
}

static void match_load_line_name_file(const char *path, int *counter)
{
    FILE *f = fopen(path, "r");
    if (!f)
        return;

    char line[1024];
    while (fgets(line, sizeof(line), f)) {
        size_t len = strlen(line);
        while (len > 0 && (line[len - 1] == '\n' || line[len - 1] == '\r'))
            line[--len] = '\0';
        if (len >= 2) {
            match_dict_add_source_text(line);
            if (counter)
                (*counter)++;
        }
    }

    fclose(f);
}

/* ── Dictionary source: hardcoded game-specific names ────────────────── */

static const char *match_game_names[] = {
    /* Body parts */
    "head", "torso", "hip", "pelvis", "neck", "jaw", "spine", "chest",
    "left_bicep", "right_bicep", "left_forearm", "right_forearm",
    "left_hand", "right_hand", "left_thigh", "right_thigh",
    "left_calf", "right_calf", "left_foot", "right_foot",
    "left_shoulder", "right_shoulder", "left_elbow", "right_elbow",
    "left_knee", "right_knee", "left_wrist", "right_wrist",
    "left_ankle", "right_ankle",
    "upper_body", "lower_body", "upper_arm", "lower_arm",
    "upper_leg", "lower_leg",
    "l_arm", "r_arm", "l_leg", "r_leg", "l_hand", "r_hand",
    "l_foot", "r_foot", "l_shoulder", "r_shoulder",
    "face", "hair", "eyes", "mouth", "teeth",
    /* THPS skater names */
    "hawk", "hawk2", "muska", "muska2", "mullen", "burnq", "burnquist",
    "caball", "cabalero", "campbell", "koston", "lasek", "margera",
    "reynolds", "rowley", "steamer", "thomas", "bucky", "officer_dick",
    "mctwist", "privateer", "spider", "spidey",
    "burnq2", "burnq_2p", "hawk_2p", "muska_2p",
    "cabll", "campb", "kostn", "marge", "reyno",
    "rowly", "steam", "thoma",
    /* Skateboard/trick */
    "board", "deck", "wheels", "trucks", "grip", "shadow",
    "hat", "cap", "helmet", "glasses", "sunglasses",
    "shirt", "pants", "shorts", "shoes", "boots", "jacket", "vest",
    "skin", "denim", "leather", "metal", "chrome", "rubber", "plastic",
    "logo", "decal", "sticker", "stripe",
    /* Neversoft prefixes */
    "sk", "bk", "sp", "sm",
    /* Common asset patterns */
    "default", "white", "black", "grey", "red", "blue", "green",
    "lo", "hi", "lod0", "lod1", "lod2",
    "glow", "alpha", "mask",
    /* Apocalypse */
    "gun", "weapon", "muzzle", "barrel", "trigger",
    "bruce", "swat", "tank", "chopper", "turret", "demon", "zombie",
    "prisoner", "guard", "punk", "bat", "hound", "esca", "hoff",
    /* Spider-Man */
    "spiderman", "venom", "carnage", "rhino", "mysterio",
    "electro", "scorpion", "shocker", "vulture", "sandman",
    "lizard", "doc_ock", "hobgoblin", "green_goblin",
    "web", "webline",
    /* Levels */
    "warehouse", "school", "hangar", "factory", "downtown",
    "burnside", "roswell", "bullring", "chopper_drop",
    "marseille", "ny_city", "venice", "skatestreet",
    "philadelphia", "foundry", "alcatraz",
    /* Common textures */
    "tex", "texture", "diffuse", "normal", "spec",
    "front", "back", "left", "right", "top", "bottom",
    "floor", "wall", "ceiling", "ground", "sky",
    "door", "window", "sign", "fence", "rail", "ramp",
    "pipe", "box", "crate",
    "water", "lava", "grass", "dirt", "concrete", "asphalt",
    "brick", "wood", "tile", "glass", "steel",
    /* UI */
    "font", "cursor", "button", "arrow", "icon",
    "menu", "title", "loading", "pause", "score",
    "timer", "health", "lives", "meter",
    NULL
};

/* ── Variant generation ─────────────────────────────────────────────── */

static int match_is_alpha_only(const char *s)
{
    for (int i = 0; s[i]; i++)
        if (!((s[i] >= 'a' && s[i] <= 'z') || (s[i] >= 'A' && s[i] <= 'Z')))
            return 0;
    return 1;
}

static int match_is_vowel(char c)
{
    return c == 'a' || c == 'e' || c == 'i' || c == 'o' || c == 'u';
}

static void match_generate_variants(size_t start, size_t end)
{
    size_t base_count = end;
    char buf[MAX_NAME_LEN + 16];

    for (size_t i = start; i < base_count; i++) {
        const char *w = match_dict[i];
        size_t len = strlen(w);
        if (len < 2) continue;
        int word_is_alpha = match_is_alpha_only(w);

        /* Stem without extension */
        if (strchr(w, '.')) {
            strncpy(buf, w, sizeof(buf) - 1);
            buf[sizeof(buf) - 1] = '\0';
            char *dot = strrchr(buf, '.');
            if (dot) {
                *dot = '\0';
                if (strlen(buf) >= 2)
                    match_dict_add_raw(buf);
            }
        }

        /* Abbreviation: remove vowels (keep first char) */
        if (len >= 4 && word_is_alpha) {
            int bpos = 0;
            buf[bpos++] = w[0];
            for (size_t j = 1; j < len && bpos < MAX_NAME_LEN - 1; j++) {
                if (!match_is_vowel(w[j]))
                    buf[bpos++] = w[j];
            }
            buf[bpos] = '\0';
            if (bpos >= 2 && strcmp(buf, w) != 0)
                match_dict_add_raw(buf);
        }

        /* Truncations: first 2-6 characters */
        if (len >= 5 && word_is_alpha) {
            int max_trunc = len < 7 ? (int)len : 7;
            for (int n = 2; n < max_trunc; n++) {
                memcpy(buf, w, n);
                buf[n] = '\0';
                match_dict_add_raw(buf);
            }
        }

        /* Digit suffixes */
        if (word_is_alpha) {
            for (int d = 0; d <= 9; d++) {
                snprintf(buf, sizeof(buf), "%s%d", w, d);
                match_dict_add_raw(buf);
                snprintf(buf, sizeof(buf), "%s_%d", w, d);
                match_dict_add_raw(buf);
            }
            snprintf(buf, sizeof(buf), "%s_2p", w);
            match_dict_add_raw(buf);
            snprintf(buf, sizeof(buf), "%s01", w);
            match_dict_add_raw(buf);
            snprintf(buf, sizeof(buf), "%s02", w);
            match_dict_add_raw(buf);
        }
    }
}

/* ── Compound body part names ───────────────────────────────────────── */

static const char *match_body_parts[] = {
    "arm", "leg", "hand", "foot", "bicep", "forearm", "thigh", "calf",
    "shoulder", "elbow", "knee", "wrist", "ankle", "shin", "toe",
    "finger", "thumb", "hip", "eye", "ear", NULL
};

static const char *match_modifiers[] = {
    "left", "right", "l", "r", "front", "back", "upper", "lower", NULL
};

static void match_generate_compounds(void)
{
    char buf[MAX_NAME_LEN];
    for (int m = 0; match_modifiers[m]; m++) {
        for (int p = 0; match_body_parts[p]; p++) {
            snprintf(buf, sizeof(buf), "%s_%s", match_modifiers[m], match_body_parts[p]);
            match_dict_add_raw(buf);
            snprintf(buf, sizeof(buf), "%s%s", match_modifiers[m], match_body_parts[p]);
            match_dict_add_raw(buf);
        }
    }
}

/* ── Hash matching ──────────────────────────────────────────────────── */

static int match_qb_matched = 0, match_hed_matched = 0;

static void match_do_matching(void)
{
    for (size_t i = 0; i < match_dict_count; i++) {
        const char *name = match_dict[i];
        size_t len = strlen(name);

        uint32_t h1 = qbkey(name);
        int slot = match_target_find(&match_qb_targets, h1);
        if (slot >= 0) {
            if (match_qb_targets.names[slot] == NULL) {
                match_qb_targets.names[slot] = name;
                match_qb_matched++;
            } else if (len < strlen(match_qb_targets.names[slot])) {
                match_qb_targets.names[slot] = name;
            }
        }

        uint32_t h2 = crc32_neversoft(name);
        slot = match_target_find(&match_hed_targets, h2);
        if (slot >= 0) {
            const char *existing = match_hed_targets.names[slot];
            if (existing == NULL) {
                match_hed_targets.names[slot] = name;
                match_hed_matched++;
            } else {
                int new_has_dot = (strchr(name, '.') != NULL);
                int old_has_dot = (strchr(existing, '.') != NULL);
                if (new_has_dot && !old_has_dot) {
                    match_hed_targets.names[slot] = name;
                } else if (len < strlen(existing) && !old_has_dot) {
                    match_hed_targets.names[slot] = name;
                }
            }
        }
    }
}

/* ── JSON output ────────────────────────────────────────────────────── */

static void match_write_output(const char *tools_dir)
{
    char path[MAX_PATH];

    /* matched_hashes.json */
    snprintf(path, sizeof(path), "%s\\matched_hashes.json", tools_dir);
    FILE *f = fopen(path, "w");
    if (!f) { fprintf(stderr, "Cannot write %s\n", path); return; }

    fprintf(f, "{\n  \"qbkey_matches\": {\n");
    int first = 1;
    for (int i = 0; i < match_qb_targets.count; i++) {
        if (match_qb_targets.names[i]) {
            if (!first) fprintf(f, ",\n");
            fprintf(f, "    \"0x%08X\": \"%s\"", match_qb_targets.hashes[i], match_qb_targets.names[i]);
            first = 0;
        }
    }
    fprintf(f, "\n  },\n  \"hed_matches\": {\n");
    first = 1;
    for (int i = 0; i < match_hed_targets.count; i++) {
        if (match_hed_targets.names[i]) {
            if (!first) fprintf(f, ",\n");
            fprintf(f, "    \"0x%08X\": \"%s\"", match_hed_targets.hashes[i], match_hed_targets.names[i]);
            first = 0;
        }
    }

    int qb_unmatched = match_qb_targets.count - match_qb_matched;
    int hed_unmatched = match_hed_targets.count - match_hed_matched;
    fprintf(f, "\n  },\n  \"stats\": {\n");
    fprintf(f, "    \"dictionary_size\": %zu,\n", match_dict_count);
    fprintf(f, "    \"qb_targets\": %d,\n", match_qb_targets.count);
    fprintf(f, "    \"hed_targets\": %d,\n", match_hed_targets.count);
    fprintf(f, "    \"qb_matched\": %d,\n", match_qb_matched);
    fprintf(f, "    \"hed_matched\": %d,\n", match_hed_matched);
    fprintf(f, "    \"qb_unmatched\": %d,\n", qb_unmatched);
    fprintf(f, "    \"hed_unmatched\": %d\n", hed_unmatched);
    fprintf(f, "  }\n}\n");
    fclose(f);
    fprintf(stderr, "  Wrote: %s\n", path);

    /* unmatched_qb_hashes.txt */
    snprintf(path, sizeof(path), "%s\\unmatched_qb_hashes.txt", tools_dir);
    f = fopen(path, "w");
    if (f) {
        uint32_t *unmatched = (uint32_t *)malloc(match_qb_targets.count * sizeof(uint32_t));
        int uc = 0;
        for (int i = 0; i < match_qb_targets.count; i++) {
            if (match_qb_targets.names[i] == NULL)
                unmatched[uc++] = match_qb_targets.hashes[i];
        }
        qsort(unmatched, uc, sizeof(uint32_t), uint32_cmp);
        for (int i = 0; i < uc; i++)
            fprintf(f, "0x%08X\n", unmatched[i]);
        fclose(f);
        free(unmatched);
        fprintf(stderr, "  Wrote: %s (%d hashes)\n", path, uc);
    }

    /* unmatched_hed_hashes.txt */
    snprintf(path, sizeof(path), "%s\\unmatched_hed_hashes.txt", tools_dir);
    f = fopen(path, "w");
    if (f) {
        for (int i = 0; i < match_hed_targets.count; i++) {
            if (match_hed_targets.names[i] == NULL)
                fprintf(f, "0x%08X\n", match_hed_targets.hashes[i]);
        }
        fclose(f);
        fprintf(stderr, "  Wrote: %s\n", path);
    }
}

/* ── Main entry ─────────────────────────────────────────────────────── */

static int cmd_match(int argc, char **argv)
{
    double t_start = get_time();
    const char *builds = BUILDS_DEFAULT;
    char tools_dir[MAX_PATH];

    get_tools_dir(tools_dir, sizeof(tools_dir));
    if (argc > 1) builds = argv[1];

    fprintf(stderr, "Stage 2: Fast dictionary-based hash matching\n");
    fprintf(stderr, "============================================================\n\n");
    fprintf(stderr, "Tools dir: %s\n", tools_dir);
    fprintf(stderr, "Builds:    %s\n\n", builds);

    init_crc_table();
    arena_init();
    match_dict_init();
    match_target_init(&match_qb_targets);
    match_target_init(&match_hed_targets);

    /* Load targets */
    double t0 = get_time();
    char path[MAX_PATH];
    snprintf(path, sizeof(path), "%s\\hash_targets.json", tools_dir);
    match_load_targets(path);
    fprintf(stderr, "Loading targets: %.2fs\n", get_time() - t0);
    fprintf(stderr, "  QBKey targets (mesh+texture): %d\n", match_qb_targets.count);
    fprintf(stderr, "  Crc32Neversoft targets (HED): %d\n\n", match_hed_targets.count);

    /* Build dictionary */
    fprintf(stderr, "Building dictionary...\n");

    t0 = get_time();
    size_t before = match_dict_count;
    walk_directory(builds, match_on_build_file, NULL);
    fprintf(stderr, "  Build filenames: %zu new (%d files walked, %.2fs)\n",
            match_dict_count - before, match_builds_file_count, get_time() - t0);

    t0 = get_time();
    before = match_dict_count;
    walk_directory(builds, match_on_hed_file, NULL);
    fprintf(stderr, "  HED filenames: %zu new (%d parsed, %.2fs)\n",
            match_dict_count - before, match_hed_names_count, get_time() - t0);

    t0 = get_time();
    before = match_dict_count;
    snprintf(path, sizeof(path), "%s\\Tony Hawk's Pro Skater 2 (2000-3-29, PSX - Prototype)\\MAIN.SYM",
             builds);
    match_load_sym(path);
    fprintf(stderr, "  SYM strings: %zu new (%d found, %.2fs)\n",
            match_dict_count - before, match_sym_count, get_time() - t0);

    t0 = get_time();
    before = match_dict_count;
    match_load_known_name_tables();
    fprintf(stderr, "  Known-name tables: %zu new (%d entries from %d files, %.2fs)\n",
            match_dict_count - before, match_known_name_count, match_known_name_files,
            get_time() - t0);

    t0 = get_time();
    before = match_dict_count;
    snprintf(path, sizeof(path), "%s\\matched_hashes.json", tools_dir);
    match_load_prior_matches(path);
    fprintf(stderr, "  Prior matched hashes: %zu new (%d names, %.2fs)\n",
            match_dict_count - before, match_prior_match_count, get_time() - t0);

    before = match_dict_count;
    for (int i = 0; match_game_names[i]; i++)
        match_dict_add_source_text(match_game_names[i]);
    fprintf(stderr, "  Game-specific names: %zu new\n", match_dict_count - before);

    t0 = get_time();
    before = match_dict_count;
    snprintf(path, sizeof(path), "%s\\all_names.txt", tools_dir);
    match_load_archive_names(path);
    fprintf(stderr, "  Archive names: %zu new (%d in file, %.2fs)\n",
            match_dict_count - before, match_archive_count, get_time() - t0);

    fprintf(stderr, "\n  Pre-variant dictionary: %zu\n", match_dict_count);

    /* Variant generation */
    t0 = get_time();
    size_t base_end = match_dict_count;
    before = match_dict_count;
    match_generate_variants(0, base_end);
    fprintf(stderr, "  Variants added: %zu (%.2fs)\n", match_dict_count - before, get_time() - t0);

    size_t compound_start = match_dict_count;
    match_generate_compounds();
    size_t compound_end = match_dict_count;
    fprintf(stderr, "  Compounds added: %zu\n", compound_end - compound_start);

    t0 = get_time();
    before = match_dict_count;
    match_generate_variants(compound_start, compound_end);
    fprintf(stderr, "  Compound variants: %zu (%.2fs)\n", match_dict_count - before, get_time() - t0);

    t0 = get_time();
    before = match_dict_count;
    snprintf(path, sizeof(path), "%s\\decompiled_script_names.txt", tools_dir);
    if (match_path_exists(path)) {
        match_load_line_name_file(path, &match_decompiled_script_count);
        fprintf(stderr, "  Decompiled script names: %zu new (%d entries, %.2fs)\n",
                match_dict_count - before, match_decompiled_script_count, get_time() - t0);
    } else {
        walk_directory(builds, match_on_script_file, NULL);
        fprintf(stderr, "  Script identifiers: %zu new (%d files, %d strings, %.2fs)\n",
                match_dict_count - before, match_script_file_count, match_script_string_count,
                get_time() - t0);
    }

    t0 = get_time();
    before = match_dict_count;
    snprintf(path, sizeof(path), "%s\\seeded_match_names.txt", tools_dir);
    if (!match_path_exists(path))
        snprintf(path, sizeof(path), "%s\\seeded_candidate_names.txt", tools_dir);
    if (match_path_exists(path)) {
        match_load_line_name_file(path, &match_seeded_name_count);
        fprintf(stderr, "  Seeded candidate names: %zu new (%d entries, %.2fs)\n",
                match_dict_count - before, match_seeded_name_count, get_time() - t0);
    }

    t0 = get_time();
    before = match_dict_count;
    snprintf(path, sizeof(path), "%s\\thaw_grammar_match_names.txt", tools_dir);
    if (match_path_exists(path)) {
        match_load_line_name_file(path, &match_grammar_name_count);
        fprintf(stderr, "  THAW grammar candidate names: %zu new (%d entries, %.2fs)\n",
                match_dict_count - before, match_grammar_name_count, get_time() - t0);
    }

    fprintf(stderr, "\n  Full dictionary: %zu\n", match_dict_count);
    fprintf(stderr, "  Arena used: %.1f MB\n\n", g_arena_used / (1024.0 * 1024.0));

    /* Match */
    t0 = get_time();
    match_do_matching();
    fprintf(stderr, "Matching: %.2fs\n", get_time() - t0);
    fprintf(stderr, "  QBKey matches: %d\n", match_qb_matched);
    fprintf(stderr, "  Crc32Neversoft (HED) matches: %d\n\n", match_hed_matched);

    /* Show samples */
    fprintf(stderr, "QBKey matches (first 20):\n");
    int shown = 0;
    for (int i = 0; i < match_qb_targets.count && shown < 20; i++) {
        if (match_qb_targets.names[i]) {
            fprintf(stderr, "  0x%08X -> %s\n", match_qb_targets.hashes[i], match_qb_targets.names[i]);
            shown++;
        }
    }

    /* Write outputs */
    fprintf(stderr, "\nWriting output files...\n");
    match_write_output(tools_dir);

    /* Summary */
    int total_targets = match_qb_targets.count + match_hed_targets.count;
    int total_matched = match_qb_matched + match_hed_matched;
    double total_time = get_time() - t_start;

    fprintf(stderr, "\n============================================================\n");
    fprintf(stderr, "SUMMARY\n");
    fprintf(stderr, "============================================================\n");
    fprintf(stderr, "Total targets: %d\n", total_targets);
    fprintf(stderr, "Total matched: %d (%.1f%%)\n", total_matched,
            100.0 * total_matched / total_targets);
    fprintf(stderr, "  QBKey: %d/%d (%.1f%%)\n", match_qb_matched, match_qb_targets.count,
            100.0 * match_qb_matched / match_qb_targets.count);
    fprintf(stderr, "  HED:   %d/%d (%.1f%%)\n", match_hed_matched, match_hed_targets.count,
            match_hed_targets.count ? 100.0 * match_hed_matched / match_hed_targets.count : 0.0);
    fprintf(stderr, "Remaining: %d\n", total_targets - total_matched);
    fprintf(stderr, "Total time: %.2fs\n", total_time);

    free(g_arena);
    g_arena = NULL;
    strset_free(&match_strset);
    free(match_dict);
    match_dict = NULL;

    return 0;
}
