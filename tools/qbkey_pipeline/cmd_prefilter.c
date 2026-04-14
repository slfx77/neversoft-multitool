/*
 * cmd_prefilter.c — Pre-filter brute-force results, keeping at most N shortest
 *                   candidates per hash.
 *
 * Usage: qbkey_pipeline prefilter [-n max_per_hash] input.txt output.txt
 *   Default: 20 candidates per hash
 *
 * Results are already length-ordered (brute-forcer processes length 1, then 2, etc.)
 * so first N per hash = shortest N.
 */

#define PF_HT_BITS  17
#define PF_HT_SIZE  (1u << PF_HT_BITS)
#define PF_HT_MASK  (PF_HT_SIZE - 1)

typedef struct {
    uint32_t hash;
    uint32_t count;
    int      occupied;
} pf_bucket_t;

static pf_bucket_t pf_ht[PF_HT_SIZE];

static pf_bucket_t *pf_ht_get(uint32_t hash)
{
    uint32_t idx = hash & PF_HT_MASK;
    while (pf_ht[idx].occupied) {
        if (pf_ht[idx].hash == hash)
            return &pf_ht[idx];
        idx = (idx + 1) & PF_HT_MASK;
    }
    pf_ht[idx].occupied = 1;
    pf_ht[idx].hash = hash;
    pf_ht[idx].count = 0;
    return &pf_ht[idx];
}

static int cmd_prefilter(int argc, char **argv)
{
    int max_per_hash = 20;
    const char *infile = NULL, *outfile = NULL;

    for (int i = 1; i < argc; i++) {
        if (strcmp(argv[i], "-n") == 0 && i + 1 < argc) {
            max_per_hash = atoi(argv[++i]);
        } else if (!infile) {
            infile = argv[i];
        } else if (!outfile) {
            outfile = argv[i];
        }
    }

    if (!infile || !outfile) {
        fprintf(stderr, "Usage: qbkey_pipeline prefilter [-n max_per_hash] input.txt output.txt\n");
        return 1;
    }

    fprintf(stderr, "Pre-filtering %s -> %s (max %d per hash)\n", infile, outfile, max_per_hash);

    memset(pf_ht, 0, sizeof(pf_ht));

    FILE *fin = fopen(infile, "rb");
    if (!fin) { perror(infile); return 1; }

    FILE *fout = fopen(outfile, "wb");
    if (!fout) { perror(outfile); return 1; }

    char *ibuf = (char *)malloc(1 << 20);
    char *obuf = (char *)malloc(1 << 20);
    setvbuf(fin, ibuf, _IOFBF, 1 << 20);
    setvbuf(fout, obuf, _IOFBF, 1 << 20);

    char line[512];
    size_t total = 0, kept = 0, capped = 0, bad = 0;

    while (fgets(line, sizeof(line), fin)) {
        total++;

        if ((total & 0x00FFFFFF) == 0) {
            fprintf(stderr, "\r  %zuM read, %zuM kept, %zuM capped, %zuM bad",
                    total / 1000000, kept / 1000000, capped / 1000000, bad / 1000000);
        }

        /* Strip trailing newline/CR */
        size_t len = strlen(line);
        while (len > 0 && (line[len - 1] == '\n' || line[len - 1] == '\r'))
            line[--len] = '\0';

        /* Validate format */
        if (len < 14) { bad++; continue; }
        if (line[0] != '0' || line[1] != 'x') { bad++; continue; }

        int valid = 1;
        for (int i = 2; i < 10; i++) {
            if (!is_hex_char(line[i])) { valid = 0; break; }
        }
        if (!valid) { bad++; continue; }
        if (line[10] != ' ' || line[11] != ':' || line[12] != ' ') { bad++; continue; }

        /* Parse hash value */
        uint32_t hash;
        if (!parse_hex8(line + 2, &hash)) { bad++; continue; }

        /* Check per-hash limit */
        pf_bucket_t *b = pf_ht_get(hash);
        if (b->count >= (uint32_t)max_per_hash) {
            capped++;
            continue;
        }
        b->count++;

        /* Write */
        fwrite(line, 1, len, fout);
        fputc('\n', fout);
        kept++;
    }

    fprintf(stderr, "\r%80s\r", "");
    fprintf(stderr, "Done: %zu read, %zu kept, %zu capped, %zu bad\n",
            total, kept, capped, bad);

    /* Stats */
    uint32_t hashes_seen = 0, hashes_capped = 0;
    for (uint32_t i = 0; i < PF_HT_SIZE; i++) {
        if (pf_ht[i].occupied) {
            hashes_seen++;
            if (pf_ht[i].count >= (uint32_t)max_per_hash)
                hashes_capped++;
        }
    }
    fprintf(stderr, "  Unique hashes: %u (%u hit cap of %d)\n",
            hashes_seen, hashes_capped, max_per_hash);

    fclose(fin);
    fclose(fout);
    free(ibuf);
    free(obuf);

    return 0;
}
