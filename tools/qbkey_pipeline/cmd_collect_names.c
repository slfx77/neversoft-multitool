/*
 * cmd_collect_names.c — Collect plaintext asset names from all Neversoft game
 *                       archives and executables.
 *
 * Parses BON, DDX, DDM, PRE, PKR archives and extracts strings from game
 * binaries to build a comprehensive dictionary for QBKey hash matching.
 *
 * Outputs:
 *   all_names.txt        - one name per line, sorted, deduplicated, lowercase
 *   all_names_stats.json - per-source counts
 *
 * Usage: qbkey_pipeline collect-names [builds_path]
 */

/* ── Name collection set ─────────────────────────────────────────────── */

#define CN_MAX_NAMES (256 * 1024)
static const char **cn_names = NULL;
static int cn_name_count = 0;
static strset_t cn_name_set;

static void cn_add_name(const char *name)
{
    if (!name || !*name) return;
    size_t len = strlen(name);
    if (len < 2 || len >= MAX_NAME_LEN) return;
    if (cn_name_count >= CN_MAX_NAMES) return;

    /* Lowercase */
    char buf[MAX_NAME_LEN];
    for (size_t i = 0; i < len; i++)
        buf[i] = (char)tolower((unsigned char)name[i]);
    buf[len] = '\0';

    if (strset_contains(&cn_name_set, buf))
        return;
    const char *stored = arena_strdup(buf);
    strset_add(&cn_name_set, stored);
    cn_names[cn_name_count++] = stored;
}

/* Also add stem (filename without extension) */
static void cn_add_name_with_stem(const char *name)
{
    cn_add_name(name);
    const char *dot = strrchr(name, '.');
    if (dot && dot > name) {
        char stem[MAX_NAME_LEN];
        size_t slen = (size_t)(dot - name);
        if (slen >= 2 && slen < MAX_NAME_LEN) {
            memcpy(stem, name, slen);
            stem[slen] = '\0';
            cn_add_name(stem);
        }
    }
}

/* Extract meaningful name components from a dev build path */
static void cn_extract_path_components(const char *path)
{
    char norm[MAX_NAME_LEN * 2];
    size_t plen = strlen(path);
    if (plen >= sizeof(norm)) return;
    memcpy(norm, path, plen + 1);

    /* Normalize separators */
    for (size_t i = 0; i < plen; i++)
        if (norm[i] == '\\') norm[i] = '/';

    char *token = strtok(norm, "/");
    while (token) {
        size_t tlen = strlen(token);
        if (tlen >= 2 && token[tlen - 1] != ':') {
            cn_add_name_with_stem(token);
        }
        token = strtok(NULL, "/");
    }
}

/* ── Inline little-endian readers ────────────────────────────────────── */

static uint32_t cn_u32(const uint8_t *d, size_t o) {
    return (uint32_t)d[o] | ((uint32_t)d[o+1]<<8) | ((uint32_t)d[o+2]<<16) | ((uint32_t)d[o+3]<<24);
}

static uint16_t cn_u16(const uint8_t *d, size_t o) {
    return (uint16_t)d[o] | ((uint16_t)d[o+1]<<8);
}

/* ── Source 1: BON archive parser ────────────────────────────────────── */

static int cn_parse_bon(const char *filepath)
{
    size_t data_len;
    uint8_t *data = read_file_bin(filepath, &data_len);
    if (!data) return 0;

    int count = 0;
    if (data_len < 12 || memcmp(data, "Bon\0", 4) != 0) {
        free(data);
        return 0;
    }

    uint32_t version = cn_u32(data, 4);
    if (version != 1 && version != 3 && version != 4) {
        free(data);
        return 0;
    }

    size_t pos = 8;
    uint32_t texture_count;
    if (version == 3) {
        if (pos + 2 > data_len) { free(data); return 0; }
        texture_count = cn_u16(data, pos);
        pos += 2;
    } else {
        if (pos + 4 > data_len) { free(data); return 0; }
        texture_count = cn_u32(data, pos);
        pos += 4;
    }

    for (uint32_t t = 0; t < texture_count; t++) {
        if (version == 1) {
            /* DC v1: uint8 nameLen + display name */
            if (pos >= data_len) break;
            uint8_t name_len = data[pos]; pos++;
            if (pos + name_len > data_len) break;

            char namebuf[MAX_NAME_LEN];
            size_t nl = name_len < MAX_NAME_LEN - 1 ? name_len : MAX_NAME_LEN - 1;
            memcpy(namebuf, data + pos, nl);
            namebuf[nl] = '\0';
            cn_add_name(namebuf);
            count++;
            pos += name_len;

            /* 7 material floats (28 bytes) */
            pos += 28;

            /* 2 flag bytes */
            if (pos + 2 > data_len) break;
            pos++; /* flag1 */
            uint8_t has_texture = data[pos]; pos++;

            if (has_texture == 0) continue;

            /* Dev build path: uint8 pathLen + path */
            if (pos >= data_len) break;
            uint8_t path_len = data[pos]; pos++;
            if (pos + path_len > data_len) break;

            char pathbuf[MAX_NAME_LEN * 2];
            size_t pl = (size_t)path_len < sizeof(pathbuf) - 1 ? (size_t)path_len : sizeof(pathbuf) - 1;
            memcpy(pathbuf, data + pos, pl);
            pathbuf[pl] = '\0';
            cn_extract_path_components(pathbuf);
            count++;
            pos += path_len;

            /* 3 flag bytes + uint32 pvrSize */
            if (pos + 7 > data_len) break;
            pos += 3;
            uint32_t pvr_size = cn_u32(data, pos);
            pos += 4;
            pos += pvr_size;

        } else {
            /* Xbox v3/v4: uint16 displayNameLen + displayName */
            if (pos + 2 > data_len) break;
            uint16_t display_len = cn_u16(data, pos); pos += 2;
            if (pos + display_len > data_len) break;

            char namebuf[MAX_NAME_LEN];
            size_t nl = display_len < MAX_NAME_LEN - 1 ? display_len : MAX_NAME_LEN - 1;
            memcpy(namebuf, data + pos, nl);
            namebuf[nl] = '\0';
            cn_add_name(namebuf);
            count++;
            pos += display_len;

            /* Material: RGBA(4) + specular(4) + glossiness(4) + flag(1) = 13 bytes */
            pos += 13;

            /* Internal name: uint16 internalNameLen + internalName */
            if (pos + 2 > data_len) break;
            uint16_t internal_len = cn_u16(data, pos); pos += 2;
            if (pos + internal_len > data_len) break;

            nl = internal_len < MAX_NAME_LEN - 1 ? internal_len : MAX_NAME_LEN - 1;
            memcpy(namebuf, data + pos, nl);
            namebuf[nl] = '\0';
            cn_add_name(namebuf);
            count++;
            pos += internal_len;

            /* 3 flag bytes + uint32 ddsSize */
            if (pos + 7 > data_len) break;
            pos += 3;
            uint32_t dds_size = cn_u32(data, pos);
            pos += 4;
            pos += dds_size;
        }
    }

    free(data);
    return count;
}

/* ── Source 2: DDX archive parser ────────────────────────────────────── */

static int cn_parse_ddx(const char *filepath)
{
    size_t data_len;
    uint8_t *data = read_file_bin(filepath, &data_len);
    if (!data) return 0;

    int count = 0;
    if (data_len < 16) { free(data); return 0; }

    uint32_t entry_count = cn_u32(data, 12);
    size_t pos = 16;
    const size_t entry_size = 264; /* offset(4) + size(4) + name(256) */

    for (uint32_t i = 0; i < entry_count; i++) {
        if (pos + entry_size > data_len) break;

        /* Name is at pos+8, 256 bytes, null-terminated */
        char namebuf[257];
        memcpy(namebuf, data + pos + 8, 256);
        namebuf[256] = '\0';

        /* Find actual end of string */
        size_t nl = strlen(namebuf);
        if (nl > 0) {
            cn_add_name_with_stem(namebuf);
            count++;
        }
        pos += entry_size;
    }

    free(data);
    return count;
}

/* ── Source 3: DDM mesh parser ───────────────────────────────────────── */

static int cn_parse_ddm(const char *filepath)
{
    size_t data_len;
    uint8_t *data = read_file_bin(filepath, &data_len);
    if (!data) return 0;

    int count = 0;
    if (data_len < 12) { free(data); return 0; }

    uint32_t version = cn_u32(data, 0);
    if (version != 1) { free(data); return 0; }

    /* uint32_t data_size = cn_u32(data, 4); */
    uint32_t object_count = cn_u32(data, 8);

    /* Object table: (offset, size) per entry */
    size_t pos = 12;
    typedef struct { uint32_t offset; uint32_t size; } obj_entry_t;
    obj_entry_t *obj_table = (obj_entry_t *)malloc(object_count * sizeof(obj_entry_t));

    for (uint32_t i = 0; i < object_count; i++) {
        if (pos + 8 > data_len) { object_count = i; break; }
        obj_table[i].offset = cn_u32(data, pos);
        obj_table[i].size = cn_u32(data, pos + 4);
        pos += 8;
    }

    for (uint32_t oi = 0; oi < object_count; oi++) {
        size_t obj_offset = obj_table[oi].offset;
        if (obj_offset + 136 > data_len) continue;

        /* Skip 28 bytes (index + checksum + anim params + flags) */
        size_t obj_pos = obj_offset + 28;

        /* Object name: 64 bytes */
        char namebuf[65];
        memcpy(namebuf, data + obj_pos, 64);
        namebuf[64] = '\0';
        if (namebuf[0]) {
            cn_add_name(namebuf);
            count++;
        }
        obj_pos += 64; /* past name */
        obj_pos += 28; /* past bounding volume (7 floats) */

        if (obj_pos + 16 > data_len) continue;
        uint32_t mat_count = cn_u32(data, obj_pos);
        /* uint32_t vert_count = cn_u32(data, obj_pos + 4); */
        /* uint32_t idx_count = cn_u32(data, obj_pos + 8); */
        /* uint32_t split_count = cn_u32(data, obj_pos + 12); */
        obj_pos += 16;

        /* Materials: 152 bytes each */
        for (uint32_t mi = 0; mi < mat_count; mi++) {
            if (obj_pos + 152 > data_len) break;

            /* Material name: 64 bytes */
            memcpy(namebuf, data + obj_pos, 64);
            namebuf[64] = '\0';
            if (namebuf[0]) {
                cn_add_name(namebuf);
                count++;
            }

            /* Texture name: 64 bytes */
            char texbuf[65];
            memcpy(texbuf, data + obj_pos + 64, 64);
            texbuf[64] = '\0';
            if (texbuf[0]) {
                cn_add_name_with_stem(texbuf);
                count++;
            }

            obj_pos += 152;
        }
    }

    free(obj_table);
    free(data);
    return count;
}

/* ── Source 4: PRE archive parser ────────────────────────────────────── */

static int cn_parse_pre(const char *filepath)
{
    size_t data_len;
    uint8_t *data = read_file_bin(filepath, &data_len);
    if (!data) return 0;

    int count = 0;
    if (data_len < 4) { free(data); return 0; }

    uint32_t entry_count = cn_u32(data, 0);
    if (entry_count > 10000) { free(data); return 0; }

    size_t pos = 4;
    for (uint32_t i = 0; i < entry_count; i++) {
        /* Align to 4 */
        size_t rem = pos % 4;
        if (rem != 0) pos += 4 - rem;

        /* Read null-terminated string */
        size_t end = pos;
        while (end < data_len && data[end] != 0) end++;
        if (end >= data_len) break;

        size_t name_len = end - pos;
        pos = end + 1;

        /* Align to 4 */
        rem = pos % 4;
        if (rem != 0) pos += 4 - rem;

        /* uint32 dataSize */
        if (pos + 4 > data_len) break;
        uint32_t data_size = cn_u32(data, pos);
        pos += 4;
        pos += data_size;

        if (name_len >= 2 && name_len < MAX_NAME_LEN) {
            char namebuf[MAX_NAME_LEN];
            memcpy(namebuf, data + end - name_len, name_len);
            namebuf[name_len] = '\0';
            cn_add_name_with_stem(namebuf);
            count++;
        }
    }

    free(data);
    return count;
}

/* ── Source 5: PKR3 archive parser ───────────────────────────────────── */

static int cn_parse_pkr(const char *filepath)
{
    size_t data_len;
    uint8_t *data = read_file_bin(filepath, &data_len);
    if (!data) return 0;

    int count = 0;
    if (data_len < 8 || memcmp(data, "PKR3", 4) != 0) {
        free(data);
        return 0;
    }

    uint32_t dir_offset = cn_u32(data, 4);
    if (dir_offset + 12 > data_len) { free(data); return 0; }

    /* Directory header: unk(4) + numDirs(4) + numFiles(4) */
    /* uint32_t unk = cn_u32(data, dir_offset); */
    uint32_t num_dirs = cn_u32(data, dir_offset + 4);
    /* uint32_t num_files = cn_u32(data, dir_offset + 8); */
    size_t pos = dir_offset + 12;

    /* Directory entries: 32-byte name + unk(4) + numFiles(4) = 40 bytes */
    typedef struct { char name[33]; uint32_t num_files; } pkr_dir_t;
    pkr_dir_t *dirs = (pkr_dir_t *)malloc(num_dirs * sizeof(pkr_dir_t));

    for (uint32_t i = 0; i < num_dirs; i++) {
        if (pos + 40 > data_len) { num_dirs = i; break; }
        memcpy(dirs[i].name, data + pos, 32);
        dirs[i].name[32] = '\0';
        if (dirs[i].name[0]) {
            cn_add_name(dirs[i].name);
            count++;
        }
        dirs[i].num_files = cn_u32(data, pos + 36);
        pos += 40;
    }

    /* File entries: 32-byte name + crc(4) + compressed(4) + offset(4) + uncompSize(4) + compSize(4) = 52 bytes */
    for (uint32_t d = 0; d < num_dirs; d++) {
        for (uint32_t fi = 0; fi < dirs[d].num_files; fi++) {
            if (pos + 52 > data_len) goto pkr_done;
            char file_name[33];
            memcpy(file_name, data + pos, 32);
            file_name[32] = '\0';
            if (file_name[0]) {
                cn_add_name_with_stem(file_name);
                /* Also add dir/file combo */
                if (dirs[d].name[0]) {
                    char combo[MAX_NAME_LEN];
                    snprintf(combo, sizeof(combo), "%s/%s", dirs[d].name, file_name);
                    cn_add_name(combo);
                }
                count++;
            }
            pos += 52;
        }
    }

pkr_done:
    free(dirs);
    free(data);
    return count;
}

/* ── Source 6: Game executable string extraction ─────────────────────── */

static const char *cn_exclude_strings[] = {
    "int", "void", "char", "long", "short", "unsigned", "signed", "const",
    "static", "return", "while", "break", "case", "else", "enum", "struct",
    "class", "this", "null", "true", "false", "bool", "for", "switch",
    "default", "typedef", "extern", "volatile", "register", "sizeof",
    "union", "goto", "continue", "inline", "float", "double", "string",
    "printf", "fprintf", "sprintf", "malloc", "calloc", "realloc", "free",
    "memcpy", "memset", "strlen", "strcpy", "strcat", "strcmp", "strncmp",
    "fopen", "fclose", "fread", "fwrite", "fseek", "ftell", "fgets",
    "abort", "exit", "assert", "include", "define", "ifdef", "ifndef",
    "endif", "pragma", "error", "warning", "main", "argc", "argv",
    "stdin", "stdout", "stderr", "eof", "nullptr",
    "virtual", "override", "public", "private", "protected", "friend",
    "template", "typename", "namespace", "using", "try", "catch", "throw",
    "new", "delete", "operator", "explicit", "mutable", "constexpr",
    "alignof", "decltype", "noexcept", "static_assert", "thread_local",
    NULL
};

static strset_t cn_exclude_set;
static int cn_exclude_init_done = 0;

static void cn_init_exclude_set(void)
{
    if (cn_exclude_init_done) return;
    strset_init_sized(&cn_exclude_set, 10);
    for (int i = 0; cn_exclude_strings[i]; i++)
        strset_add(&cn_exclude_set, cn_exclude_strings[i]);
    cn_exclude_init_done = 1;
}

/* Check exclude patterns (replaces regex in Python version) */
static int cn_matches_exclude_pattern(const char *s)
{
    /* __compiler_builtins */
    if (s[0] == '_' && s[1] == '_')
        return 1;

    /* _Reserved identifiers */
    if (s[0] == '_' && s[1] >= 'A' && s[1] <= 'Z')
        return 1;

    /* MACRO_NAMES (all uppercase with underscore) */
    int all_upper = 1, has_underscore = 0;
    for (int i = 0; s[i]; i++) {
        if (s[i] == '_') has_underscore = 1;
        else if (!(s[i] >= 'A' && s[i] <= 'Z')) { all_upper = 0; break; }
    }
    if (all_upper && has_underscore && strlen(s) >= 5)
        return 1;

    /* DLL/system file extensions */
    size_t len = strlen(s);
    if (len >= 4) {
        const char *ext = s + len - 4;
        if (_stricmp(ext, ".dll") == 0 || _stricmp(ext, ".lib") == 0 ||
            _stricmp(ext, ".obj") == 0 || _stricmp(ext, ".pdb") == 0 ||
            _stricmp(ext, ".sys") == 0 || _stricmp(ext, ".drv") == 0 ||
            _stricmp(ext, ".ocx") == 0)
            return 1;
    }

    /* Windows/system prefixes */
    if (_strnicmp(s, "Microsoft", 9) == 0 || _strnicmp(s, "Windows", 7) == 0 ||
        _strnicmp(s, "KERNEL", 6) == 0 || _strnicmp(s, "USER", 4) == 0 ||
        _strnicmp(s, "GDI", 3) == 0 || _strnicmp(s, "ADVAPI", 6) == 0 ||
        strncmp(s, "HKEY", 4) == 0 || strncmp(s, "REG_", 4) == 0 ||
        strncmp(s, "CLSID", 5) == 0 || strncmp(s, "IID_", 4) == 0)
        return 1;

    /* UNC paths */
    if (s[0] == '\\' && s[1] == '\\')
        return 1;

    /* Windows drive paths */
    if (len >= 3 && s[0] >= 'A' && s[0] <= 'Z' && s[1] == ':' && s[2] == '\\')
        return 1;

    return 0;
}

static int cn_is_plausible_exe_name(const char *s)
{
    size_t len = strlen(s);

    /* Must start with letter, rest is alnum + underscore + dot + dash + slash */
    if (!((s[0] >= 'a' && s[0] <= 'z') || (s[0] >= 'A' && s[0] <= 'Z')))
        return 0;
    for (size_t i = 1; i < len; i++) {
        char c = s[i];
        if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
              (c >= '0' && c <= '9') || c == '_' || c == '.' || c == '-' || c == '/'))
            return 0;
    }

    if (len > 40) return 0;

    /* Check lowercase version against exclude set */
    char lower[MAX_NAME_LEN];
    for (size_t i = 0; i < len && i < MAX_NAME_LEN - 1; i++)
        lower[i] = (char)tolower((unsigned char)s[i]);
    lower[len < MAX_NAME_LEN - 1 ? len : MAX_NAME_LEN - 1] = '\0';

    if (strset_contains(&cn_exclude_set, lower))
        return 0;

    if (cn_matches_exclude_pattern(s))
        return 0;

    return 1;
}

static int cn_extract_exe_strings(const char *filepath)
{
    size_t data_len;
    uint8_t *data = read_file_bin(filepath, &data_len);
    if (!data) return 0;

    cn_init_exclude_set();

    int count = 0;
    char current[MAX_NAME_LEN];
    int cur_len = 0;

    for (size_t i = 0; i < data_len; i++) {
        uint8_t b = data[i];
        if (b >= 0x20 && b <= 0x7E) {
            if (cur_len < MAX_NAME_LEN - 1)
                current[cur_len++] = (char)b;
        } else {
            if (cur_len >= 3 && cur_len <= 64) {
                current[cur_len] = '\0';
                if (cn_is_plausible_exe_name(current)) {
                    cn_add_name_with_stem(current);
                    count++;
                }
            }
            cur_len = 0;
        }
    }
    /* Trailing string */
    if (cur_len >= 3 && cur_len <= 64) {
        current[cur_len] = '\0';
        if (cn_is_plausible_exe_name(current)) {
            cn_add_name_with_stem(current);
            count++;
        }
    }

    free(data);
    return count;
}

/* ── Source 7: SYM-guided binary extraction ──────────────────────────── */

static int cn_extract_sym_guided(const char *sym_path, const char *binary_path)
{
    size_t sym_len;
    uint8_t *sym_data = read_file_bin(sym_path, &sym_len);
    if (!sym_data) return 0;

    cn_init_exclude_set();

    int count = 0;

    if (sym_len < 8 || memcmp(sym_data, "MND", 3) != 0) {
        fprintf(stderr, "  WARNING: Not a valid SYM file (missing MND magic)\n");
        free(sym_data);
        return 0;
    }

    /* Parse symbols: extract symbol names as candidates */
    size_t pos = 8;
    /* First pass: collect symbol names */
    size_t save_pos = pos;
    while (pos + 6 <= sym_len) {
        /* uint32_t addr = cn_u32(sym_data, pos); */
        /* uint8_t sym_type = sym_data[pos + 4]; */
        uint8_t name_len = sym_data[pos + 5];
        pos += 6;
        if (pos + name_len > sym_len) break;

        if (name_len >= 3 && name_len <= 30) {
            char name[MAX_NAME_LEN];
            memcpy(name, sym_data + pos, name_len);
            name[name_len] = '\0';

            /* Check if valid identifier */
            char c0 = name[0];
            if ((c0 >= 'a' && c0 <= 'z') || (c0 >= 'A' && c0 <= 'Z')) {
                int valid = 1;
                for (int j = 1; j < name_len; j++) {
                    char c = name[j];
                    if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                          (c >= '0' && c <= '9') || c == '_')) {
                        valid = 0; break;
                    }
                }
                if (valid) {
                    char lower[MAX_NAME_LEN];
                    for (int j = 0; j < name_len; j++)
                        lower[j] = (char)tolower((unsigned char)name[j]);
                    lower[name_len] = '\0';
                    if (!strset_contains(&cn_exclude_set, lower)) {
                        cn_add_name(name);
                        count++;
                    }
                }
            }
        }
        pos += name_len;
    }

    /* Read binary file */
    size_t bin_len;
    uint8_t *bin_data = read_file_bin(binary_path, &bin_len);
    if (!bin_data || bin_len < 0x800) {
        free(sym_data);
        if (bin_data) free(bin_data);
        return count;
    }

    uint32_t load_addr = cn_u32(bin_data, 0x18);
    size_t data_start = 0x800;

    /* Interesting keywords for data symbol filtering */
    static const char *interesting[] = {
        "name", "tex", "mesh", "string", "table", "data", "list",
        "anim", "font", "level", "skater", "trick", "file",
        "path", "board", "char", "model", "skin", "part", NULL
    };

    /* Second pass: find interesting data addresses */
    pos = save_pos;
    hashset_t data_addrs;
    hashset_init_sized(&data_addrs, 14);

    while (pos + 6 <= sym_len) {
        uint32_t addr = cn_u32(sym_data, pos);
        uint8_t sym_type = sym_data[pos + 4];
        uint8_t name_len = sym_data[pos + 5];
        pos += 6;
        if (pos + name_len > sym_len) break;

        char name[MAX_NAME_LEN];
        size_t nl = name_len < MAX_NAME_LEN - 1 ? name_len : MAX_NAME_LEN - 1;
        memcpy(name, sym_data + pos, nl);
        name[nl] = '\0';
        pos += name_len;

        /* Check if data symbol or has interesting keyword */
        int is_data = (sym_type == 'D' || sym_type == 'd' || sym_type == 'B' || sym_type == 'b');
        if (!is_data) {
            char lower_name[MAX_NAME_LEN];
            for (size_t j = 0; j < nl; j++)
                lower_name[j] = (char)tolower((unsigned char)name[j]);
            lower_name[nl] = '\0';

            for (int k = 0; interesting[k]; k++) {
                if (strstr(lower_name, interesting[k])) {
                    is_data = 1;
                    break;
                }
            }
        }
        if (is_data)
            hashset_add(&data_addrs, addr);
    }

    /* Collect sorted addresses */
    uint32_t *addrs = (uint32_t *)malloc(data_addrs.count * sizeof(uint32_t));
    uint32_t addr_count = 0;
    for (uint32_t i = 0; i < data_addrs.capacity; i++) {
        if (data_addrs.occupied[i])
            addrs[addr_count++] = data_addrs.keys[i];
    }
    qsort(addrs, addr_count, sizeof(uint32_t), uint32_cmp);

    /* Extract strings from each data address region */
    for (uint32_t ai = 0; ai < addr_count; ai++) {
        int64_t file_offset = (int64_t)addrs[ai] - (int64_t)load_addr + (int64_t)data_start;
        if (file_offset < 0 || (size_t)file_offset >= bin_len) continue;

        size_t chunk_end = (size_t)file_offset + 4096;
        if (chunk_end > bin_len) chunk_end = bin_len;

        char current[MAX_NAME_LEN];
        int cur_len = 0;

        for (size_t bi = (size_t)file_offset; bi < chunk_end; bi++) {
            uint8_t b = bin_data[bi];
            if (b >= 0x20 && b <= 0x7E) {
                if (cur_len < MAX_NAME_LEN - 1)
                    current[cur_len++] = (char)b;
            } else {
                if (cur_len >= 3 && cur_len <= 64) {
                    current[cur_len] = '\0';
                    if (cn_is_plausible_exe_name(current)) {
                        cn_add_name_with_stem(current);
                        count++;
                    }
                }
                cur_len = 0;
            }
        }
    }

    free(addrs);
    hashset_free(&data_addrs);
    free(bin_data);
    free(sym_data);
    return count;
}

/* ── Directory walking helpers ───────────────────────────────────────── */

typedef struct {
    const char *ext;
    int (*parser)(const char *filepath);
    int total_names;
    int files_found;
} cn_file_scanner_t;

static void cn_on_ext_file(const char *filepath, const char *filename, void *ctx)
{
    cn_file_scanner_t *scanner = (cn_file_scanner_t *)ctx;
    size_t len = strlen(filename);
    size_t ext_len = strlen(scanner->ext);
    if (len < ext_len) return;
    if (_stricmp(filename + len - ext_len, scanner->ext) != 0) return;

    scanner->files_found++;
    int before = cn_name_count;
    scanner->parser(filepath);
    scanner->total_names += cn_name_count - before;
}

/* ── Executable list ─────────────────────────────────────────────────── */

static const char *cn_executables[] = {
    "Apocalypse (1998-11-17, PSX - Final)\\Data\\SLUS_003.73",
    "Spider-Man (2000-2-4, PSX - Prototype)\\Data\\HARNESS.EXE",
    "Spider-Man (2000-2-18, PSX - Prototype)\\Data\\PSX.EXE",
    "Spider-Man (2000-9-1, PSX - Final)\\Data\\SLUS_008.75",
    "Spider-Man (2001-2-14, DC - Prototype)\\Data\\1ST_READ.BIN",
    "Spider-Man (2001-9-17, PC - Final)\\Data\\Setup\\SpideyPC.exe",
    "Spider-Man 2 - Enter Electro (2001-8-14 - Prototype)\\SLUS_013.78",
    "Spider-Man 2 - Enter Electro (Rev1)\\Data\\SLUS_013.78",
    "Tony Hawk's Pro Skater (1999-4-4, PSX - Prototype)\\Data\\PSX.EXE",
    "Tony Hawk's Pro Skater (1999-9-29, PSX - Final)\\Data\\SLUS_008.60",
    "Tony Hawk's Pro Skater 2 (2000-3-29, PSX - Prototype)\\SLUS_900.86",
    "Tony Hawk's Pro Skater 2 (2000-9-19, PSX - Final)\\SLUS_010.66",
    "Tony Hawk's Pro Skater 2 (2000-11-15, DC - Final)\\1ST_READ.BIN",
    "Tony Hawk's Pro Skater 2X (2001-11-15, Xbox - Final)\\default.xbe",
    NULL
};

/* ── Main entry ──────────────────────────────────────────────────────── */

static int cmd_collect_names(int argc, char **argv)
{
    const char *builds = BUILDS_DEFAULT;
    char tools_dir[MAX_PATH];
    get_tools_dir(tools_dir, sizeof(tools_dir));

    if (argc > 1) builds = argv[1];

    fprintf(stderr, "Collecting plaintext asset names from all sources\n");
    fprintf(stderr, "============================================================\n");

    arena_init();
    strset_init(&cn_name_set);
    cn_names = (const char **)malloc(CN_MAX_NAMES * sizeof(const char *));
    cn_name_count = 0;

    int stats_bon = 0, stats_ddx = 0, stats_ddm = 0, stats_pre = 0;
    int stats_pkr = 0, stats_exe = 0, stats_sym = 0;

    /* Source 1: BON archives */
    fprintf(stderr, "\n[1/7] Parsing BON archives...\n");
    int before = cn_name_count;
    cn_file_scanner_t bon_scanner = {".bon", cn_parse_bon, 0, 0};
    walk_directory(builds, cn_on_ext_file, &bon_scanner);
    stats_bon = cn_name_count - before;
    fprintf(stderr, "  Found %d BON files, extracted %d unique names\n",
            bon_scanner.files_found, stats_bon);

    /* Source 2: DDX archives */
    fprintf(stderr, "\n[2/7] Parsing DDX archives...\n");
    before = cn_name_count;
    cn_file_scanner_t ddx_scanner = {".ddx", cn_parse_ddx, 0, 0};
    walk_directory(builds, cn_on_ext_file, &ddx_scanner);
    stats_ddx = cn_name_count - before;
    fprintf(stderr, "  Found %d DDX files, extracted %d unique names\n",
            ddx_scanner.files_found, stats_ddx);

    /* Source 3: DDM meshes */
    fprintf(stderr, "\n[3/7] Parsing DDM mesh files...\n");
    before = cn_name_count;
    cn_file_scanner_t ddm_scanner = {".ddm", cn_parse_ddm, 0, 0};
    walk_directory(builds, cn_on_ext_file, &ddm_scanner);
    stats_ddm = cn_name_count - before;
    fprintf(stderr, "  Found %d DDM files, extracted %d unique names\n",
            ddm_scanner.files_found, stats_ddm);

    /* Source 4: PRE archives */
    fprintf(stderr, "\n[4/7] Parsing PRE archives...\n");
    before = cn_name_count;
    cn_file_scanner_t pre_scanner = {".pre", cn_parse_pre, 0, 0};
    walk_directory(builds, cn_on_ext_file, &pre_scanner);
    stats_pre = cn_name_count - before;
    fprintf(stderr, "  Found %d PRE files, extracted %d unique names\n",
            pre_scanner.files_found, stats_pre);

    /* Source 5: PKR archives */
    fprintf(stderr, "\n[5/7] Parsing PKR archives...\n");
    before = cn_name_count;
    {
        char pkr_path[MAX_PATH * 2];
        snprintf(pkr_path, sizeof(pkr_path),
                 "%s\\Spider-Man (2001-9-17, PC - Final)\\Data\\Setup\\data.pkr", builds);
        int names = cn_parse_pkr(pkr_path);
        if (names > 0)
            fprintf(stderr, "  data.pkr: %d names\n", names);

        /* Also try Spidey.dbd */
        snprintf(pkr_path, sizeof(pkr_path),
                 "%s\\Spider-Man (2001-9-17, PC - Final)\\Data\\Bin\\Spidey.dbd", builds);
        names = cn_parse_pkr(pkr_path);
        if (names > 0)
            fprintf(stderr, "  Spidey.dbd (PKR format): %d names\n", names);
    }
    stats_pkr = cn_name_count - before;
    fprintf(stderr, "  Extracted %d unique names from PKR files\n", stats_pkr);

    /* Source 6: Game executables */
    fprintf(stderr, "\n[6/7] Extracting strings from game executables...\n");
    before = cn_name_count;
    for (int i = 0; cn_executables[i]; i++) {
        char full_path[MAX_PATH * 2];
        snprintf(full_path, sizeof(full_path), "%s\\%s", builds, cn_executables[i]);

        int exe_before = cn_name_count;
        cn_extract_exe_strings(full_path);
        int exe_names = cn_name_count - exe_before;
        if (exe_names > 0)
            fprintf(stderr, "  %s: %d names\n", path_basename(cn_executables[i]), exe_names);
        else
            fprintf(stderr, "  WARNING: Not found or no names: %s\n", path_basename(cn_executables[i]));
    }
    stats_exe = cn_name_count - before;
    fprintf(stderr, "  Extracted %d unique names from executables\n", stats_exe);

    /* Source 7: SYM-guided */
    fprintf(stderr, "\n[7/7] SYM-guided binary extraction...\n");
    before = cn_name_count;
    {
        char sym_path[MAX_PATH * 2], bin_path[MAX_PATH * 2];
        snprintf(sym_path, sizeof(sym_path),
                 "%s\\Tony Hawk's Pro Skater 2 (2000-3-29, PSX - Prototype)\\MAIN.SYM", builds);
        snprintf(bin_path, sizeof(bin_path),
                 "%s\\Tony Hawk's Pro Skater 2 (2000-3-29, PSX - Prototype)\\SLUS_900.86", builds);

        fprintf(stderr, "  Parsing SYM file...\n");
        int names = cn_extract_sym_guided(sym_path, bin_path);
        fprintf(stderr, "    Extracted %d names from SYM + binary\n", names);
    }
    stats_sym = cn_name_count - before;

    /* Results */
    fprintf(stderr, "\n============================================================\n");
    fprintf(stderr, "RESULTS\n");
    fprintf(stderr, "============================================================\n");
    fprintf(stderr, "\nTotal unique names: %d\n", cn_name_count);
    fprintf(stderr, "  bon: %d\n", stats_bon);
    fprintf(stderr, "  ddx: %d\n", stats_ddx);
    fprintf(stderr, "  ddm: %d\n", stats_ddm);
    fprintf(stderr, "  pre: %d\n", stats_pre);
    fprintf(stderr, "  pkr: %d\n", stats_pkr);
    fprintf(stderr, "  executables: %d\n", stats_exe);
    fprintf(stderr, "  sym_guided: %d\n", stats_sym);

    /* Sort names */
    qsort(cn_names, cn_name_count, sizeof(const char *), str_cmp);

    /* Write all_names.txt */
    char out_path[MAX_PATH];
    snprintf(out_path, sizeof(out_path), "%s\\all_names.txt", tools_dir);
    FILE *f = fopen(out_path, "w");
    if (f) {
        for (int i = 0; i < cn_name_count; i++)
            fprintf(f, "%s\n", cn_names[i]);
        fclose(f);
        fprintf(stderr, "\nWritten to: %s\n", out_path);
    }

    /* Write stats */
    snprintf(out_path, sizeof(out_path), "%s\\all_names_stats.json", tools_dir);
    f = fopen(out_path, "w");
    if (f) {
        fprintf(f, "{\n");
        fprintf(f, "  \"bon\": %d,\n", stats_bon);
        fprintf(f, "  \"ddx\": %d,\n", stats_ddx);
        fprintf(f, "  \"ddm\": %d,\n", stats_ddm);
        fprintf(f, "  \"pre\": %d,\n", stats_pre);
        fprintf(f, "  \"pkr\": %d,\n", stats_pkr);
        fprintf(f, "  \"executables\": %d,\n", stats_exe);
        fprintf(f, "  \"sym_guided\": %d,\n", stats_sym);
        fprintf(f, "  \"total_unique\": %d\n", cn_name_count);
        fprintf(f, "}\n");
        fclose(f);
        fprintf(stderr, "Stats written to: %s\n", out_path);
    }

    strset_free(&cn_name_set);
    if (cn_exclude_init_done)
        strset_free(&cn_exclude_set);
    free(cn_names);
    free(g_arena);
    g_arena = NULL;

    return 0;
}
