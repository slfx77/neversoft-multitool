/*
 * cmd_filter.c — Filter brute-force results to names of length 1-maxlen.
 *
 * Usage: qbkey_pipeline filter [-m maxlen] input.txt output.txt
 *   Default maxlen = 9
 *
 * Reads lines of format "0xHHHHHHHH : name\n", keeps only valid lines where
 * strlen(name) <= maxlen.
 */

static int cmd_filter(int argc, char **argv)
{
    int maxlen = 9;
    const char *infile = NULL, *outfile = NULL;

    for (int i = 1; i < argc; i++) {
        if (strcmp(argv[i], "-m") == 0 && i + 1 < argc) {
            maxlen = atoi(argv[++i]);
        } else if (!infile) {
            infile = argv[i];
        } else if (!outfile) {
            outfile = argv[i];
        }
    }

    if (!infile || !outfile) {
        fprintf(stderr, "Usage: qbkey_pipeline filter [-m maxlen] input.txt output.txt\n");
        return 1;
    }

    fprintf(stderr, "Filtering %s -> %s (max name length: %d)\n", infile, outfile, maxlen);

    FILE *fin = fopen(infile, "rb");
    if (!fin) { perror(infile); return 1; }

    FILE *fout = fopen(outfile, "wb");
    if (!fout) { perror(outfile); return 1; }

    char *ibuf = (char *)malloc(1 << 20);
    char *obuf = (char *)malloc(1 << 20);
    setvbuf(fin, ibuf, _IOFBF, 1 << 20);
    setvbuf(fout, obuf, _IOFBF, 1 << 20);

    char line[512];
    size_t total = 0, kept = 0, skipped = 0;

    while (fgets(line, sizeof(line), fin)) {
        total++;

        if ((total & 0x00FFFFFF) == 0) {
            fprintf(stderr, "\r  %zuM lines read, %zuM kept, %zuM skipped",
                    total / 1000000, kept / 1000000, skipped / 1000000);
        }

        /* Strip trailing newline/CR */
        size_t len = strlen(line);
        while (len > 0 && (line[len - 1] == '\n' || line[len - 1] == '\r'))
            line[--len] = '\0';

        /* Validate: "0xHHHHHHHH : name" = minimum 14 chars (1-char name) */
        if (len < 14) { skipped++; continue; }
        if (line[0] != '0' || line[1] != 'x') { skipped++; continue; }

        /* Quick check: 8 hex digits */
        int valid = 1;
        for (int i = 2; i < 10; i++) {
            if (!is_hex_char(line[i])) { valid = 0; break; }
        }
        if (!valid) { skipped++; continue; }

        /* " : " separator */
        if (line[10] != ' ' || line[11] != ':' || line[12] != ' ') { skipped++; continue; }

        /* Name length check */
        size_t namelen = len - 13;
        if (namelen < 1 || namelen > (size_t)maxlen) { skipped++; continue; }

        /* Write line + newline */
        fwrite(line, 1, len, fout);
        fputc('\n', fout);
        kept++;
    }

    fprintf(stderr, "\r%80s\r", "");
    fprintf(stderr, "Done: %zu lines read, %zu kept, %zu skipped\n", total, kept, skipped);

    fclose(fin);
    fclose(fout);
    free(ibuf);
    free(obuf);

    return 0;
}
