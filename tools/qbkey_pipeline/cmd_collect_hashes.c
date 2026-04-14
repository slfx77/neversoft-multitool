/*
 * cmd_collect_hashes.c — Stage 1: Collect unique CRC-32 hashes from PSX
 *                        model files, THAW skin files, and THPS2 Final HED.
 *
 * PSX file structure (matches PsxLibrary.cs):
 *   1. Magic (4 bytes): 04 00 02 00, 03 00 02 00, or 06 00 02 00
 *   2. ptrMeta (uint32) + objCount (uint32)
 *   3. Objects: objCount x 36 bytes
 *   4. meshCount (uint32)
 *   5. Seek to ptrMeta: tagged chunks until 0xFFFFFFFF terminator
 *   6. Mesh name hashes: meshCount x uint32
 *   7. Texture count (uint32) + texture name hashes: texCount x uint32
 *
 * HED file (THPS2 Final, hashed format):
 *   12-byte entries: [uint32 hash, uint32 offset, uint32 size]
 *   Terminated by all-zero entry.
 *
 * THAW skin files:
 *   - .skin.wpc (material hash, material-name hash, pass texture hashes)
 *   - .skin.ps2 (entry material hash, entry texture hash)
 *
 * Usage: qbkey_pipeline collect-hashes [builds_path]
 */

/* ── Hash-to-file-list storage ───────────────────────────────────────── */

#define CH_MAX_FILES_PER_HASH 64
#define CH_HT_BITS  17
#define CH_HT_SIZE  (1u << CH_HT_BITS)
#define CH_HT_MASK  (CH_HT_SIZE - 1)

typedef struct {
    uint32_t hash;
    const char *files[CH_MAX_FILES_PER_HASH];
    int file_count;
    int occupied;
} ch_hash_entry_t;

static ch_hash_entry_t ch_mesh_ht[CH_HT_SIZE];
static ch_hash_entry_t ch_tex_ht[CH_HT_SIZE];
static ch_hash_entry_t ch_mat_ht[CH_HT_SIZE];
static ch_hash_entry_t ch_mat_name_ht[CH_HT_SIZE];
static ch_hash_entry_t ch_hed_ht[CH_HT_SIZE];

static int ch_mesh_unique = 0;
static int ch_tex_unique = 0;
static int ch_mat_unique = 0;
static int ch_mat_name_unique = 0;
static int ch_hed_unique = 0;

static ch_hash_entry_t *ch_ht_get(ch_hash_entry_t *ht, uint32_t hash, int *unique_count)
{
    uint32_t idx = hash & CH_HT_MASK;
    while (ht[idx].occupied) {
        if (ht[idx].hash == hash)
            return &ht[idx];
        idx = (idx + 1) & CH_HT_MASK;
    }
    ht[idx].occupied = 1;
    ht[idx].hash = hash;
    ht[idx].file_count = 0;
    if (unique_count) (*unique_count)++;
    return &ht[idx];
}

static void ch_add_hash(ch_hash_entry_t *ht, uint32_t hash, const char *rel_path, int *unique_count)
{
    ch_hash_entry_t *e = ch_ht_get(ht, hash, unique_count);
    if (e->file_count < CH_MAX_FILES_PER_HASH) {
        /* Avoid duplicate file entries */
        int dup = 0;
        for (int i = 0; i < e->file_count; i++) {
            if (strcmp(e->files[i], rel_path) == 0) { dup = 1; break; }
        }
        if (!dup)
            e->files[e->file_count++] = rel_path;
    }
}

/* ── PSX file parsing ────────────────────────────────────────────────── */

static const uint8_t ch_valid_magics[][4] = {
    {0x04, 0x00, 0x02, 0x00},
    {0x03, 0x00, 0x02, 0x00},
    {0x06, 0x00, 0x02, 0x00},
};

static uint32_t ch_read_u32(const uint8_t *data, size_t offset)
{
    return (uint32_t)data[offset]
         | ((uint32_t)data[offset + 1] << 8)
         | ((uint32_t)data[offset + 2] << 16)
         | ((uint32_t)data[offset + 3] << 24);
}

static uint16_t ch_read_u16(const uint8_t *data, size_t offset)
{
    return (uint16_t)data[offset]
         | ((uint16_t)data[offset + 1] << 8);
}

static int32_t ch_read_i32(const uint8_t *data, size_t offset)
{
    return (int32_t)ch_read_u32(data, offset);
}

static int ch_has_ci_suffix(const char *text, const char *suffix)
{
    size_t text_len = strlen(text);
    size_t suffix_len = strlen(suffix);
    if (text_len < suffix_len)
        return 0;
    return _stricmp(text + text_len - suffix_len, suffix) == 0;
}

static int ch_contains_ci(const char *text, const char *needle)
{
    size_t needle_len = strlen(needle);
    if (needle_len == 0)
        return 1;

    for (const char *p = text; *p; p++) {
        size_t i = 0;
        while (i < needle_len) {
            char a = p[i];
            char b = needle[i];
            if (!a)
                return 0;
            if (a >= 'A' && a <= 'Z') a = (char)(a + 32);
            if (b >= 'A' && b <= 'Z') b = (char)(b + 32);
            if (a != b)
                break;
            i++;
        }
        if (i == needle_len)
            return 1;
    }
    return 0;
}

static const char *ch_relative_path(const char *filepath, const char *builds)
{
    size_t builds_len = strlen(builds);
    if (strncmp(filepath, builds, builds_len) == 0 && filepath[builds_len] == '\\')
        return filepath + builds_len + 1;
    return filepath;
}

/* Returns 1 on success, 0 on failure */
static int ch_parse_psx(const uint8_t *data, size_t data_len, const char *rel_path)
{
    if (data_len < 12)
        return 0;

    /* Validate magic */
    int valid_magic = 0;
    for (int m = 0; m < 3; m++) {
        if (memcmp(data, ch_valid_magics[m], 4) == 0) {
            valid_magic = 1;
            break;
        }
    }
    if (!valid_magic)
        return 0;

    /* Read ptrMeta and objCount */
    uint32_t ptr_meta = ch_read_u32(data, 4);
    uint32_t obj_count = ch_read_u32(data, 8);

    /* Skip objects (36 bytes each) */
    size_t pos = 12 + (size_t)obj_count * 36;
    if (pos + 4 > data_len) return 0;

    /* Read meshCount */
    uint32_t mesh_count = ch_read_u32(data, pos);
    pos += 4;

    /* Seek to ptrMeta for tagged chunks */
    pos = ptr_meta;
    if (pos >= data_len) return 0;

    /* Skip tagged chunks until 0xFFFFFFFF */
    int chunk_count = 0;
    while (pos + 4 <= data_len) {
        uint32_t chunk_magic = ch_read_u32(data, pos);
        pos += 4;
        if (chunk_magic == 0xFFFFFFFF)
            break;
        if (pos + 4 > data_len) return 0;
        uint32_t chunk_len = ch_read_u32(data, pos);
        pos += 4;
        pos += chunk_len;
        chunk_count++;
        if (chunk_count > 16)
            return 0;
    }

    /* Read mesh name hashes */
    if (pos + (size_t)mesh_count * 4 > data_len)
        return 0;

    for (uint32_t i = 0; i < mesh_count; i++) {
        uint32_t h = ch_read_u32(data, pos);
        pos += 4;
        if (h != 0)
            ch_add_hash(ch_mesh_ht, h, rel_path, &ch_mesh_unique);
    }

    /* Read texture count */
    if (pos + 4 > data_len)
        return 1; /* OK, just no textures */

    uint32_t tex_count = ch_read_u32(data, pos);
    pos += 4;

    if (tex_count > 10000)
        return 1;

    if (pos + (size_t)tex_count * 4 > data_len)
        return 1;

    for (uint32_t i = 0; i < tex_count; i++) {
        uint32_t h = ch_read_u32(data, pos);
        pos += 4;
        if (h != 0)
            ch_add_hash(ch_tex_ht, h, rel_path, &ch_tex_unique);
    }

    return 1;
}

/* ── HED file parsing ────────────────────────────────────────────────── */

static void ch_parse_hashed_hed(const uint8_t *data, size_t data_len, const char *rel_path)
{
    size_t pos = 0;
    while (pos + 12 <= data_len) {
        uint32_t h = ch_read_u32(data, pos);
        uint32_t offset = ch_read_u32(data, pos + 4);
        uint32_t size = ch_read_u32(data, pos + 8);
        pos += 12;
        if (h == 0 && offset == 0 && size == 0)
            break;
        ch_add_hash(ch_hed_ht, h, rel_path, &ch_hed_unique);
    }
}

/* ── THAW skin parsing ───────────────────────────────────────────────── */

static int ch_parse_thaw_pc_skin(const uint8_t *data, size_t data_len, const char *rel_path)
{
    if (data_len < 48)
        return 0;

    uint16_t material_count = ch_read_u16(data, 34);
    size_t offset = 48;
    if (material_count == 0 || material_count > 4096)
        return 0;

    for (uint16_t i = 0; i < material_count; i++) {
        if (offset + 288 > data_len)
            return i > 0;

        uint32_t material_hash = ch_read_u32(data, offset);
        uint32_t material_name_hash = ch_read_u32(data, offset + 4);
        int32_t pass_count = ch_read_i32(data, offset + 8);
        if (pass_count < 0) pass_count = 0;
        if (pass_count > 4) pass_count = 4;

        if (material_hash)
            ch_add_hash(ch_mat_ht, material_hash, rel_path, &ch_mat_unique);
        if (material_name_hash)
            ch_add_hash(ch_mat_name_ht, material_name_hash, rel_path, &ch_mat_name_unique);

        for (int pass = 0; pass < pass_count; pass++) {
            uint32_t texture_hash = ch_read_u32(data, offset + 64 + (size_t)pass * 4);
            if (texture_hash)
                ch_add_hash(ch_tex_ht, texture_hash, rel_path, &ch_tex_unique);
        }

        offset += 288;
    }

    return 1;
}

static int ch_parse_thaw_ps2_skin(const uint8_t *data, size_t data_len, const char *rel_path)
{
    if (data_len < 32)
        return 0;

    uint32_t object_count = ch_read_u32(data, 0);
    uint32_t entry_count = ch_read_u32(data, 8);
    if (entry_count == 0 || entry_count > 8192)
        return 0;

    size_t entry_offset = 32 + (size_t)object_count * 8;
    if (entry_offset >= data_len)
        return 0;

    for (uint32_t index = 0; index < entry_count; index++) {
        size_t offset = entry_offset + (size_t)index * 64;
        if (offset + 36 > data_len)
            return index > 0;

        uint32_t material_hash = ch_read_u32(data, offset + 4);
        uint32_t texture_hash = ch_read_u32(data, offset + 32);
        if (material_hash)
            ch_add_hash(ch_mat_ht, material_hash, rel_path, &ch_mat_unique);
        if (texture_hash)
            ch_add_hash(ch_tex_ht, texture_hash, rel_path, &ch_tex_unique);
    }

    return 1;
}

/* ── Directory walking context ───────────────────────────────────────── */

typedef struct {
    const char *builds;
    int psx_files_found;
    int psx_files_parsed;
    int thaw_pc_skin_files_found;
    int thaw_pc_skin_files_parsed;
    int thaw_ps2_skin_files_found;
    int thaw_ps2_skin_files_parsed;
    const char **parse_errors;
    int error_count;
    int error_capacity;
} ch_walk_ctx_t;

static void ch_on_hash_source_file(const char *filepath, const char *filename, void *ctx)
{
    ch_walk_ctx_t *wc = (ch_walk_ctx_t *)ctx;
    int is_psx = ch_has_ci_suffix(filename, ".psx");
    int is_thaw_pc_skin = ch_has_ci_suffix(filename, ".skin.wpc") &&
        ch_contains_ci(filepath, "American Wasteland");
    int is_thaw_ps2_skin = ch_has_ci_suffix(filename, ".skin.ps2") &&
        ch_contains_ci(filepath, "American Wasteland");

    if (!is_psx && !is_thaw_pc_skin && !is_thaw_ps2_skin)
        return;

    const char *rel = ch_relative_path(filepath, wc->builds);
    const char *rel_stored = arena_strdup(rel);

    size_t data_len;
    uint8_t *data = read_file_bin(filepath, &data_len);
    if (!data) {
        if (wc->error_count < wc->error_capacity)
            wc->parse_errors[wc->error_count++] = rel_stored;
        return;
    }

    if (is_psx) {
        wc->psx_files_found++;
        if (ch_parse_psx(data, data_len, rel_stored))
            wc->psx_files_parsed++;
        else if (wc->error_count < wc->error_capacity)
            wc->parse_errors[wc->error_count++] = rel_stored;
    } else if (is_thaw_pc_skin) {
        wc->thaw_pc_skin_files_found++;
        if (ch_parse_thaw_pc_skin(data, data_len, rel_stored))
            wc->thaw_pc_skin_files_parsed++;
        else if (wc->error_count < wc->error_capacity)
            wc->parse_errors[wc->error_count++] = rel_stored;
    } else if (is_thaw_ps2_skin) {
        wc->thaw_ps2_skin_files_found++;
        if (ch_parse_thaw_ps2_skin(data, data_len, rel_stored))
            wc->thaw_ps2_skin_files_parsed++;
        else if (wc->error_count < wc->error_capacity)
            wc->parse_errors[wc->error_count++] = rel_stored;
    }

    free(data);
}

/* ── JSON output ─────────────────────────────────────────────────────── */

static void ch_write_hash_section(FILE *f, const char *name, ch_hash_entry_t *ht, int unique_count)
{
    /* Collect and sort hashes */
    uint32_t *hashes = (uint32_t *)malloc(unique_count * sizeof(uint32_t));
    int count = 0;
    for (uint32_t i = 0; i < CH_HT_SIZE; i++) {
        if (ht[i].occupied)
            hashes[count++] = ht[i].hash;
    }
    qsort(hashes, count, sizeof(uint32_t), uint32_cmp);

    fprintf(f, "  \"%s\": {\n", name);
    for (int i = 0; i < count; i++) {
        ch_hash_entry_t *e = ch_ht_get(ht, hashes[i], NULL);
        fprintf(f, "    \"0x%08X\": [", e->hash);
        for (int j = 0; j < e->file_count; j++) {
            if (j > 0) fprintf(f, ", ");
            json_escape_string(f, e->files[j]);
        }
        fprintf(f, "]%s\n", i < count - 1 ? "," : "");
    }
    fprintf(f, "  }");
    free(hashes);
}

/* ── Main entry ──────────────────────────────────────────────────────── */

static int cmd_collect_hashes(int argc, char **argv)
{
    const char *builds = BUILDS_DEFAULT;
    char tools_dir[MAX_PATH];
    get_tools_dir(tools_dir, sizeof(tools_dir));

    if (argc > 1) builds = argv[1];

    fprintf(stderr, "Stage 1: Collecting unique hashes from PSX, THAW skins, and HED archives\n");
    fprintf(stderr, "Builds directory: %s\n\n", builds);

    arena_init();

    memset(ch_mesh_ht, 0, sizeof(ch_mesh_ht));
    memset(ch_tex_ht, 0, sizeof(ch_tex_ht));
    memset(ch_mat_ht, 0, sizeof(ch_mat_ht));
    memset(ch_mat_name_ht, 0, sizeof(ch_mat_name_ht));
    memset(ch_hed_ht, 0, sizeof(ch_hed_ht));

    /* Walk for PSX files */
    ch_walk_ctx_t wc = {0};
    wc.builds = builds;
    wc.error_capacity = 1024;
    wc.parse_errors = (const char **)malloc(wc.error_capacity * sizeof(const char *));

    walk_directory(builds, ch_on_hash_source_file, &wc);

    fprintf(stderr, "Found %d .psx files\n", wc.psx_files_found);
    fprintf(stderr, "Successfully parsed: %d\n", wc.psx_files_parsed);
    fprintf(stderr, "Found %d THAW PC skin files\n", wc.thaw_pc_skin_files_found);
    fprintf(stderr, "Successfully parsed: %d\n", wc.thaw_pc_skin_files_parsed);
    fprintf(stderr, "Found %d THAW PS2 skin files\n", wc.thaw_ps2_skin_files_found);
    fprintf(stderr, "Successfully parsed: %d\n", wc.thaw_ps2_skin_files_parsed);
    fprintf(stderr, "Parse errors/skipped: %d\n", wc.error_count);
    fprintf(stderr, "Unique mesh hashes: %d\n", ch_mesh_unique);
    fprintf(stderr, "Unique texture hashes: %d\n", ch_tex_unique);
    fprintf(stderr, "Unique material hashes: %d\n", ch_mat_unique);
    fprintf(stderr, "Unique material-name hashes: %d\n", ch_mat_name_unique);

    /* Check overlap */
    int overlap = 0;
    for (uint32_t i = 0; i < CH_HT_SIZE; i++) {
        if (ch_mesh_ht[i].occupied) {
            uint32_t idx = ch_mesh_ht[i].hash & CH_HT_MASK;
            while (ch_tex_ht[idx].occupied) {
                if (ch_tex_ht[idx].hash == ch_mesh_ht[i].hash) {
                    overlap++;
                    break;
                }
                idx = (idx + 1) & CH_HT_MASK;
            }
        }
    }
    fprintf(stderr, "Hashes appearing in both mesh and texture: %d\n", overlap);

    /* Parse hashed HED files */
    char hed_path[MAX_PATH * 2];
    snprintf(hed_path, sizeof(hed_path),
             "%s\\Tony Hawk's Pro Skater 2 (2000-9-19, PSX - Final)\\CD.HED", builds);

    size_t hed_data_len;
    uint8_t *hed_data = read_file_bin(hed_path, &hed_data_len);
    if (hed_data) {
        const char *rel = hed_path;
        size_t builds_len = strlen(builds);
        if (strncmp(hed_path, builds, builds_len) == 0 && hed_path[builds_len] == '\\')
            rel = hed_path + builds_len + 1;
        const char *rel_stored = arena_strdup(rel);

        ch_parse_hashed_hed(hed_data, hed_data_len, rel_stored);
        free(hed_data);

        size_t entry_count = 0;
        for (uint32_t i = 0; i < CH_HT_SIZE; i++)
            if (ch_hed_ht[i].occupied) entry_count++;
        fprintf(stderr, "\nHashed HED: %s\n", rel_stored);
        fprintf(stderr, "  Entries: %zu\n", entry_count);
    } else {
        fprintf(stderr, "\nWARNING: HED file not found: %s\n", hed_path);
    }

    fprintf(stderr, "\nUnique HED hashes: %d\n", ch_hed_unique);

    /* Combined stats */
    hashset_t all_hashes;
    hashset_init_sized(&all_hashes, 18);
    for (uint32_t i = 0; i < CH_HT_SIZE; i++) {
        if (ch_mesh_ht[i].occupied) hashset_add(&all_hashes, ch_mesh_ht[i].hash);
        if (ch_tex_ht[i].occupied) hashset_add(&all_hashes, ch_tex_ht[i].hash);
        if (ch_mat_ht[i].occupied) hashset_add(&all_hashes, ch_mat_ht[i].hash);
        if (ch_mat_name_ht[i].occupied) hashset_add(&all_hashes, ch_mat_name_ht[i].hash);
        if (ch_hed_ht[i].occupied) hashset_add(&all_hashes, ch_hed_ht[i].hash);
    }
    fprintf(stderr, "\nTotal unique hashes (all sources): %u\n", all_hashes.count);

    /* Write hash_targets.json */
    char out_path[MAX_PATH];
    snprintf(out_path, sizeof(out_path), "%s\\hash_targets.json", tools_dir);
    FILE *f = fopen(out_path, "w");
    if (!f) { perror(out_path); return 1; }

    fprintf(f, "{\n");
    ch_write_hash_section(f, "mesh_hashes", ch_mesh_ht, ch_mesh_unique);
    fprintf(f, ",\n");
    ch_write_hash_section(f, "texture_hashes", ch_tex_ht, ch_tex_unique);
    fprintf(f, ",\n");
    ch_write_hash_section(f, "material_hashes", ch_mat_ht, ch_mat_unique);
    fprintf(f, ",\n");
    ch_write_hash_section(f, "material_name_hashes", ch_mat_name_ht, ch_mat_name_unique);
    fprintf(f, ",\n");
    ch_write_hash_section(f, "hed_hashes", ch_hed_ht, ch_hed_unique);
    fprintf(f, ",\n");

    /* Stats */
    fprintf(f, "  \"stats\": {\n");
    fprintf(f, "    \"psx_files_found\": %d,\n", wc.psx_files_found);
    fprintf(f, "    \"psx_files_parsed\": %d,\n", wc.psx_files_parsed);
    fprintf(f, "    \"thaw_pc_skin_files_found\": %d,\n", wc.thaw_pc_skin_files_found);
    fprintf(f, "    \"thaw_pc_skin_files_parsed\": %d,\n", wc.thaw_pc_skin_files_parsed);
    fprintf(f, "    \"thaw_ps2_skin_files_found\": %d,\n", wc.thaw_ps2_skin_files_found);
    fprintf(f, "    \"thaw_ps2_skin_files_parsed\": %d,\n", wc.thaw_ps2_skin_files_parsed);
    fprintf(f, "    \"parse_errors\": %d,\n", wc.error_count);
    fprintf(f, "    \"unique_mesh_hashes\": %d,\n", ch_mesh_unique);
    fprintf(f, "    \"unique_texture_hashes\": %d,\n", ch_tex_unique);
    fprintf(f, "    \"unique_material_hashes\": %d,\n", ch_mat_unique);
    fprintf(f, "    \"unique_material_name_hashes\": %d,\n", ch_mat_name_unique);
    fprintf(f, "    \"unique_hed_hashes\": %d,\n", ch_hed_unique);
    fprintf(f, "    \"total_unique_hashes\": %u,\n", all_hashes.count);
    fprintf(f, "    \"mesh_tex_overlap\": %d\n", overlap);
    fprintf(f, "  },\n");

    /* Parse errors */
    fprintf(f, "  \"parse_errors\": [");
    for (int i = 0; i < wc.error_count; i++) {
        if (i > 0) fprintf(f, ", ");
        json_escape_string(f, wc.parse_errors[i]);
    }
    fprintf(f, "]\n}\n");
    fclose(f);
    fprintf(stderr, "\nOutput written to: %s\n", out_path);

    /* Write flat hash list */
    snprintf(out_path, sizeof(out_path), "%s\\all_hashes.txt", tools_dir);
    f = fopen(out_path, "w");
    if (f) {
        /* Collect all unique hashes sorted */
        uint32_t *sorted = (uint32_t *)malloc(all_hashes.count * sizeof(uint32_t));
        uint32_t si = 0;
        for (uint32_t i = 0; i < all_hashes.capacity; i++) {
            if (all_hashes.occupied[i])
                sorted[si++] = all_hashes.keys[i];
        }
        qsort(sorted, si, sizeof(uint32_t), uint32_cmp);
        for (uint32_t i = 0; i < si; i++)
            fprintf(f, "0x%08X\n", sorted[i]);
        fclose(f);
        free(sorted);
        fprintf(stderr, "All hashes (flat list): %s\n", out_path);
    }

    /* Show sample errors */
    if (wc.error_count > 0) {
        fprintf(stderr, "\nParse errors (first 20):\n");
        int show = wc.error_count < 20 ? wc.error_count : 20;
        for (int i = 0; i < show; i++)
            fprintf(stderr, "  %s\n", wc.parse_errors[i]);
    }

    free(wc.parse_errors);
    hashset_free(&all_hashes);
    free(g_arena);
    g_arena = NULL;

    return 0;
}
