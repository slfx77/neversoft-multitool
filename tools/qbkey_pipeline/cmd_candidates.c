/*
 * cmd_candidates.c — Generate review_candidates.json from brute-force results.
 *
 * Also runs Stage 3 (brute_force_pipeline) logic inline:
 * reads matched_hashes.json and merges with brute-force results.
 *
 * Usage: qbkey_pipeline candidates
 */

/* ── Per-hash entry storage ──────────────────────────────────────────── */

#define CAND_MAX_FILES       64
#define CAND_MAX_CANDIDATES  20
#define CAND_ENTRY_BITS      17
#define CAND_ENTRY_SIZE      (1u << CAND_ENTRY_BITS)
#define CAND_ENTRY_MASK      (CAND_ENTRY_SIZE - 1)

typedef struct {
    const char *name;
    int score;
    int length;
} cand_candidate_t;

typedef struct {
    uint32_t hash;
    int type;       /* bitmask: 1=mesh, 2=texture, 4=material, 8=material_name */
    const char *files[CAND_MAX_FILES];
    int file_count;
    cand_candidate_t candidates[CAND_MAX_CANDIDATES];
    int candidate_count;
    int occupied;
} cand_entry_t;

static cand_entry_t *cand_entries;

static cand_entry_t *cand_entry_get(uint32_t hash)
{
    uint32_t idx = hash & CAND_ENTRY_MASK;
    while (cand_entries[idx].occupied) {
        if (cand_entries[idx].hash == hash)
            return &cand_entries[idx];
        idx = (idx + 1) & CAND_ENTRY_MASK;
    }
    cand_entries[idx].occupied = 1;
    cand_entries[idx].hash = hash;
    cand_entries[idx].type = -1;
    cand_entries[idx].file_count = 0;
    cand_entries[idx].candidate_count = 0;
    return &cand_entries[idx];
}

#define CAND_TYPE_MESH          1
#define CAND_TYPE_TEXTURE       2
#define CAND_TYPE_MATERIAL      4
#define CAND_TYPE_MATERIAL_NAME 8

static void cand_type_to_string(int type, char *out, size_t out_size)
{
    const char *parts[4];
    int count = 0;
    out[0] = '\0';

    if (type & CAND_TYPE_MESH) parts[count++] = "mesh";
    if (type & CAND_TYPE_TEXTURE) parts[count++] = "texture";
    if (type & CAND_TYPE_MATERIAL) parts[count++] = "material";
    if (type & CAND_TYPE_MATERIAL_NAME) parts[count++] = "material_name";
    if (count == 0) {
        snprintf(out, out_size, "unknown");
        return;
    }

    for (int i = 0; i < count; i++) {
        size_t used = strlen(out);
        snprintf(out + used, out_size > used ? out_size - used : 0,
                 "%s%s", i > 0 ? "," : "", parts[i]);
    }
}

/* ── Stem storage for scoring ────────────────────────────────────────── */

#define CAND_MAX_STEMS         32000
#define CAND_MAX_SUBSTR_STEMS  32000

static const char *cand_stems[CAND_MAX_STEMS];
static int cand_stem_count;
static const char *cand_substr_stems[CAND_MAX_SUBSTR_STEMS];
static int cand_substr_stem_count;

/* ── Scoring helpers ─────────────────────────────────────────────────── */

static int cand_is_vowel(char c)
{
    return c == 'a' || c == 'e' || c == 'i' || c == 'o' || c == 'u';
}

static int cand_is_valid_name(const char *n)
{
    if (!*n) return 0;
    char c = n[0];
    if (!((c >= 'a' && c <= 'z') || c == '_')) return 0;
    for (int i = 1; n[i]; i++) {
        c = n[i];
        if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_'))
            return 0;
    }
    return 1;
}

static int cand_is_compound_underscore(const char *n)
{
    int i = 0;
    if (!(n[i] >= 'a' && n[i] <= 'z')) return 0;
    while (n[i] >= 'a' && n[i] <= 'z') i++;
    if (n[i] != '_') return 0;
    i++;
    if (!(n[i] >= 'a' && n[i] <= 'z')) return 0;
    while (n[i] >= 'a' && n[i] <= 'z') i++;
    while (n[i] >= '0' && n[i] <= '9') i++;
    return n[i] == '\0';
}

static int cand_is_word_digits(const char *n)
{
    int i = 0;
    if (!(n[i] >= 'a' && n[i] <= 'z')) return 0;
    while (n[i] >= 'a' && n[i] <= 'z') i++;
    int dstart = i;
    while (n[i] >= '0' && n[i] <= '9') i++;
    int dlen = i - dstart;
    return n[i] == '\0' && dlen >= 1 && dlen <= 2;
}

static int cand_has_natural_vowel_ratio(const char *n)
{
    int vowels = 0, consonants = 0;
    for (int i = 0; n[i]; i++) {
        char c = n[i];
        if (c >= 'a' && c <= 'z') {
            if (cand_is_vowel(c)) vowels++;
            else consonants++;
        }
    }
    if (vowels + consonants == 0) return 0;
    return vowels > 0 && consonants <= vowels * 3;
}

static int cand_score(const char *name, const strset_t *stem_set)
{
    if (!cand_is_valid_name(name)) return -100;

    int score = 0;
    int len = (int)strlen(name);

    if (strset_contains(stem_set, name))
        score += 10;

    for (int s = 0; s < cand_substr_stem_count; s++) {
        int slen = (int)strlen(cand_substr_stems[s]);
        if (slen > len) continue;
        if (slen == len && strcmp(cand_substr_stems[s], name) == 0) continue;
        if (strstr(name, cand_substr_stems[s])) {
            score += 5;
            break;
        }
    }

    if (cand_is_compound_underscore(name))
        score += 3;
    else if (cand_is_word_digits(name))
        score += 2;

    int vowels = 0, consonants = 0, alpha_count = 0;
    for (int i = 0; name[i]; i++) {
        char c = name[i];
        if (c >= 'a' && c <= 'z') {
            alpha_count++;
            if (cand_is_vowel(c)) vowels++;
            else consonants++;
        }
    }
    if (alpha_count > 0) {
        if (vowels > 0 && consonants <= vowels * 4)
            score += 2;
        else if (vowels == 0 && alpha_count > 3)
            score -= 3;
    }

    char seen[256] = {0};
    int unique = 0, non_underscore = 0;
    for (int i = 0; name[i]; i++) {
        char c = name[i];
        if (c != '_') {
            non_underscore++;
            if (!seen[(unsigned char)c]) {
                seen[(unsigned char)c] = 1;
                unique++;
            }
        }
    }
    if (len > 4 && unique == non_underscore)
        score -= 2;

    return score;
}

static int cand_is_plausible(const char *name, int score)
{
    int len = (int)strlen(name);
    if (len <= 4) return score >= 0;
    if (!cand_has_natural_vowel_ratio(name)) return 0;
    if (len == 5) return score >= 2;
    if (len >= 6) return score >= 5;
    return score >= 2;
}

/* ── Directory walking for stems ─────────────────────────────────────── */

static void cand_on_file_stem(const char *filepath, const char *filename, void *ctx)
{
    (void)filepath;
    strset_t *stem_set = (strset_t *)ctx;

    char stem_buf[256];
    path_stem_lower(filename, stem_buf, sizeof(stem_buf));
    int slen = (int)strlen(stem_buf);

    if (slen >= 2 && !strset_contains(stem_set, stem_buf)) {
        const char *stored = arena_strdup(stem_buf);
        strset_add(stem_set, stored);
        if (cand_stem_count < CAND_MAX_STEMS)
            cand_stems[cand_stem_count++] = stored;
        if (slen >= 4 && cand_substr_stem_count < CAND_MAX_SUBSTR_STEMS)
            cand_substr_stems[cand_substr_stem_count++] = stored;
    }
}

/* ── JSON section parsing ────────────────────────────────────────────── */

static void cand_parse_hash_section(const char *json, const char *section_name, int type_val)
{
    char needle[64];
    snprintf(needle, sizeof(needle), "\"%s\"", section_name);
    const char *sect = strstr(json, needle);
    if (!sect) return;

    const char *brace = strchr(sect + strlen(needle), '{');
    if (!brace) return;

    int depth = 1;
    const char *p = brace + 1;
    const char *end = NULL;
    while (*p && depth > 0) {
        if (*p == '{') depth++;
        else if (*p == '}') { depth--; if (depth == 0) { end = p; break; } }
        p++;
    }
    if (!end) return;

    p = brace + 1;
    while (p < end) {
        const char *q = strstr(p, "\"0x");
        if (!q || q >= end) break;
        q++;

        uint32_t hash;
        if (!parse_hex8(q + 2, &hash)) { p = q + 3; continue; }

        const char *arr_start = strchr(q + 10, '[');
        if (!arr_start || arr_start >= end) break;
        const char *arr_end = strchr(arr_start, ']');
        if (!arr_end || arr_end >= end) break;

        cand_entry_t *e = cand_entry_get(hash);
        if (e->type == -1) e->type = type_val;
        else e->type |= type_val;

        const char *fp = arr_start + 1;
        while (fp < arr_end) {
            const char *fq = strchr(fp, '"');
            if (!fq || fq >= arr_end) break;
            fq++;
            const char *fe = strchr(fq, '"');
            if (!fe || fe >= arr_end) break;

            int flen = (int)(fe - fq);
            char fbuf[512];
            if (flen < 512) {
                memcpy(fbuf, fq, flen);
                fbuf[flen] = '\0';
                const char *base = path_basename(fbuf);
                if (e->file_count < CAND_MAX_FILES) {
                    int dup = 0;
                    for (int i = 0; i < e->file_count; i++) {
                        if (strcmp(e->files[i], base) == 0) { dup = 1; break; }
                    }
                    if (!dup)
                        e->files[e->file_count++] = arena_strdup(base);
                }
            }
            fp = fe + 1;
        }
        p = arr_end + 1;
    }
}

static void cand_parse_resolved(const char *json, hashset_t *resolved)
{
    const char *sect = strstr(json, "\"qbkey_resolved\"");
    if (!sect) return;
    const char *brace = strchr(sect + 16, '{');
    if (!brace) return;

    int depth = 1;
    const char *p = brace + 1;
    while (*p && depth > 0) {
        if (*p == '"' && p[1] == '0' && p[2] == 'x') {
            uint32_t hash;
            if (parse_hex8(p + 3, &hash))
                hashset_add(resolved, hash);
            p += 12;
        } else if (*p == '{') { depth++; p++; }
        else if (*p == '}') { depth--; p++; }
        else p++;
    }
}

/* ── Main entry ──────────────────────────────────────────────────────── */

static int cmd_candidates(int argc, char **argv)
{
    (void)argc; (void)argv;
    char tools_dir[MAX_PATH];
    get_tools_dir(tools_dir, sizeof(tools_dir));

    arena_init();
    cand_entries = (cand_entry_t *)calloc(CAND_ENTRY_SIZE, sizeof(cand_entry_t));

    fprintf(stderr, "Generating review_candidates.json\n");
    fprintf(stderr, "============================================================\n");

    /* Load stems */
    fprintf(stderr, "Loading build filename stems...\n");
    strset_t stem_set;
    strset_init_sized(&stem_set, 17);
    walk_directory(BUILDS_DEFAULT, cand_on_file_stem, &stem_set);
    fprintf(stderr, "  %d stems (%d substring stems)\n", cand_stem_count, cand_substr_stem_count);

    /* Load hash targets */
    fprintf(stderr, "Loading hash targets...\n");
    char path[MAX_PATH];
    size_t sz;

    snprintf(path, sizeof(path), "%s\\hash_targets.json", tools_dir);
    char *tgt_json = read_file(path, &sz);
    cand_parse_hash_section(tgt_json, "mesh_hashes", CAND_TYPE_MESH);
    cand_parse_hash_section(tgt_json, "texture_hashes", CAND_TYPE_TEXTURE);
    cand_parse_hash_section(tgt_json, "material_hashes", CAND_TYPE_MATERIAL);
    cand_parse_hash_section(tgt_json, "material_name_hashes", CAND_TYPE_MATERIAL_NAME);

    int total_targets = 0;
    for (uint32_t i = 0; i < CAND_ENTRY_SIZE; i++) {
        if (cand_entries[i].occupied && cand_entries[i].type >= 0)
            total_targets++;
    }
    fprintf(stderr, "  %d target hashes loaded\n", total_targets);
    free(tgt_json);

    /* Load resolved */
    fprintf(stderr, "Loading resolved hashes...\n");
    hashset_t resolved;
    hashset_init(&resolved);
    snprintf(path, sizeof(path), "%s\\final_resolved_hashes.json", tools_dir);
    char *res_json = read_file(path, &sz);
    cand_parse_resolved(res_json, &resolved);
    fprintf(stderr, "  %u resolved\n", resolved.count);
    free(res_json);

    /* Parse brute-force results */
    fprintf(stderr, "Parsing brute-force results and scoring candidates...\n");
    snprintf(path, sizeof(path), "%s\\brute_force_results_prefiltered.txt", tools_dir);
    FILE *bf = fopen(path, "r");
    if (!bf) { perror(path); return 1; }

    char *bfbuf = (char *)malloc(1 << 20);
    setvbuf(bf, bfbuf, _IOFBF, 1 << 20);

    char line[512];
    size_t bf_lines = 0, bf_scored = 0, bf_accepted = 0;

    while (fgets(line, sizeof(line), bf)) {
        bf_lines++;
        if ((bf_lines & 0x000FFFFF) == 0)
            fprintf(stderr, "\r  %zuM lines scored", bf_lines / 1000000);

        size_t len = strlen(line);
        while (len > 0 && (line[len - 1] == '\n' || line[len - 1] == '\r'))
            line[--len] = '\0';

        if (len < 14) continue;
        if (line[0] != '0' || line[1] != 'x') continue;
        if (line[10] != ' ' || line[11] != ':' || line[12] != ' ') continue;

        uint32_t hash;
        if (!parse_hex8(line + 2, &hash)) continue;
        if (hashset_contains(&resolved, hash)) continue;

        const char *name = line + 13;
        int namelen = (int)(len - 13);

        int score = cand_score(name, &stem_set);
        bf_scored++;

        if (score <= -100) continue;
        if (!cand_is_plausible(name, score)) continue;
        bf_accepted++;

        cand_entry_t *e = cand_entry_get(hash);
        if (e->candidate_count < CAND_MAX_CANDIDATES) {
            cand_candidate_t *c = &e->candidates[e->candidate_count++];
            c->name = arena_strdup(name);
            c->score = score;
            c->length = namelen;
        }
    }
    fclose(bf);
    free(bfbuf);

    fprintf(stderr, "\r%80s\r", "");
    fprintf(stderr, "  %zu lines, %zu scored, %zu accepted candidates\n",
            bf_lines, bf_scored, bf_accepted);

    /* Sort candidates per entry */
    for (uint32_t i = 0; i < CAND_ENTRY_SIZE; i++) {
        if (!cand_entries[i].occupied || cand_entries[i].candidate_count <= 1) continue;
        for (int j = 1; j < cand_entries[i].candidate_count; j++) {
            cand_candidate_t tmp = cand_entries[i].candidates[j];
            int k = j - 1;
            while (k >= 0 &&
                   (cand_entries[i].candidates[k].score < tmp.score ||
                    (cand_entries[i].candidates[k].score == tmp.score &&
                     cand_entries[i].candidates[k].length > tmp.length))) {
                cand_entries[i].candidates[k + 1] = cand_entries[i].candidates[k];
                k--;
            }
            cand_entries[i].candidates[k + 1] = tmp;
        }
    }

    /* Write output */
    fprintf(stderr, "Writing review_candidates.json...\n");
    snprintf(path, sizeof(path), "%s\\review_candidates.json", tools_dir);
    FILE *out = fopen(path, "w");
    if (!out) { perror(path); return 1; }

    int total_unresolved = 0, with_candidates = 0, without_candidates = 0;
    for (uint32_t i = 0; i < CAND_ENTRY_SIZE; i++) {
        if (!cand_entries[i].occupied || cand_entries[i].type < 0) continue;
        if (hashset_contains(&resolved, cand_entries[i].hash)) continue;
        total_unresolved++;
        if (cand_entries[i].candidate_count > 0) with_candidates++;
        else without_candidates++;
    }

    uint32_t *sorted_hashes = (uint32_t *)malloc(total_unresolved * sizeof(uint32_t));
    int si = 0;
    for (uint32_t i = 0; i < CAND_ENTRY_SIZE; i++) {
        if (!cand_entries[i].occupied || cand_entries[i].type < 0) continue;
        if (hashset_contains(&resolved, cand_entries[i].hash)) continue;
        sorted_hashes[si++] = cand_entries[i].hash;
    }
    qsort(sorted_hashes, si, sizeof(uint32_t), uint32_cmp);

    fprintf(out, "{\n  \"hashes\": {\n");
    for (int hi = 0; hi < si; hi++) {
        cand_entry_t *e = cand_entry_get(sorted_hashes[hi]);
        fprintf(out, "    \"0x%08X\": {\n", e->hash);

        char type_str[64];
        cand_type_to_string(e->type, type_str, sizeof(type_str));
        fprintf(out, "      \"type\": \"%s\",\n", type_str);

        fprintf(out, "      \"files\": [");
        for (int fi = 0; fi < e->file_count; fi++) {
            if (fi > 0) fprintf(out, ", ");
            json_escape_string(out, e->files[fi]);
        }
        fprintf(out, "],\n");

        fprintf(out, "      \"candidates\": [");
        for (int ci = 0; ci < e->candidate_count; ci++) {
            if (ci > 0) fprintf(out, ", ");
            fprintf(out, "{\"name\": \"%s\", \"score\": %d, \"length\": %d}",
                    e->candidates[ci].name, e->candidates[ci].score,
                    e->candidates[ci].length);
        }
        fprintf(out, "]\n");
        fprintf(out, "    }%s\n", hi < si - 1 ? "," : "");
    }

    fprintf(out, "  },\n");
    fprintf(out, "  \"stats\": {\n");
    fprintf(out, "    \"total\": %d,\n", total_unresolved);
    fprintf(out, "    \"with_candidates\": %d,\n", with_candidates);
    fprintf(out, "    \"without_candidates\": %d\n", without_candidates);
    fprintf(out, "  }\n");
    fprintf(out, "}\n");
    fclose(out);

    free(sorted_hashes);

    fprintf(stderr, "\nDone!\n");
    fprintf(stderr, "  Total hashes: %d\n", total_unresolved);
    fprintf(stderr, "  With candidates: %d\n", with_candidates);
    fprintf(stderr, "  Without candidates: %d\n", without_candidates);
    fprintf(stderr, "  Arena used: %.1f MB\n", g_arena_used / (1024.0 * 1024.0));

    free(g_arena);
    g_arena = NULL;
    strset_free(&stem_set);
    hashset_free(&resolved);
    free(cand_entries);

    return 0;
}
