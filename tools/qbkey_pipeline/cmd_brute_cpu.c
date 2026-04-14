/*
 * cmd_brute_cpu.c — CPU brute-force reversal of Neversoft CRC-32 hashes.
 *
 * Phase 1: Direct brute-force for lengths 1 through max_brute (default 7).
 * Phase 2: Meet-in-the-middle for lengths max_brute+1 through max_mitm (default 12).
 *
 * Both phases are parallelized with OpenMP when available (falls back to
 * single-threaded if compiled without -fopenmp).
 *
 * Part of qbkey_pipeline unified CLI.
 */

#ifdef _OPENMP
#include <omp.h>
#else
/* OpenMP fallback — #pragma omp directives are silently ignored by the compiler */
#define omp_get_max_threads() 1
#define omp_set_num_threads(n) ((void)(n))
#endif

/* ========================================================================= */
/* CRC-32 inverse table (forward table is g_crc_table from common.h)         */
/* ========================================================================= */

static uint32_t bcpu_crc_inv_table[256];

static void bcpu_init_crc_inv_table(void)
{
    init_crc_table(); /* ensure g_crc_table is populated */
    for (int i = 0; i < 256; i++) {
        uint32_t top_byte = g_crc_table[i] >> 24;
        bcpu_crc_inv_table[top_byte] = (uint32_t)i;
    }
}

static inline uint32_t bcpu_crc_step(uint32_t crc, uint8_t byte)
{
    return (crc >> 8) ^ g_crc_table[(crc ^ byte) & 0xFF];
}

static inline uint32_t bcpu_crc_unstep(uint32_t crc_next, uint8_t byte)
{
    uint32_t index = bcpu_crc_inv_table[crc_next >> 24];
    uint32_t crc_prev = ((crc_next ^ g_crc_table[index]) << 8) | (index ^ byte);
    return crc_prev;
}

/* ========================================================================= */
/* Hash target set (for batch matching)                                      */
/* ========================================================================= */

#define BCPU_MAX_TARGETS 131072

static uint32_t bcpu_targets[BCPU_MAX_TARGETS];
static uint32_t bcpu_match_targets[BCPU_MAX_TARGETS];
static int bcpu_target_count = 0;
static int bcpu_target_found[BCPU_MAX_TARGETS];
static char bcpu_fixed_prefix[MAX_NAME_LEN] = "";
static int bcpu_fixed_prefix_len = 0;
static char bcpu_fixed_suffix[MAX_NAME_LEN] = "";
static int bcpu_fixed_suffix_len = 0;
static uint32_t bcpu_initial_crc = 0xFFFFFFFF;

#define BCPU_HASHSET_SIZE 262144
#define BCPU_HASHSET_MASK (BCPU_HASHSET_SIZE - 1)

typedef struct bcpu_hashset_entry {
    uint32_t value;
    int target_idx;
    struct bcpu_hashset_entry *next;
} bcpu_hashset_entry_t;

static bcpu_hashset_entry_t bcpu_hashset_storage[BCPU_MAX_TARGETS];
static bcpu_hashset_entry_t *bcpu_hashset_buckets[BCPU_HASHSET_SIZE];
static int bcpu_hashset_storage_used = 0;

static void bcpu_hashset_init(void)
{
    memset(bcpu_hashset_buckets, 0, sizeof(bcpu_hashset_buckets));
    bcpu_hashset_storage_used = 0;
}

static void bcpu_hashset_insert(uint32_t value, int target_idx)
{
    uint32_t bucket = value & BCPU_HASHSET_MASK;
    bcpu_hashset_entry_t *e = &bcpu_hashset_storage[bcpu_hashset_storage_used++];
    e->value = value;
    e->target_idx = target_idx;
    e->next = bcpu_hashset_buckets[bucket];
    bcpu_hashset_buckets[bucket] = e;
}

static inline int bcpu_hashset_lookup(uint32_t value)
{
    uint32_t bucket = value & BCPU_HASHSET_MASK;
    bcpu_hashset_entry_t *e = bcpu_hashset_buckets[bucket];
    while (e) {
        if (e->value == value)
            return e->target_idx;
        e = e->next;
    }
    return -1;
}

/* ========================================================================= */
/* Character set                                                             */
/* ========================================================================= */

static uint8_t bcpu_charset[37];
static int bcpu_charset_size = 0;
static int bcpu_include_digits = 0;
static int bcpu_char_to_idx[256];

static void bcpu_init_charset(void)
{
    bcpu_charset_size = 0;
    memset(bcpu_char_to_idx, 0, sizeof(bcpu_char_to_idx));
    for (int c = 'a'; c <= 'z'; c++) {
        bcpu_char_to_idx[c] = bcpu_charset_size;
        bcpu_charset[bcpu_charset_size++] = (uint8_t)c;
    }
    if (bcpu_include_digits) {
        for (int c = '0'; c <= '9'; c++) {
            bcpu_char_to_idx[c] = bcpu_charset_size;
            bcpu_charset[bcpu_charset_size++] = (uint8_t)c;
        }
    }
    bcpu_char_to_idx['_'] = bcpu_charset_size;
    bcpu_charset[bcpu_charset_size++] = '_';
}

static int bcpu_first_char_count(void)
{
    return bcpu_charset_size - 1; /* exclude underscore which is last */
}

static int bcpu_variable_first_char_count(void)
{
    return bcpu_fixed_prefix_len > 0 ? bcpu_charset_size : bcpu_first_char_count();
}

static uint32_t bcpu_adjust_target_for_suffix(uint32_t hash)
{
    uint32_t adjusted = hash;
    for (int i = bcpu_fixed_suffix_len - 1; i >= 0; i--)
        adjusted = bcpu_crc_unstep(adjusted, (uint8_t)bcpu_fixed_suffix[i]);
    return adjusted;
}

static uint32_t bcpu_compute_crc32_with_suffix(const char *body, int body_len)
{
    uint32_t crc = 0xFFFFFFFF;
    for (int i = 0; i < bcpu_fixed_prefix_len; i++)
        crc = bcpu_crc_step(crc, (uint8_t)bcpu_fixed_prefix[i]);
    for (int i = 0; i < body_len; i++)
        crc = bcpu_crc_step(crc, (uint8_t)body[i]);
    for (int i = 0; i < bcpu_fixed_suffix_len; i++)
        crc = bcpu_crc_step(crc, (uint8_t)bcpu_fixed_suffix[i]);
    return crc;
}

static void bcpu_build_full_name(char *out, size_t out_size, const char *body, int body_len)
{
    if (out_size == 0)
        return;

    size_t prefix_copy = (size_t)bcpu_fixed_prefix_len;
    if (prefix_copy > out_size - 1)
        prefix_copy = out_size - 1;
    memcpy(out, bcpu_fixed_prefix, prefix_copy);

    size_t copy_len = (size_t)body_len;
    if (prefix_copy + copy_len > out_size - 1)
        copy_len = out_size - 1 - prefix_copy;
    memcpy(out + prefix_copy, body, copy_len);

    if (copy_len > out_size - 1)
        copy_len = out_size - 1;

    size_t suffix_copy = (size_t)bcpu_fixed_suffix_len;
    if (prefix_copy + copy_len + suffix_copy > out_size - 1)
        suffix_copy = out_size - 1 - prefix_copy - copy_len;
    memcpy(out + prefix_copy + copy_len, bcpu_fixed_suffix, suffix_copy);
    out[prefix_copy + copy_len + suffix_copy] = '\0';
}

/* ========================================================================= */
/* Phase 1: Direct brute-force (OpenMP parallelized)                         */
/* ========================================================================= */

static int bcpu_total_matches = 0;
static int bcpu_num_threads = 0;

static void bcpu_report_match(int target_idx, const char *name, int len)
{
    #pragma omp critical(bcpu_report)
    {
        char full[MAX_NAME_LEN];
        bcpu_build_full_name(full, sizeof(full), name, len);
        bcpu_target_found[target_idx]++;
        printf("0x%08X : %s\n", bcpu_targets[target_idx], full);
        fflush(stdout);
        bcpu_total_matches++;
    }
}

static void bcpu_brute_force(int min_len, int max_len)
{
    for (int target_len = min_len; target_len <= max_len; target_len++) {
        double t0 = get_time();
        uint64_t total_count = 0;
        int found_at_len = 0;
        int nfirst = bcpu_variable_first_char_count();

        #pragma omp parallel for schedule(static) reduction(+:total_count,found_at_len)
        for (int fc = 0; fc < nfirst; fc++) {
            uint8_t buf[32];
            uint32_t crc_stack[32];
            int idx_stack[32];

            buf[0] = bcpu_charset[fc];
            crc_stack[0] = bcpu_initial_crc;
            crc_stack[1] = bcpu_crc_step(crc_stack[0], buf[0]);

            if (target_len == 1) {
                int idx = bcpu_hashset_lookup(crc_stack[1]);
                if (idx >= 0) {
                    bcpu_report_match(idx, (const char *)buf, 1);
                    found_at_len++;
                }
                total_count++;
            } else {
                for (int p = 1; p < target_len; p++) {
                    idx_stack[p] = 0;
                    buf[p] = bcpu_charset[0];
                    crc_stack[p + 1] = bcpu_crc_step(crc_stack[p], bcpu_charset[0]);
                }

                while (1) {
                    uint32_t final_crc = crc_stack[target_len];
                    int idx = bcpu_hashset_lookup(final_crc);
                    if (idx >= 0) {
                        bcpu_report_match(idx, (const char *)buf, target_len);
                        found_at_len++;
                    }
                    total_count++;

                    int pos = target_len - 1;
                    while (pos >= 1) {
                        idx_stack[pos]++;
                        if (idx_stack[pos] < bcpu_charset_size) {
                            buf[pos] = bcpu_charset[idx_stack[pos]];
                            crc_stack[pos + 1] = bcpu_crc_step(crc_stack[pos], buf[pos]);
                            for (int p = pos + 1; p < target_len; p++) {
                                idx_stack[p] = 0;
                                buf[p] = bcpu_charset[0];
                                crc_stack[p + 1] = bcpu_crc_step(crc_stack[p], bcpu_charset[0]);
                            }
                            break;
                        }
                        pos--;
                    }

                    if (pos < 1)
                        break;
                }
            }
        }

        double elapsed = get_time() - t0;
        fprintf(stderr, "  Length %2d: %12llu strings, %d match(es), %.2fs\n",
                target_len, (unsigned long long)total_count, found_at_len, elapsed);
    }
}

/* ========================================================================= */
/* Phase 2: Meet-in-the-middle (OpenMP parallelized)                         */
/* ========================================================================= */

typedef struct {
    uint32_t crc_state;
    uint32_t prefix_encoded;
} bcpu_mitm_pair_t;

static bcpu_mitm_pair_t *bcpu_mitm_pairs = NULL;
static size_t bcpu_mitm_pairs_count = 0;
static size_t bcpu_mitm_pairs_capacity = 0;

static void bcpu_mitm_pairs_clear(void)
{
    bcpu_mitm_pairs_count = 0;
}

static void bcpu_mitm_pairs_ensure(size_t needed)
{
    if (needed > bcpu_mitm_pairs_capacity) {
        bcpu_mitm_pairs_capacity = needed + (needed >> 2);
        bcpu_mitm_pairs = realloc(bcpu_mitm_pairs,
                                  bcpu_mitm_pairs_capacity * sizeof(bcpu_mitm_pair_t));
        if (!bcpu_mitm_pairs) {
            fprintf(stderr, "Out of memory allocating %zu MITM pairs\n",
                    bcpu_mitm_pairs_capacity);
            exit(1);
        }
    }
}

static int bcpu_mitm_pair_cmp(const void *a, const void *b)
{
    uint32_t ca = ((const bcpu_mitm_pair_t *)a)->crc_state;
    uint32_t cb = ((const bcpu_mitm_pair_t *)b)->crc_state;
    if (ca < cb) return -1;
    if (ca > cb) return 1;
    return 0;
}

static void bcpu_decode_prefix(uint32_t encoded, int len, char *out)
{
    for (int i = len - 1; i >= 0; i--) {
        out[i] = (char)bcpu_charset[encoded % bcpu_charset_size];
        encoded /= bcpu_charset_size;
    }
    out[len] = '\0';
}

static uint32_t bcpu_encode_prefix(const uint8_t *buf, int len)
{
    uint32_t encoded = 0;
    for (int i = 0; i < len; i++)
        encoded = encoded * bcpu_charset_size + bcpu_char_to_idx[buf[i]];
    return encoded;
}

static void bcpu_enumerate_prefixes(int plen, uint32_t init_crc)
{
    int is_first = (init_crc == 0xFFFFFFFF);
    int nfirst = is_first ? bcpu_first_char_count() : bcpu_charset_size;

    size_t combos_per_first = 1;
    for (int i = 1; i < plen; i++)
        combos_per_first *= (size_t)bcpu_charset_size;

    size_t total = (size_t)nfirst * combos_per_first;
    bcpu_mitm_pairs_ensure(bcpu_mitm_pairs_count + total);
    bcpu_mitm_pairs_count = total;

    if (plen == 1) {
        #pragma omp parallel for schedule(static)
        for (int fc = 0; fc < nfirst; fc++) {
            uint32_t crc = bcpu_crc_step(init_crc, bcpu_charset[fc]);
            bcpu_mitm_pairs[fc].crc_state = crc;
            bcpu_mitm_pairs[fc].prefix_encoded = (uint32_t)fc;
        }
        return;
    }

    #pragma omp parallel for schedule(static)
    for (int fc = 0; fc < nfirst; fc++) {
        uint8_t buf[16];
        uint32_t crc_stack[16];
        int idx_stack[16];

        size_t write_idx = (size_t)fc * combos_per_first;

        buf[0] = bcpu_charset[fc];
        crc_stack[0] = init_crc;
        crc_stack[1] = bcpu_crc_step(init_crc, buf[0]);

        for (int p = 1; p < plen; p++) {
            idx_stack[p] = 0;
            buf[p] = bcpu_charset[0];
            crc_stack[p + 1] = bcpu_crc_step(crc_stack[p], bcpu_charset[0]);
        }

        while (1) {
            bcpu_mitm_pairs[write_idx].crc_state = crc_stack[plen];
            bcpu_mitm_pairs[write_idx].prefix_encoded = bcpu_encode_prefix(buf, plen);
            write_idx++;

            int pos = plen - 1;
            while (pos >= 1) {
                idx_stack[pos]++;
                if (idx_stack[pos] < bcpu_charset_size) {
                    buf[pos] = bcpu_charset[idx_stack[pos]];
                    crc_stack[pos + 1] = bcpu_crc_step(crc_stack[pos], buf[pos]);
                    for (int p = pos + 1; p < plen; p++) {
                        idx_stack[p] = 0;
                        buf[p] = bcpu_charset[0];
                        crc_stack[p + 1] = bcpu_crc_step(crc_stack[p], bcpu_charset[0]);
                    }
                    break;
                }
                pos--;
            }
            if (pos < 1) break;
        }
    }
}

static const bcpu_mitm_pair_t *bcpu_mitm_find(uint32_t crc_state)
{
    size_t lo = 0, hi = bcpu_mitm_pairs_count;
    while (lo < hi) {
        size_t mid = lo + (hi - lo) / 2;
        if (bcpu_mitm_pairs[mid].crc_state < crc_state)
            lo = mid + 1;
        else
            hi = mid;
    }
    if (lo < bcpu_mitm_pairs_count && bcpu_mitm_pairs[lo].crc_state == crc_state)
        return &bcpu_mitm_pairs[lo];
    return NULL;
}

static void bcpu_enumerate_suffixes_and_match(int slen, int plen)
{
    if (slen == 1) {
        #pragma omp parallel for schedule(static)
        for (int sc = 0; sc < bcpu_charset_size; sc++) {
            uint8_t byte = bcpu_charset[sc];
            for (int t = 0; t < bcpu_target_count; t++) {
                uint32_t crc = bcpu_crc_unstep(bcpu_match_targets[t], byte);
                const bcpu_mitm_pair_t *found = bcpu_mitm_find(crc);
                if (found) {
                    size_t idx = found - bcpu_mitm_pairs;
                    while (idx < bcpu_mitm_pairs_count &&
                           bcpu_mitm_pairs[idx].crc_state == crc) {
                        char prefix[16];
                        bcpu_decode_prefix(bcpu_mitm_pairs[idx].prefix_encoded,
                                           plen, prefix);
                        char full[MAX_NAME_LEN];
                        snprintf(full, sizeof(full), "%s%c", prefix, (char)byte);
                        uint32_t verify = bcpu_compute_crc32_with_suffix(full, plen + 1);
                        if (verify == bcpu_targets[t])
                            bcpu_report_match(t, full, plen + 1);
                        idx++;
                    }
                }
            }
        }
        return;
    }

    #pragma omp parallel for schedule(static)
    for (int sc = 0; sc < bcpu_charset_size; sc++) {
        uint8_t buf[16];
        int idx_stack[16];

        buf[0] = bcpu_charset[sc];
        idx_stack[0] = sc;

        for (int p = 1; p < slen; p++) {
            idx_stack[p] = 0;
            buf[p] = bcpu_charset[0];
        }

        while (1) {
            for (int t = 0; t < bcpu_target_count; t++) {
                uint32_t crc = bcpu_match_targets[t];
                for (int p = slen - 1; p >= 0; p--)
                    crc = bcpu_crc_unstep(crc, buf[p]);

                const bcpu_mitm_pair_t *found = bcpu_mitm_find(crc);
                if (found) {
                    size_t fi = found - bcpu_mitm_pairs;
                    while (fi < bcpu_mitm_pairs_count &&
                           bcpu_mitm_pairs[fi].crc_state == crc) {
                        char prefix[16], suffix[16];
                        bcpu_decode_prefix(bcpu_mitm_pairs[fi].prefix_encoded,
                                           plen, prefix);
                        memcpy(suffix, buf, slen);
                        suffix[slen] = '\0';

                        char full[MAX_NAME_LEN];
                        snprintf(full, sizeof(full), "%s%s", prefix, suffix);
                        uint32_t verify = bcpu_compute_crc32_with_suffix(full, plen + slen);
                        if (verify == bcpu_targets[t])
                            bcpu_report_match(t, full, plen + slen);
                        fi++;
                    }
                }
            }

            int pos = slen - 1;
            while (pos >= 1) {
                idx_stack[pos]++;
                if (idx_stack[pos] < bcpu_charset_size) {
                    buf[pos] = bcpu_charset[idx_stack[pos]];
                    for (int p = pos + 1; p < slen; p++) {
                        idx_stack[p] = 0;
                        buf[p] = bcpu_charset[0];
                    }
                    break;
                }
                pos--;
            }
            if (pos < 1) break;
        }
    }
}

static void bcpu_meet_in_middle(int min_len, int max_len)
{
    for (int total_len = min_len; total_len <= max_len; total_len++) {
        double t0 = get_time();

        int plen = total_len / 2;
        int slen = total_len - plen;

        fprintf(stderr, "  MITM length %2d (split %d+%d): ", total_len, plen, slen);

        bcpu_mitm_pairs_clear();
        bcpu_enumerate_prefixes(plen, bcpu_initial_crc);

        qsort(bcpu_mitm_pairs, bcpu_mitm_pairs_count,
              sizeof(bcpu_mitm_pair_t), bcpu_mitm_pair_cmp);

        fprintf(stderr, "%zu prefixes, ", bcpu_mitm_pairs_count);

        int before = bcpu_total_matches;
        bcpu_enumerate_suffixes_and_match(slen, plen);

        double elapsed = get_time() - t0;
        fprintf(stderr, "%d match(es), %.2fs\n", bcpu_total_matches - before, elapsed);
    }
}

/* ========================================================================= */
/* Argument parsing + entry point                                            */
/* ========================================================================= */

static int bcpu_parse_hash(const char *str, uint32_t *out)
{
    char *end;
    unsigned long val = strtoul(str, &end, 16);
    if (end == str || (*end != '\0' && *end != '\n' && *end != '\r'))
        return 0;
    *out = (uint32_t)val;
    return 1;
}

static int bcpu_load_hashes_from_file(const char *path)
{
    FILE *f = fopen(path, "r");
    if (!f) {
        fprintf(stderr, "Cannot open %s\n", path);
        return 0;
    }
    char line[256];
    while (fgets(line, sizeof(line), f)) {
        if (line[0] == '#' || line[0] == '\n' || line[0] == '\r')
            continue;
        uint32_t h;
        if (bcpu_parse_hash(line, &h) && bcpu_target_count < BCPU_MAX_TARGETS) {
            bcpu_targets[bcpu_target_count] = h;
            bcpu_target_found[bcpu_target_count] = 0;
            bcpu_target_count++;
        }
    }
    fclose(f);
    return 1;
}

static int cmd_brute_cpu(int argc, char **argv)
{
    int min_brute = 1;
    int max_brute = 7;
    int max_mitm = 12;
    const char *hash_file = NULL;
    const char *fixed_prefix = NULL;
    const char *fixed_suffix = NULL;

    int argi = 1;
    while (argi < argc && argv[argi][0] == '-') {
        if (strcmp(argv[argi], "-h") == 0 || strcmp(argv[argi], "--help") == 0) {
            fprintf(stderr,
                "Usage: qbkey_pipeline brute [options] hash1 [hash2 ...]\n"
                "\n"
                "CPU brute-force reversal of Neversoft CRC-32 hashes.\n"
                "Algorithm: reflected CRC-32, init 0xFFFFFFFF, no final XOR, lowercase.\n"
                "\n"
                "Options:\n"
                "  -f FILE   Read target hashes from file (one hex per line)\n"
                "  -l N      Min variable length to search (default: 1)\n"
                "  -m N      Max brute-force length (default: 7)\n"
                "  -M N      Max meet-in-the-middle length (default: 12)\n"
                "  -t N      Number of threads (default: all cores)\n"
                "  -d        Include digits [0-9] in charset (default: [a-z_] only)\n"
                "  -p TEXT   Prepend fixed prefix to every candidate before hashing\n"
                "  -s TEXT   Append fixed suffix to every candidate before hashing\n"
                "  -h        Show this help\n"
                "\n"
                "Examples:\n"
                "  qbkey_pipeline brute 0x580C0963 0xD8A0437A\n"
                "  qbkey_pipeline brute -f hashes.txt -M 14\n"
                "  qbkey_pipeline brute -f hashes.txt -m 4 -M 6 -p ped_ -s .png\n"
                "  qbkey_pipeline brute -f hashes.txt -l 6 -m 6 -p ped_acl_ -s _head.png\n"
                "  qbkey_pipeline brute -f hashes.txt -m 8 -M 10 -s .png\n");
            return 0;
        } else if (strcmp(argv[argi], "-f") == 0 && argi + 1 < argc) {
            hash_file = argv[++argi];
        } else if (strcmp(argv[argi], "-l") == 0 && argi + 1 < argc) {
            min_brute = atoi(argv[++argi]);
        } else if (strcmp(argv[argi], "-m") == 0 && argi + 1 < argc) {
            max_brute = atoi(argv[++argi]);
        } else if (strcmp(argv[argi], "-M") == 0 && argi + 1 < argc) {
            max_mitm = atoi(argv[++argi]);
        } else if (strcmp(argv[argi], "-t") == 0 && argi + 1 < argc) {
            bcpu_num_threads = atoi(argv[++argi]);
            if (bcpu_num_threads < 1) bcpu_num_threads = 1;
        } else if (strcmp(argv[argi], "-d") == 0) {
            bcpu_include_digits = 1;
        } else if (strcmp(argv[argi], "-p") == 0 && argi + 1 < argc) {
            fixed_prefix = argv[++argi];
        } else if (strcmp(argv[argi], "-s") == 0 && argi + 1 < argc) {
            fixed_suffix = argv[++argi];
        } else {
            uint32_t h;
            if (bcpu_parse_hash(argv[argi], &h) &&
                bcpu_target_count < BCPU_MAX_TARGETS) {
                bcpu_targets[bcpu_target_count] = h;
                bcpu_target_found[bcpu_target_count] = 0;
                bcpu_target_count++;
            } else {
                fprintf(stderr, "Unknown option: %s\n", argv[argi]);
                return 1;
            }
        }
        argi++;
    }

    /* Remaining args are hashes */
    while (argi < argc) {
        uint32_t h;
        const char *s = argv[argi];
        if (s[0] == '0' && (s[1] == 'x' || s[1] == 'X'))
            s += 2;
        if (bcpu_parse_hash(s, &h) && bcpu_target_count < BCPU_MAX_TARGETS) {
            bcpu_targets[bcpu_target_count] = h;
            bcpu_target_found[bcpu_target_count] = 0;
            bcpu_target_count++;
        } else {
            fprintf(stderr, "Cannot parse hash: %s\n", argv[argi]);
        }
        argi++;
    }

    if (hash_file) {
        if (!bcpu_load_hashes_from_file(hash_file))
            return 1;
    }

    if (bcpu_target_count == 0) {
        fprintf(stderr, "No target hashes specified.\n");
        return 1;
    }
    if (min_brute < 1) {
        fprintf(stderr, "Minimum variable length must be >= 1.\n");
        return 1;
    }
    if (max_brute < min_brute && max_mitm < min_brute) {
        fprintf(stderr, "Nothing to search: max lengths are below the requested minimum length.\n");
        return 1;
    }

    /* Initialize */
    bcpu_init_crc_inv_table();
    bcpu_init_charset();
    if (fixed_prefix) {
        size_t prefix_len = strlen(fixed_prefix);
        if (prefix_len >= sizeof(bcpu_fixed_prefix)) {
            fprintf(stderr, "Fixed prefix too long (%zu >= %zu)\n",
                    prefix_len, sizeof(bcpu_fixed_prefix));
            return 1;
        }
        memcpy(bcpu_fixed_prefix, fixed_prefix, prefix_len + 1);
        bcpu_fixed_prefix_len = (int)prefix_len;
        for (int i = 0; i < bcpu_fixed_prefix_len; i++)
            bcpu_initial_crc = bcpu_crc_step(bcpu_initial_crc, (uint8_t)bcpu_fixed_prefix[i]);
    }
    if (fixed_suffix) {
        size_t suffix_len = strlen(fixed_suffix);
        if (suffix_len >= sizeof(bcpu_fixed_suffix)) {
            fprintf(stderr, "Fixed suffix too long (%zu >= %zu)\n",
                    suffix_len, sizeof(bcpu_fixed_suffix));
            return 1;
        }
        memcpy(bcpu_fixed_suffix, fixed_suffix, suffix_len + 1);
        bcpu_fixed_suffix_len = (int)suffix_len;
    }

    if (bcpu_num_threads > 0)
        omp_set_num_threads(bcpu_num_threads);
    else
        bcpu_num_threads = omp_get_max_threads();

    fprintf(stderr, "Charset: %d characters (%s)\n",
            bcpu_charset_size,
            bcpu_include_digits ? "a-z, 0-9, _" : "a-z, _");
    fprintf(stderr, "Targets: %d hash(es)\n", bcpu_target_count);
    fprintf(stderr, "Threads: %d%s\n", bcpu_num_threads,
#ifdef _OPENMP
            ""
#else
            " (OpenMP unavailable, single-threaded)"
#endif
    );
    if (bcpu_fixed_prefix_len > 0)
        fprintf(stderr, "Fixed prefix: \"%s\"\n", bcpu_fixed_prefix);
    if (bcpu_fixed_suffix_len > 0)
        fprintf(stderr, "Fixed suffix: \"%s\"\n", bcpu_fixed_suffix);
    fprintf(stderr, "Brute-force: variable lengths %d-%d%s%s\n", min_brute, max_brute,
            bcpu_fixed_prefix_len > 0 ? " (prefix prepended before search)" : "",
            bcpu_fixed_suffix_len > 0 ? " (suffix appended after search)" : "");
    if (max_mitm > max_brute)
        fprintf(stderr, "Meet-in-the-middle: variable lengths %d-%d%s%s\n",
                (max_brute + 1) > min_brute ? (max_brute + 1) : min_brute, max_mitm,
                bcpu_fixed_prefix_len > 0 ? " (prefix prepended before search)" : "",
                bcpu_fixed_suffix_len > 0 ? " (suffix appended after search)" : "");

    /* Build hash set for fast lookup */
    bcpu_hashset_init();
    for (int i = 0; i < bcpu_target_count; i++) {
        bcpu_match_targets[i] = bcpu_adjust_target_for_suffix(bcpu_targets[i]);
        bcpu_hashset_insert(bcpu_match_targets[i], i);
    }

    double t_start = get_time();

    /* Phase 1: Brute force */
    if (max_brute >= min_brute) {
        fprintf(stderr, "\n--- Phase 1: Brute-force ---\n");
        bcpu_brute_force(min_brute, max_brute);
    }

    /* Phase 2: Meet-in-the-middle */
    if (max_mitm > max_brute) {
        int mitm_start = max_brute + 1;
        if (mitm_start < min_brute) mitm_start = min_brute;
        if (mitm_start < 2) mitm_start = 2;
        fprintf(stderr, "\n--- Phase 2: Meet-in-the-middle ---\n");
        bcpu_meet_in_middle(mitm_start, max_mitm);
    }

    double total_elapsed = get_time() - t_start;

    /* Summary */
    fprintf(stderr, "\n--- Summary ---\n");
    fprintf(stderr, "Total matches: %d\n", bcpu_total_matches);
    fprintf(stderr, "Total time: %.2fs\n", total_elapsed);

    int unmatched = 0;
    for (int i = 0; i < bcpu_target_count; i++) {
        if (bcpu_target_found[i] == 0) {
            if (unmatched == 0)
                fprintf(stderr, "Unmatched hashes:\n");
            fprintf(stderr, "  0x%08X\n", bcpu_targets[i]);
            unmatched++;
        }
    }
    if (unmatched)
        fprintf(stderr, "%d hash(es) had no match\n", unmatched);

    free(bcpu_mitm_pairs);
    bcpu_mitm_pairs = NULL;
    return 0;
}
