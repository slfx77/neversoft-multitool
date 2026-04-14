/*
 * common.h — Shared infrastructure for the QBKey pipeline.
 *
 * Unity-build header: #include'd by qbkey_pipeline.c before all cmd_*.c files.
 * Provides arena allocator, hash sets, CRC-32, directory walking, file I/O.
 *
 * Configurable sizes — #define before #include to override:
 *   ARENA_SIZE, STRSET_BITS, HSET_BITS
 */

#ifndef COMMON_H
#define COMMON_H

#include <stdio.h>
#include <stdlib.h>
#include <stdint.h>
#include <string.h>
#include <ctype.h>

#define WIN32_LEAN_AND_MEAN
#include <windows.h>

/* ========================================================================= */
/* Configuration defaults                                                    */
/* ========================================================================= */

#ifndef ARENA_SIZE
#define ARENA_SIZE      (256 * 1024 * 1024)  /* 256 MB */
#endif

#ifndef STRSET_BITS
#define STRSET_BITS     22                   /* 4M buckets */
#endif
#define STRSET_SIZE     (1u << STRSET_BITS)
#define STRSET_MASK     (STRSET_SIZE - 1)

#ifndef HSET_BITS
#define HSET_BITS       17                   /* 128K buckets */
#endif
#define HSET_SIZE       (1u << HSET_BITS)
#define HSET_MASK       (HSET_SIZE - 1)

#define MAX_NAME_LEN    256
#define BUILDS_DEFAULT  "C:\\Users\\mmc99\\Desktop\\Games\\TCRF\\Spider-Man Research\\Builds"

/* ========================================================================= */
/* Performance timer                                                         */
/* ========================================================================= */

static double get_time(void)
{
    static LARGE_INTEGER freq = {0};
    LARGE_INTEGER counter;
    if (freq.QuadPart == 0) QueryPerformanceFrequency(&freq);
    QueryPerformanceCounter(&counter);
    return (double)counter.QuadPart / (double)freq.QuadPart;
}

/* ========================================================================= */
/* Arena allocator                                                           */
/* ========================================================================= */

static char *g_arena = NULL;
static size_t g_arena_used = 0;

static void arena_init(void)
{
    g_arena = (char *)malloc(ARENA_SIZE);
    if (!g_arena) {
        fprintf(stderr, "Failed to allocate %d MB arena\n", ARENA_SIZE / (1024*1024));
        exit(1);
    }
    g_arena_used = 0;
}

static char *arena_alloc(size_t size)
{
    if (g_arena_used + size > ARENA_SIZE) {
        fprintf(stderr, "Arena overflow at %zu MB\n", g_arena_used / (1024*1024));
        exit(1);
    }
    char *p = g_arena + g_arena_used;
    g_arena_used += size;
    return p;
}

static char *arena_strdup(const char *s)
{
    size_t len = strlen(s) + 1;
    char *p = arena_alloc(len);
    memcpy(p, s, len);
    return p;
}

static char *arena_strndup(const char *s, size_t n)
{
    char *p = arena_alloc(n + 1);
    memcpy(p, s, n);
    p[n] = '\0';
    return p;
}

/* ========================================================================= */
/* FNV-1a hash                                                               */
/* ========================================================================= */

static uint32_t fnv1a(const char *s)
{
    uint32_t h = 2166136261u;
    while (*s) {
        h ^= (uint8_t)*s++;
        h *= 16777619u;
    }
    return h;
}

/* ========================================================================= */
/* String set (open-addressing hash table)                                   */
/* ========================================================================= */

typedef struct {
    const char **buckets;
    uint32_t count;
    uint32_t capacity;
    uint32_t mask;
} strset_t;

static void strset_init(strset_t *ss)
{
    ss->capacity = STRSET_SIZE;
    ss->mask = STRSET_MASK;
    ss->buckets = (const char **)calloc(ss->capacity, sizeof(const char *));
    ss->count = 0;
}

static void strset_init_sized(strset_t *ss, uint32_t bits)
{
    ss->capacity = 1u << bits;
    ss->mask = ss->capacity - 1;
    ss->buckets = (const char **)calloc(ss->capacity, sizeof(const char *));
    ss->count = 0;
}

static int strset_contains(const strset_t *ss, const char *s)
{
    uint32_t idx = fnv1a(s) & ss->mask;
    while (ss->buckets[idx]) {
        if (strcmp(ss->buckets[idx], s) == 0)
            return 1;
        idx = (idx + 1) & ss->mask;
    }
    return 0;
}

/* Returns 1 if newly inserted, 0 if already present */
static int strset_add(strset_t *ss, const char *s)
{
    uint32_t idx = fnv1a(s) & ss->mask;
    while (ss->buckets[idx]) {
        if (strcmp(ss->buckets[idx], s) == 0)
            return 0;
        idx = (idx + 1) & ss->mask;
    }
    ss->buckets[idx] = s;
    ss->count++;
    return 1;
}

/* Intern: returns existing pointer or stores new copy in arena */
static const char *strset_intern(strset_t *ss, const char *s)
{
    uint32_t idx = fnv1a(s) & ss->mask;
    while (ss->buckets[idx]) {
        if (strcmp(ss->buckets[idx], s) == 0)
            return ss->buckets[idx];
        idx = (idx + 1) & ss->mask;
    }
    const char *copy = arena_strdup(s);
    ss->buckets[idx] = copy;
    ss->count++;
    return copy;
}

static void strset_free(strset_t *ss)
{
    free(ss->buckets);
    ss->buckets = NULL;
    ss->count = 0;
}

/* ========================================================================= */
/* Hash set (uint32_t keys, open-addressing)                                 */
/* ========================================================================= */

typedef struct {
    uint32_t *keys;
    uint8_t *occupied;
    uint32_t count;
    uint32_t capacity;
    uint32_t mask;
} hashset_t;

static void hashset_init(hashset_t *hs)
{
    hs->capacity = HSET_SIZE;
    hs->mask = HSET_MASK;
    hs->keys = (uint32_t *)calloc(hs->capacity, sizeof(uint32_t));
    hs->occupied = (uint8_t *)calloc(hs->capacity, 1);
    hs->count = 0;
}

static void hashset_init_sized(hashset_t *hs, uint32_t bits)
{
    hs->capacity = 1u << bits;
    hs->mask = hs->capacity - 1;
    hs->keys = (uint32_t *)calloc(hs->capacity, sizeof(uint32_t));
    hs->occupied = (uint8_t *)calloc(hs->capacity, 1);
    hs->count = 0;
}

static int hashset_contains(const hashset_t *hs, uint32_t key)
{
    uint32_t idx = key & hs->mask;
    while (hs->occupied[idx]) {
        if (hs->keys[idx] == key) return 1;
        idx = (idx + 1) & hs->mask;
    }
    return 0;
}

static int hashset_add(hashset_t *hs, uint32_t key)
{
    uint32_t idx = key & hs->mask;
    while (hs->occupied[idx]) {
        if (hs->keys[idx] == key) return 0;
        idx = (idx + 1) & hs->mask;
    }
    hs->keys[idx] = key;
    hs->occupied[idx] = 1;
    hs->count++;
    return 1;
}

static void hashset_free(hashset_t *hs)
{
    free(hs->keys);
    free(hs->occupied);
    hs->keys = NULL;
    hs->occupied = NULL;
    hs->count = 0;
}

/* ========================================================================= */
/* CRC-32 tables                                                             */
/* ========================================================================= */

static uint32_t g_crc_table[256];
static int g_crc_table_init = 0;

static void init_crc_table(void)
{
    if (g_crc_table_init) return;
    for (int i = 0; i < 256; i++) {
        uint32_t crc = (uint32_t)i;
        for (int j = 0; j < 8; j++) {
            if (crc & 1)
                crc = (crc >> 1) ^ 0xEDB88320u;
            else
                crc >>= 1;
        }
        g_crc_table[i] = crc;
    }
    g_crc_table_init = 1;
}

/* QBKey: reflected CRC-32, init 0xFFFFFFFF, NO final XOR, lowercase input */
static uint32_t qbkey(const char *name)
{
    uint32_t crc = 0xFFFFFFFF;
    for (int i = 0; name[i]; i++) {
        uint8_t ch = (uint8_t)name[i];
        if (ch >= 'A' && ch <= 'Z') ch += 32;
        crc = (crc >> 8) ^ g_crc_table[(crc ^ ch) & 0xFF];
    }
    return crc;
}

/* Crc32Neversoft: rotate-left CRC variant for HED filename hashes */
static uint32_t crc32_neversoft(const char *name)
{
    uint32_t result = 0xFFFFFFFF;
    for (int i = 0; name[i]; i++) {
        uint32_t mask = result ^ (uint8_t)name[i];
        for (int j = 0; j < 8; j++) {
            result = (result << 1) | (result >> 31);
            if (mask & 1)
                result ^= 0xEDB88320u;
            mask >>= 1;
        }
    }
    return result;
}

/* ========================================================================= */
/* Hex parsing                                                               */
/* ========================================================================= */

static int is_hex_char(char c)
{
    return (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F') || (c >= 'a' && c <= 'f');
}

/* Parse 8 hex digits, return 1 on success */
static int parse_hex8(const char *s, uint32_t *out)
{
    uint32_t v = 0;
    for (int i = 0; i < 8; i++) {
        char c = s[i];
        if (c >= '0' && c <= '9')      v = (v << 4) | (c - '0');
        else if (c >= 'A' && c <= 'F') v = (v << 4) | (c - 'A' + 10);
        else if (c >= 'a' && c <= 'f') v = (v << 4) | (c - 'a' + 10);
        else return 0;
    }
    *out = v;
    return 1;
}

/* ========================================================================= */
/* File I/O                                                                  */
/* ========================================================================= */

/* Read entire file into malloc'd buffer, null-terminated */
static char *read_file(const char *path, size_t *out_size)
{
    FILE *f = fopen(path, "rb");
    if (!f) {
        fprintf(stderr, "Cannot open: %s\n", path);
        exit(1);
    }
    fseek(f, 0, SEEK_END);
    size_t sz = ftell(f);
    fseek(f, 0, SEEK_SET);
    char *buf = (char *)malloc(sz + 1);
    fread(buf, 1, sz, f);
    buf[sz] = '\0';
    fclose(f);
    if (out_size) *out_size = sz;
    return buf;
}

/* Read entire file into malloc'd buffer, return NULL on failure (no exit) */
static uint8_t *read_file_bin(const char *path, size_t *out_size)
{
    FILE *f = fopen(path, "rb");
    if (!f) return NULL;
    fseek(f, 0, SEEK_END);
    size_t sz = ftell(f);
    fseek(f, 0, SEEK_SET);
    uint8_t *buf = (uint8_t *)malloc(sz);
    if (buf) fread(buf, 1, sz, f);
    fclose(f);
    if (out_size) *out_size = sz;
    return buf;
}

/* ========================================================================= */
/* JSON helpers                                                              */
/* ========================================================================= */

static void json_escape_string(FILE *f, const char *s)
{
    fputc('"', f);
    for (; *s; s++) {
        if (*s == '"') fputs("\\\"", f);
        else if (*s == '\\') fputs("\\\\", f);
        else if (*s == '\n') fputs("\\n", f);
        else fputc(*s, f);
    }
    fputc('"', f);
}

/* ========================================================================= */
/* Directory walker (Windows API)                                            */
/* ========================================================================= */

typedef void (*file_callback_t)(const char *filepath, const char *filename, void *ctx);

static void walk_directory(const char *dir, file_callback_t callback, void *ctx)
{
    char pattern[MAX_PATH * 2];
    snprintf(pattern, sizeof(pattern), "%s\\*", dir);

    WIN32_FIND_DATAA fd;
    HANDLE hFind = FindFirstFileA(pattern, &fd);
    if (hFind == INVALID_HANDLE_VALUE) return;

    do {
        if (fd.cFileName[0] == '.' &&
            (fd.cFileName[1] == '\0' ||
             (fd.cFileName[1] == '.' && fd.cFileName[2] == '\0')))
            continue;

        char fullpath[MAX_PATH * 2];
        snprintf(fullpath, sizeof(fullpath), "%s\\%s", dir, fd.cFileName);

        if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) {
            walk_directory(fullpath, callback, ctx);
        } else {
            callback(fullpath, fd.cFileName, ctx);
        }
    } while (FindNextFileA(hFind, &fd));

    FindClose(hFind);
}

/* ========================================================================= */
/* String utilities                                                          */
/* ========================================================================= */

/* Extract basename from a path (after last \ or /) */
static const char *path_basename(const char *path)
{
    const char *last = path;
    for (const char *p = path; *p; p++) {
        if (*p == '\\' || *p == '/')
            last = p + 1;
    }
    return last;
}

/* Extract file stem (name without extension) into buf, lowercase */
static void path_stem_lower(const char *filename, char *buf, size_t bufsize)
{
    const char *dot = strrchr(filename, '.');
    size_t len = dot ? (size_t)(dot - filename) : strlen(filename);
    if (len >= bufsize) len = bufsize - 1;
    for (size_t i = 0; i < len; i++)
        buf[i] = (char)tolower((unsigned char)filename[i]);
    buf[len] = '\0';
}

/* Lowercase a string in-place */
static void str_tolower(char *s)
{
    for (; *s; s++)
        *s = (char)tolower((unsigned char)*s);
}

/* Sort comparator for uint32_t */
static int uint32_cmp(const void *a, const void *b)
{
    uint32_t va = *(const uint32_t *)a;
    uint32_t vb = *(const uint32_t *)b;
    if (va < vb) return -1;
    if (va > vb) return 1;
    return 0;
}

/* Sort comparator for const char* (strcmp) */
static int str_cmp(const void *a, const void *b)
{
    return strcmp(*(const char **)a, *(const char **)b);
}

/* Resolve tools directory from executable path */
static void get_tools_dir(char *out, size_t outsize)
{
    if (GetModuleFileNameA(NULL, out, (DWORD)outsize)) {
        char *last_sep = strrchr(out, '\\');
        if (last_sep) *last_sep = '\0';
    } else {
        GetCurrentDirectoryA((DWORD)outsize, out);
    }
}

#endif /* COMMON_H */
