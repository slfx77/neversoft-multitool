/*
 * cmd_brute_gpu.c — GPU-accelerated brute-force + MITM reversal of CRC-32 hashes.
 *
 * Requires OpenCL. Compile with:
 *   -DHAS_OPENCL -DCL_TARGET_OPENCL_VERSION=120
 *   -I"<CUDA_PATH>/include" -L"<CUDA_PATH>/lib/x64" -lOpenCL
 *
 * Part of qbkey_pipeline unified CLI.
 */

#ifdef HAS_OPENCL

#ifndef CL_TARGET_OPENCL_VERSION
#define CL_TARGET_OPENCL_VERSION 120
#endif
#include <CL/cl.h>

/* ========================================================================= */
/* OpenCL error checking                                                     */
/* ========================================================================= */

static const char *bgpu_cl_error_string(cl_int err)
{
    switch (err) {
        case CL_SUCCESS: return "CL_SUCCESS";
        case CL_DEVICE_NOT_FOUND: return "CL_DEVICE_NOT_FOUND";
        case CL_BUILD_PROGRAM_FAILURE: return "CL_BUILD_PROGRAM_FAILURE";
        case CL_OUT_OF_RESOURCES: return "CL_OUT_OF_RESOURCES";
        case CL_OUT_OF_HOST_MEMORY: return "CL_OUT_OF_HOST_MEMORY";
        case CL_MEM_OBJECT_ALLOCATION_FAILURE: return "CL_MEM_OBJECT_ALLOCATION_FAILURE";
        case CL_INVALID_VALUE: return "CL_INVALID_VALUE";
        case CL_INVALID_KERNEL_ARGS: return "CL_INVALID_KERNEL_ARGS";
        case CL_INVALID_WORK_GROUP_SIZE: return "CL_INVALID_WORK_GROUP_SIZE";
        case CL_INVALID_WORK_ITEM_SIZE: return "CL_INVALID_WORK_ITEM_SIZE";
        default: return "UNKNOWN";
    }
}

#define BGPU_CL_CHECK(call) do { \
    cl_int _err = (call); \
    if (_err != CL_SUCCESS) { \
        fprintf(stderr, "OpenCL error %d (%s) at %s:%d\n", \
                _err, bgpu_cl_error_string(_err), __FILE__, __LINE__); \
        exit(1); \
    } \
} while(0)

/* ========================================================================= */
/* CRC-32 inverse table (forward table is g_crc_table from common.h)         */
/* ========================================================================= */

static uint32_t bgpu_crc_inv_table[256];

static void bgpu_init_crc_inv_table(void)
{
    init_crc_table(); /* ensure g_crc_table is populated */
    for (int i = 0; i < 256; i++) {
        uint32_t top_byte = g_crc_table[i] >> 24;
        bgpu_crc_inv_table[top_byte] = (uint32_t)i;
    }
}

static uint32_t bgpu_compute_crc32(const char *str)
{
    uint32_t crc = 0xFFFFFFFF;
    for (int i = 0; str[i]; i++)
        crc = (crc >> 8) ^ g_crc_table[(crc ^ (uint8_t)str[i]) & 0xFF];
    return crc;
}

static inline uint32_t bgpu_crc_unstep(uint32_t crc_next, uint8_t byte)
{
    uint32_t index = bgpu_crc_inv_table[crc_next >> 24];
    return ((crc_next ^ g_crc_table[index]) << 8) | (index ^ byte);
}

/* ========================================================================= */
/* Character set                                                             */
/* ========================================================================= */

static uint8_t bgpu_charset[37];
static int bgpu_charset_size = 0;
static int bgpu_include_digits = 0;
static char bgpu_fixed_prefix[MAX_NAME_LEN] = "";
static int bgpu_fixed_prefix_len = 0;
static char bgpu_fixed_suffix[MAX_NAME_LEN] = "";
static int bgpu_fixed_suffix_len = 0;
static uint32_t bgpu_initial_crc = 0xFFFFFFFF;
static size_t bgpu_brute_local_size = 256;
static uint32_t bgpu_brute_items_per_work_item = 1;
static size_t bgpu_brute_kernel_max_work_group_size = 0;
static size_t bgpu_brute_kernel_preferred_wg_multiple = 0;

static void bgpu_init_charset(void)
{
    bgpu_charset_size = 0;
    for (int c = 'a'; c <= 'z'; c++)
        bgpu_charset[bgpu_charset_size++] = (uint8_t)c;
    if (bgpu_include_digits) {
        for (int c = '0'; c <= '9'; c++)
            bgpu_charset[bgpu_charset_size++] = (uint8_t)c;
    }
    bgpu_charset[bgpu_charset_size++] = '_';
}

static int bgpu_first_char_count(void)
{
    return bgpu_charset_size - 1;
}

static int bgpu_variable_first_char_count(void)
{
    return bgpu_fixed_prefix_len > 0 ? bgpu_charset_size : bgpu_first_char_count();
}

static inline uint32_t bgpu_crc_step(uint32_t crc, uint8_t byte)
{
    return (crc >> 8) ^ g_crc_table[(crc ^ byte) & 0xFF];
}

static uint32_t bgpu_adjust_target_for_suffix(uint32_t hash)
{
    uint32_t adjusted = hash;
    for (int i = bgpu_fixed_suffix_len - 1; i >= 0; i--)
        adjusted = bgpu_crc_unstep(adjusted, (uint8_t)bgpu_fixed_suffix[i]);
    return adjusted;
}

static uint32_t bgpu_compute_crc32_with_suffix(const char *body)
{
    uint32_t crc = 0xFFFFFFFF;
    for (int i = 0; i < bgpu_fixed_prefix_len; i++)
        crc = bgpu_crc_step(crc, (uint8_t)bgpu_fixed_prefix[i]);
    for (int i = 0; body[i]; i++)
        crc = bgpu_crc_step(crc, (uint8_t)body[i]);
    for (int i = 0; i < bgpu_fixed_suffix_len; i++)
        crc = bgpu_crc_step(crc, (uint8_t)bgpu_fixed_suffix[i]);
    return crc;
}

static void bgpu_build_full_name(char *out, size_t out_size, const char *body)
{
    if (out_size == 0)
        return;

    size_t fixed_prefix_copy = (size_t)bgpu_fixed_prefix_len;
    if (fixed_prefix_copy > out_size - 1)
        fixed_prefix_copy = out_size - 1;
    memcpy(out, bgpu_fixed_prefix, fixed_prefix_copy);

    size_t prefix_len = strlen(body);
    if (fixed_prefix_copy + prefix_len > out_size - 1)
        prefix_len = out_size - 1 - fixed_prefix_copy;
    memcpy(out + fixed_prefix_copy, body, prefix_len);

    size_t suffix_copy = (size_t)bgpu_fixed_suffix_len;
    if (fixed_prefix_copy + prefix_len + suffix_copy > out_size - 1)
        suffix_copy = out_size - 1 - fixed_prefix_copy - prefix_len;
    memcpy(out + fixed_prefix_copy + prefix_len, bgpu_fixed_suffix, suffix_copy);
    out[fixed_prefix_copy + prefix_len + suffix_copy] = '\0';
}

/* ========================================================================= */
/* Target hash loading                                                       */
/* ========================================================================= */

#define BGPU_MAX_TARGETS 131072

static uint32_t bgpu_targets[BGPU_MAX_TARGETS];
static uint32_t bgpu_match_targets[BGPU_MAX_TARGETS];
static int bgpu_target_count = 0;
static int bgpu_target_found[BGPU_MAX_TARGETS];

static int bgpu_parse_hash(const char *str, uint32_t *out)
{
    char *end;
    unsigned long val = strtoul(str, &end, 16);
    if (end == str || (*end != '\0' && *end != '\n' && *end != '\r'))
        return 0;
    *out = (uint32_t)val;
    return 1;
}

static int bgpu_load_hashes_from_file(const char *path)
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
        if (bgpu_parse_hash(line, &h) && bgpu_target_count < BGPU_MAX_TARGETS) {
            bgpu_targets[bgpu_target_count] = h;
            bgpu_target_found[bgpu_target_count] = 0;
            bgpu_target_count++;
        }
    }
    fclose(f);
    return 1;
}

static int bgpu_uint32_cmp(const void *a, const void *b)
{
    uint32_t va = *(const uint32_t *)a;
    uint32_t vb = *(const uint32_t *)b;
    if (va < vb) return -1;
    if (va > vb) return 1;
    return 0;
}

/* Sorted copy for GPU lookup */
static uint32_t bgpu_sorted_targets[BGPU_MAX_TARGETS];
static int bgpu_sorted_target_count = 0;

/* Bucket acceleration for target lookup in brute-force kernel */
#define BGPU_NUM_BUCKETS 65537
static uint32_t bgpu_bucket_starts[BGPU_NUM_BUCKETS];

static void bgpu_sort_targets(void)
{
    memcpy(bgpu_sorted_targets, bgpu_match_targets,
           bgpu_target_count * sizeof(uint32_t));
    qsort(bgpu_sorted_targets, bgpu_target_count,
          sizeof(uint32_t), bgpu_uint32_cmp);
    bgpu_sorted_target_count = 0;
    for (int i = 0; i < bgpu_target_count; i++) {
        if (bgpu_sorted_target_count == 0 ||
            bgpu_sorted_targets[i] != bgpu_sorted_targets[bgpu_sorted_target_count - 1])
            bgpu_sorted_targets[bgpu_sorted_target_count++] = bgpu_sorted_targets[i];
    }

    int ti = 0;
    for (int b = 0; b < BGPU_NUM_BUCKETS; b++) {
        while (ti < bgpu_sorted_target_count &&
               (bgpu_sorted_targets[ti] >> 16) < (uint32_t)b)
            ti++;
        bgpu_bucket_starts[b] = (uint32_t)ti;
    }
}

static int bgpu_find_target_index_by_adjusted(uint32_t adjusted_hash)
{
    for (int i = 0; i < bgpu_target_count; i++) {
        if (bgpu_match_targets[i] == adjusted_hash)
            return i;
    }
    return -1;
}

static void bgpu_mark_target_found_index(int target_idx)
{
    if (target_idx >= 0 && target_idx < bgpu_target_count)
        bgpu_target_found[target_idx]++;
}

static uint64_t bgpu_candidate_count_for_length(int str_len)
{
    uint64_t total_strings = (uint64_t)bgpu_variable_first_char_count();
    for (int i = 1; i < str_len; i++)
        total_strings *= (uint64_t)bgpu_charset_size;
    return total_strings;
}

/* ========================================================================= */
/* String reconstruction from index                                          */
/* ========================================================================= */

static void bgpu_index_to_string(uint64_t idx, int str_len, char *out)
{
    for (int p = str_len - 1; p >= 1; p--) {
        out[p] = (char)bgpu_charset[idx % bgpu_charset_size];
        idx /= bgpu_charset_size;
    }
    out[0] = (char)bgpu_charset[idx];
    out[str_len] = '\0';
}

static void bgpu_index_to_string_suffix(uint64_t idx, int str_len, char *out)
{
    for (int p = str_len - 1; p >= 0; p--) {
        out[p] = (char)bgpu_charset[idx % bgpu_charset_size];
        idx /= bgpu_charset_size;
    }
    out[str_len] = '\0';
}

/* ========================================================================= */
/* MITM precomputation (host side)                                           */
/* ========================================================================= */

#define BGPU_MAX_SUFFIX_LEN 8

static uint32_t bgpu_g_tables[BGPU_MAX_SUFFIX_LEN][256];

static void bgpu_build_g_tables(int slen)
{
    for (int p = 0; p < slen; p++) {
        for (int b = 0; b < 256; b++) {
            uint32_t v = g_crc_table[b];
            for (int i = 0; i <= p; i++)
                v = bgpu_crc_unstep(v, 0);
            bgpu_g_tables[p][b] = v;
        }
    }
}

static uint32_t bgpu_adj_targets_buf[BGPU_MAX_TARGETS];

static void bgpu_compute_adjusted_targets(int slen)
{
    for (int i = 0; i < bgpu_sorted_target_count; i++) {
        uint32_t v = bgpu_sorted_targets[i];
        for (int j = 0; j < slen; j++)
            v = bgpu_crc_unstep(v, 0);
        bgpu_adj_targets_buf[i] = v;
    }
}

static uint32_t *bgpu_prefix_bucket_starts = NULL;

static void bgpu_build_prefix_buckets(uint32_t *sorted_crcs, size_t count)
{
    if (!bgpu_prefix_bucket_starts)
        bgpu_prefix_bucket_starts = malloc(BGPU_NUM_BUCKETS * sizeof(uint32_t));

    size_t ti = 0;
    for (int b = 0; b < BGPU_NUM_BUCKETS; b++) {
        while (ti < count && (sorted_crcs[ti] >> 16) < (uint32_t)b)
            ti++;
        bgpu_prefix_bucket_starts[b] = (uint32_t)ti;
    }
}

static int bgpu_uint64_cmp(const void *a, const void *b)
{
    uint64_t va = *(const uint64_t *)a;
    uint64_t vb = *(const uint64_t *)b;
    if (va < vb) return -1;
    if (va > vb) return 1;
    return 0;
}

/* ========================================================================= */
/* OpenCL kernel sources                                                     */
/* ========================================================================= */

static const char *bgpu_kernel_source_fast =
"__kernel void crc32_brute(\n"
"    __constant uint *crc_table,\n"
"    __global const uint *targets,\n"
"    const uint target_count,\n"
"    __global const uchar *charset,\n"
"    const uint charset_size,\n"
"    const uint first_char_count,\n"
"    const uint initial_crc,\n"
"    const uint str_len,\n"
"    const ulong batch_offset,\n"
"    const ulong candidate_count,\n"
"    const uint items_per_work_item,\n"
"    __global uint *match_hashes,\n"
"    __global uint *match_indices,\n"
"    __global volatile uint *match_count,\n"
"    const ulong div_magic,\n"
"    __global const uint *buckets\n"
") {\n"
"    ulong gid = get_global_id(0);\n"
"    ulong rel_base = gid * (ulong)items_per_work_item;\n"
"    if (rel_base >= candidate_count)\n"
"        return;\n"
"\n"
"    for (uint item = 0; item < items_per_work_item; item++) {\n"
"        ulong rel_idx = rel_base + (ulong)item;\n"
"        if (rel_idx >= candidate_count)\n"
"            break;\n"
"        ulong idx = batch_offset + rel_idx;\n"
"\n"
"        uchar buf[16];\n"
"        ulong remaining = idx;\n"
"        for (int p = str_len - 1; p >= 1; p--) {\n"
"            ulong q = mul_hi(remaining, div_magic);\n"
"            ulong r = remaining - q * (ulong)charset_size;\n"
"            buf[p] = charset[r];\n"
"            remaining = q;\n"
"        }\n"
"        if (remaining >= first_char_count)\n"
"            continue;\n"
"        buf[0] = charset[remaining];\n"
"\n"
"        uint crc = initial_crc;\n"
"        for (uint i = 0; i < str_len; i++) {\n"
"            crc = (crc >> 8) ^ crc_table[(crc ^ buf[i]) & 0xFF];\n"
"        }\n"
"\n"
"        uint prefix = crc >> 16;\n"
"        uint lo = buckets[prefix];\n"
"        uint hi = buckets[prefix + 1];\n"
"        for (uint i = lo; i < hi; i++) {\n"
"            if (targets[i] == crc) {\n"
"                uint slot = atomic_inc(match_count);\n"
"                if (slot < 4096u) {\n"
"                    match_hashes[slot] = crc;\n"
"                    match_indices[slot] = (uint)rel_idx;\n"
"                }\n"
"                break;\n"
"            }\n"
"        }\n"
"    }\n"
"}\n";

static const char *bgpu_kernel_mitm_prefix_source =
"__kernel void crc32_mitm_prefix(\n"
"    __constant uint *crc_table,\n"
"    __global const uchar *charset,\n"
"    const uint charset_size,\n"
"    const uint first_char_count,\n"
"    const uint plen,\n"
"    const uint initial_crc,\n"
"    const ulong batch_offset,\n"
"    __global uint *prefix_crcs,\n"
"    const ulong div_magic\n"
") {\n"
"    ulong gid = get_global_id(0);\n"
"    ulong prefix_idx = batch_offset + gid;\n"
"\n"
"    uchar buf[8];\n"
"    ulong remaining = prefix_idx;\n"
"    for (int p = plen - 1; p >= 1; p--) {\n"
"        ulong q = mul_hi(remaining, div_magic);\n"
"        ulong r = remaining - q * (ulong)charset_size;\n"
"        buf[p] = charset[r];\n"
"        remaining = q;\n"
"    }\n"
"    if (remaining >= first_char_count)\n"
"        return;\n"
"    buf[0] = charset[remaining];\n"
"\n"
"    uint crc = initial_crc;\n"
"    for (uint i = 0; i < plen; i++)\n"
"        crc = (crc >> 8) ^ crc_table[(crc ^ buf[i]) & 0xFF];\n"
"\n"
"    prefix_crcs[prefix_idx] = crc;\n"
"}\n";

static const char *bgpu_kernel_mitm_g_suffix_source =
"__kernel void crc32_mitm_g_suffix(\n"
"    __constant uint *g_tables,\n"
"    __global const uchar *charset,\n"
"    const uint charset_size,\n"
"    const uint slen,\n"
"    const ulong batch_offset,\n"
"    __global uint *g_values,\n"
"    const ulong div_magic\n"
") {\n"
"    ulong gid = get_global_id(0);\n"
"    ulong suffix_idx = batch_offset + gid;\n"
"\n"
"    uchar buf[8];\n"
"    ulong remaining = suffix_idx;\n"
"    for (int p = slen - 1; p >= 0; p--) {\n"
"        ulong q = mul_hi(remaining, div_magic);\n"
"        ulong r = remaining - q * (ulong)charset_size;\n"
"        buf[p] = charset[r];\n"
"        remaining = q;\n"
"    }\n"
"    if (remaining > 0) return;\n"
"\n"
"    uint g = 0;\n"
"    for (uint p = 0; p < slen; p++)\n"
"        g ^= g_tables[p * 256 + buf[p]];\n"
"\n"
"    g_values[suffix_idx] = g;\n"
"}\n";

static const char *bgpu_kernel_mitm_match_source =
"__kernel void crc32_mitm_match(\n"
"    __global const uint *g_values,\n"
"    __global const uint *adj_targets,\n"
"    const uint target_count,\n"
"    __global const uint *prefix_crcs,\n"
"    __global const uint *prefix_buckets,\n"
"    const ulong suffix_batch_offset,\n"
"    __global uint *match_target_ids,\n"
"    __global uint *match_prefix_ids,\n"
"    __global uint *match_suffix_ids,\n"
"    __global volatile uint *match_count,\n"
"    const uint max_matches\n"
") {\n"
"    uint suffix_local = get_global_id(0);\n"
"    uint target_idx = get_global_id(1);\n"
"    if (target_idx >= target_count) return;\n"
"\n"
"    ulong suffix_idx = suffix_batch_offset + (ulong)suffix_local;\n"
"    uint needed = adj_targets[target_idx] ^ g_values[suffix_idx];\n"
"\n"
"    uint pb = needed >> 16;\n"
"    uint lo = prefix_buckets[pb];\n"
"    uint hi = prefix_buckets[pb + 1];\n"
"    for (uint i = lo; i < hi; i++) {\n"
"        if (prefix_crcs[i] == needed) {\n"
"            uint slot = atomic_inc(match_count);\n"
"            if (slot < max_matches) {\n"
"                match_target_ids[slot] = target_idx;\n"
"                match_prefix_ids[slot] = i;\n"
"                match_suffix_ids[slot] = suffix_local;\n"
"            }\n"
"        }\n"
"    }\n"
"}\n";

/* ========================================================================= */
/* OpenCL state                                                              */
/* ========================================================================= */

#define BGPU_MAX_MATCHES_PER_BATCH 4096
#define BGPU_MITM_MAX_MATCHES (4 * 1024 * 1024)

static cl_platform_id bgpu_platform;
static cl_device_id bgpu_device;
static cl_context bgpu_context;
static cl_command_queue bgpu_queue;

static cl_program bgpu_program_brute;
static cl_kernel bgpu_kernel_brute;
static cl_mem bgpu_buf_crc_table;
static cl_mem bgpu_buf_targets;
static cl_mem bgpu_buf_charset;
static cl_mem bgpu_buf_match_hashes;
static cl_mem bgpu_buf_match_indices;
static cl_mem bgpu_buf_match_count;
static cl_mem bgpu_buf_buckets;

static cl_program bgpu_program_mitm_prefix;
static cl_program bgpu_program_mitm_g_suffix;
static cl_program bgpu_program_mitm_match;
static cl_kernel bgpu_kernel_mitm_prefix;
static cl_kernel bgpu_kernel_mitm_g_suffix;
static cl_kernel bgpu_kernel_mitm_match;

static cl_mem bgpu_buf_mitm_prefix_crcs = NULL;
static cl_mem bgpu_buf_mitm_prefix_buckets = NULL;
static cl_mem bgpu_buf_mitm_g_tables = NULL;
static cl_mem bgpu_buf_mitm_g_values = NULL;
static cl_mem bgpu_buf_mitm_adj_targets = NULL;
static cl_mem bgpu_buf_mitm_match_target_ids = NULL;
static cl_mem bgpu_buf_mitm_match_prefix_ids = NULL;
static cl_mem bgpu_buf_mitm_match_suffix_ids = NULL;
static cl_mem bgpu_buf_mitm_match_count = NULL;

static const char *bgpu_cl_build_opts = "-cl-mad-enable -cl-fast-relaxed-math";

static cl_program bgpu_build_program(const char *source, const char *name)
{
    cl_int err;
    cl_program prog = clCreateProgramWithSource(bgpu_context, 1, &source, NULL, &err);
    BGPU_CL_CHECK(err);

    err = clBuildProgram(prog, 1, &bgpu_device, bgpu_cl_build_opts, NULL, NULL);
    if (err != CL_SUCCESS) {
        size_t log_size;
        clGetProgramBuildInfo(prog, bgpu_device, CL_PROGRAM_BUILD_LOG,
                              0, NULL, &log_size);
        char *log = malloc(log_size + 1);
        clGetProgramBuildInfo(prog, bgpu_device, CL_PROGRAM_BUILD_LOG,
                              log_size, log, NULL);
        log[log_size] = '\0';
        fprintf(stderr, "Kernel '%s' build failed:\n%s\n", name, log);
        free(log);
        exit(1);
    }
    return prog;
}

static void bgpu_init_opencl(int need_mitm)
{
    cl_int err;
    static uint32_t bgpu_dummy_target = 0;

    cl_uint num_platforms;
    BGPU_CL_CHECK(clGetPlatformIDs(0, NULL, &num_platforms));
    cl_platform_id *platforms = malloc(num_platforms * sizeof(cl_platform_id));
    BGPU_CL_CHECK(clGetPlatformIDs(num_platforms, platforms, NULL));

    bgpu_platform = platforms[0];
    for (cl_uint i = 0; i < num_platforms; i++) {
        char name[256];
        clGetPlatformInfo(platforms[i], CL_PLATFORM_NAME, sizeof(name), name, NULL);
        if (strstr(name, "NVIDIA") || strstr(name, "nvidia")) {
            bgpu_platform = platforms[i];
            fprintf(stderr, "Platform: %s\n", name);
            break;
        }
        if (i == num_platforms - 1)
            fprintf(stderr, "Platform: %s\n", name);
    }
    free(platforms);

    err = clGetDeviceIDs(bgpu_platform, CL_DEVICE_TYPE_GPU, 1, &bgpu_device, NULL);
    if (err != CL_SUCCESS) {
        fprintf(stderr, "No GPU device found (error %d)\n", err);
        exit(1);
    }

    char dev_name[256];
    cl_uint compute_units;
    size_t max_wg_size;
    cl_ulong global_mem;
    clGetDeviceInfo(bgpu_device, CL_DEVICE_NAME, sizeof(dev_name), dev_name, NULL);
    clGetDeviceInfo(bgpu_device, CL_DEVICE_MAX_COMPUTE_UNITS,
                    sizeof(compute_units), &compute_units, NULL);
    clGetDeviceInfo(bgpu_device, CL_DEVICE_MAX_WORK_GROUP_SIZE,
                    sizeof(max_wg_size), &max_wg_size, NULL);
    clGetDeviceInfo(bgpu_device, CL_DEVICE_GLOBAL_MEM_SIZE,
                    sizeof(global_mem), &global_mem, NULL);
    fprintf(stderr, "Device: %s (%u CUs, %zu max WG, %.0f MB VRAM)\n",
            dev_name, compute_units, max_wg_size, global_mem / 1048576.0);

    bgpu_context = clCreateContext(NULL, 1, &bgpu_device, NULL, NULL, &err);
    BGPU_CL_CHECK(err);
    bgpu_queue = clCreateCommandQueue(bgpu_context, bgpu_device, 0, &err);
    BGPU_CL_CHECK(err);

    bgpu_program_brute = bgpu_build_program(bgpu_kernel_source_fast, "crc32_brute");
    bgpu_kernel_brute = clCreateKernel(bgpu_program_brute, "crc32_brute", &err);
    BGPU_CL_CHECK(err);
    BGPU_CL_CHECK(clGetKernelWorkGroupInfo(bgpu_kernel_brute, bgpu_device,
        CL_KERNEL_WORK_GROUP_SIZE, sizeof(bgpu_brute_kernel_max_work_group_size),
        &bgpu_brute_kernel_max_work_group_size, NULL));
    BGPU_CL_CHECK(clGetKernelWorkGroupInfo(bgpu_kernel_brute, bgpu_device,
        CL_KERNEL_PREFERRED_WORK_GROUP_SIZE_MULTIPLE,
        sizeof(bgpu_brute_kernel_preferred_wg_multiple),
        &bgpu_brute_kernel_preferred_wg_multiple, NULL));

    bgpu_buf_crc_table = clCreateBuffer(bgpu_context,
        CL_MEM_READ_ONLY | CL_MEM_COPY_HOST_PTR,
        256 * sizeof(uint32_t), g_crc_table, &err);
    BGPU_CL_CHECK(err);
    bgpu_buf_targets = clCreateBuffer(bgpu_context,
        CL_MEM_READ_ONLY | CL_MEM_COPY_HOST_PTR,
        (bgpu_sorted_target_count > 0 ? bgpu_sorted_target_count : 1) * sizeof(uint32_t),
        bgpu_sorted_target_count > 0 ? (void *)bgpu_sorted_targets
                                     : (void *)&bgpu_dummy_target,
        &err);
    BGPU_CL_CHECK(err);
    bgpu_buf_charset = clCreateBuffer(bgpu_context,
        CL_MEM_READ_ONLY | CL_MEM_COPY_HOST_PTR,
        bgpu_charset_size * sizeof(uint8_t), bgpu_charset, &err);
    BGPU_CL_CHECK(err);
    bgpu_buf_match_hashes = clCreateBuffer(bgpu_context, CL_MEM_WRITE_ONLY,
        BGPU_MAX_MATCHES_PER_BATCH * sizeof(uint32_t), NULL, &err);
    BGPU_CL_CHECK(err);
    bgpu_buf_match_indices = clCreateBuffer(bgpu_context, CL_MEM_WRITE_ONLY,
        BGPU_MAX_MATCHES_PER_BATCH * sizeof(uint32_t), NULL, &err);
    BGPU_CL_CHECK(err);
    bgpu_buf_match_count = clCreateBuffer(bgpu_context, CL_MEM_READ_WRITE,
        sizeof(uint32_t), NULL, &err);
    BGPU_CL_CHECK(err);
    bgpu_buf_buckets = clCreateBuffer(bgpu_context,
        CL_MEM_READ_ONLY | CL_MEM_COPY_HOST_PTR,
        BGPU_NUM_BUCKETS * sizeof(uint32_t), bgpu_bucket_starts, &err);
    BGPU_CL_CHECK(err);

    BGPU_CL_CHECK(clSetKernelArg(bgpu_kernel_brute, 0,
        sizeof(cl_mem), &bgpu_buf_crc_table));
    BGPU_CL_CHECK(clSetKernelArg(bgpu_kernel_brute, 1,
        sizeof(cl_mem), &bgpu_buf_targets));
    uint32_t tc = (uint32_t)bgpu_sorted_target_count;
    BGPU_CL_CHECK(clSetKernelArg(bgpu_kernel_brute, 2, sizeof(uint32_t), &tc));
    BGPU_CL_CHECK(clSetKernelArg(bgpu_kernel_brute, 3,
        sizeof(cl_mem), &bgpu_buf_charset));
    uint32_t cs = (uint32_t)bgpu_charset_size;
    BGPU_CL_CHECK(clSetKernelArg(bgpu_kernel_brute, 4, sizeof(uint32_t), &cs));
    uint32_t fcc = (uint32_t)bgpu_variable_first_char_count();
    BGPU_CL_CHECK(clSetKernelArg(bgpu_kernel_brute, 5, sizeof(uint32_t), &fcc));
    BGPU_CL_CHECK(clSetKernelArg(bgpu_kernel_brute, 6, sizeof(uint32_t), &bgpu_initial_crc));
    BGPU_CL_CHECK(clSetKernelArg(bgpu_kernel_brute, 11,
        sizeof(cl_mem), &bgpu_buf_match_hashes));
    BGPU_CL_CHECK(clSetKernelArg(bgpu_kernel_brute, 12,
        sizeof(cl_mem), &bgpu_buf_match_indices));
    BGPU_CL_CHECK(clSetKernelArg(bgpu_kernel_brute, 13,
        sizeof(cl_mem), &bgpu_buf_match_count));
    uint64_t div_magic = UINT64_MAX / (uint64_t)bgpu_charset_size + 1;
    BGPU_CL_CHECK(clSetKernelArg(bgpu_kernel_brute, 14,
        sizeof(uint64_t), &div_magic));
    BGPU_CL_CHECK(clSetKernelArg(bgpu_kernel_brute, 15,
        sizeof(cl_mem), &bgpu_buf_buckets));
    fprintf(stderr, "Division magic: 0x%016llX (for d=%d)\n",
            (unsigned long long)div_magic, bgpu_charset_size);
    fprintf(stderr, "Brute kernel WG limit: %zu, preferred multiple: %zu\n",
            bgpu_brute_kernel_max_work_group_size,
            bgpu_brute_kernel_preferred_wg_multiple);

    if (need_mitm) {
        bgpu_program_mitm_prefix = bgpu_build_program(
            bgpu_kernel_mitm_prefix_source, "crc32_mitm_prefix");
        bgpu_kernel_mitm_prefix = clCreateKernel(
            bgpu_program_mitm_prefix, "crc32_mitm_prefix", &err);
        BGPU_CL_CHECK(err);

        bgpu_program_mitm_g_suffix = bgpu_build_program(
            bgpu_kernel_mitm_g_suffix_source, "crc32_mitm_g_suffix");
        bgpu_kernel_mitm_g_suffix = clCreateKernel(
            bgpu_program_mitm_g_suffix, "crc32_mitm_g_suffix", &err);
        BGPU_CL_CHECK(err);

        bgpu_program_mitm_match = bgpu_build_program(
            bgpu_kernel_mitm_match_source, "crc32_mitm_match");
        bgpu_kernel_mitm_match = clCreateKernel(
            bgpu_program_mitm_match, "crc32_mitm_match", &err);
        BGPU_CL_CHECK(err);

        bgpu_buf_mitm_match_target_ids = clCreateBuffer(bgpu_context, CL_MEM_WRITE_ONLY,
            BGPU_MITM_MAX_MATCHES * sizeof(uint32_t), NULL, &err);
        BGPU_CL_CHECK(err);
        bgpu_buf_mitm_match_prefix_ids = clCreateBuffer(bgpu_context, CL_MEM_WRITE_ONLY,
            BGPU_MITM_MAX_MATCHES * sizeof(uint32_t), NULL, &err);
        BGPU_CL_CHECK(err);
        bgpu_buf_mitm_match_suffix_ids = clCreateBuffer(bgpu_context, CL_MEM_WRITE_ONLY,
            BGPU_MITM_MAX_MATCHES * sizeof(uint32_t), NULL, &err);
        BGPU_CL_CHECK(err);
        bgpu_buf_mitm_match_count = clCreateBuffer(bgpu_context, CL_MEM_READ_WRITE,
            sizeof(uint32_t), NULL, &err);
        BGPU_CL_CHECK(err);

        fprintf(stderr, "MITM kernels compiled\n");
    }

    fprintf(stderr, "OpenCL initialized\n\n");
}

static void bgpu_cleanup_opencl(int had_mitm)
{
    clReleaseMemObject(bgpu_buf_crc_table);
    clReleaseMemObject(bgpu_buf_targets);
    clReleaseMemObject(bgpu_buf_charset);
    clReleaseMemObject(bgpu_buf_match_hashes);
    clReleaseMemObject(bgpu_buf_match_indices);
    clReleaseMemObject(bgpu_buf_match_count);
    clReleaseMemObject(bgpu_buf_buckets);
    clReleaseKernel(bgpu_kernel_brute);
    clReleaseProgram(bgpu_program_brute);

    if (had_mitm) {
        if (bgpu_buf_mitm_prefix_crcs) clReleaseMemObject(bgpu_buf_mitm_prefix_crcs);
        if (bgpu_buf_mitm_prefix_buckets) clReleaseMemObject(bgpu_buf_mitm_prefix_buckets);
        if (bgpu_buf_mitm_g_tables) clReleaseMemObject(bgpu_buf_mitm_g_tables);
        if (bgpu_buf_mitm_g_values) clReleaseMemObject(bgpu_buf_mitm_g_values);
        if (bgpu_buf_mitm_adj_targets) clReleaseMemObject(bgpu_buf_mitm_adj_targets);
        clReleaseMemObject(bgpu_buf_mitm_match_target_ids);
        clReleaseMemObject(bgpu_buf_mitm_match_prefix_ids);
        clReleaseMemObject(bgpu_buf_mitm_match_suffix_ids);
        clReleaseMemObject(bgpu_buf_mitm_match_count);
        clReleaseKernel(bgpu_kernel_mitm_prefix);
        clReleaseKernel(bgpu_kernel_mitm_g_suffix);
        clReleaseKernel(bgpu_kernel_mitm_match);
        clReleaseProgram(bgpu_program_mitm_prefix);
        clReleaseProgram(bgpu_program_mitm_g_suffix);
        clReleaseProgram(bgpu_program_mitm_match);
    }

    clReleaseCommandQueue(bgpu_queue);
    clReleaseContext(bgpu_context);

    free(bgpu_prefix_bucket_starts);
}

/* ========================================================================= */
/* GPU brute-force per length                                                */
/* ========================================================================= */

static int bgpu_total_matches = 0;

static double bgpu_brute_force_length_run(int str_len, uint64_t batch_size,
                                          int print_progress, int verify_matches,
                                          int *out_matches)
{
    uint64_t total_strings = bgpu_candidate_count_for_length(str_len);

    double t0 = get_time();
    int matches_at_len = 0;
    uint64_t strings_done = 0;

    uint32_t sl = (uint32_t)str_len;
    BGPU_CL_CHECK(clSetKernelArg(bgpu_kernel_brute, 7, sizeof(uint32_t), &sl));
    uint32_t items_per_work_item = bgpu_brute_items_per_work_item;
    BGPU_CL_CHECK(clSetKernelArg(bgpu_kernel_brute, 10,
        sizeof(uint32_t), &items_per_work_item));

    for (uint64_t batch_offset = 0; batch_offset < total_strings;
         batch_offset += batch_size) {
        uint64_t this_batch = batch_size;
        if (batch_offset + this_batch > total_strings)
            this_batch = total_strings - batch_offset;

        BGPU_CL_CHECK(clSetKernelArg(bgpu_kernel_brute, 8,
            sizeof(uint64_t), &batch_offset));
        BGPU_CL_CHECK(clSetKernelArg(bgpu_kernel_brute, 9,
            sizeof(uint64_t), &this_batch));

        uint64_t work_items = (this_batch + (uint64_t)bgpu_brute_items_per_work_item - 1) /
                              (uint64_t)bgpu_brute_items_per_work_item;
        size_t global_size = (size_t)(((work_items + (uint64_t)bgpu_brute_local_size - 1) /
                                       (uint64_t)bgpu_brute_local_size) *
                                      (uint64_t)bgpu_brute_local_size);

        uint32_t zero = 0;
        BGPU_CL_CHECK(clEnqueueWriteBuffer(bgpu_queue, bgpu_buf_match_count, CL_FALSE,
                                            0, sizeof(uint32_t), &zero, 0, NULL, NULL));
        BGPU_CL_CHECK(clEnqueueNDRangeKernel(bgpu_queue, bgpu_kernel_brute, 1, NULL,
                                              &global_size, &bgpu_brute_local_size, 0, NULL, NULL));

        uint32_t num_matches;
        BGPU_CL_CHECK(clEnqueueReadBuffer(bgpu_queue, bgpu_buf_match_count, CL_TRUE,
                                           0, sizeof(uint32_t), &num_matches,
                                           0, NULL, NULL));

        if (num_matches > 0 && verify_matches) {
            if (num_matches > BGPU_MAX_MATCHES_PER_BATCH)
                num_matches = BGPU_MAX_MATCHES_PER_BATCH;

            uint32_t match_hashes[BGPU_MAX_MATCHES_PER_BATCH];
            uint32_t match_indices[BGPU_MAX_MATCHES_PER_BATCH];
            BGPU_CL_CHECK(clEnqueueReadBuffer(bgpu_queue, bgpu_buf_match_hashes, CL_TRUE,
                0, num_matches * sizeof(uint32_t), match_hashes, 0, NULL, NULL));
            BGPU_CL_CHECK(clEnqueueReadBuffer(bgpu_queue, bgpu_buf_match_indices, CL_TRUE,
                0, num_matches * sizeof(uint32_t), match_indices, 0, NULL, NULL));

            for (uint32_t i = 0; i < num_matches; i++) {
                uint64_t abs_idx = batch_offset + (uint64_t)match_indices[i];
                int target_idx = bgpu_find_target_index_by_adjusted(match_hashes[i]);
                char prefix[MAX_NAME_LEN];
                char full[MAX_NAME_LEN];
                bgpu_index_to_string(abs_idx, str_len, prefix);
                bgpu_build_full_name(full, sizeof(full), prefix);

                uint32_t verify = bgpu_compute_crc32_with_suffix(prefix);
                if (target_idx >= 0 && verify == bgpu_targets[target_idx]) {
                    printf("0x%08X : %s\n", bgpu_targets[target_idx], full);
                    fflush(stdout);
                    bgpu_mark_target_found_index(target_idx);
                    matches_at_len++;
                    bgpu_total_matches++;
                } else {
                    fprintf(stderr,
                        "  WARNING: GPU match verification failed for '%s' "
                        "(GPU: 0x%08X, CPU: 0x%08X)\n",
                        full, target_idx >= 0 ? bgpu_targets[target_idx] : match_hashes[i], verify);
                }
            }
        }

        strings_done += this_batch;
        double elapsed = get_time() - t0;
        double rate = strings_done / elapsed / 1e9;
        if (print_progress) {
            fprintf(stderr,
                "\r  Length %2d: %12llu / %12llu (%.1f%%), %.1f B/s, %d match(es), %.1fs",
                str_len, (unsigned long long)strings_done,
                (unsigned long long)total_strings,
                100.0 * strings_done / total_strings,
                rate, matches_at_len, elapsed);
        }
    }

    double elapsed = get_time() - t0;
    double rate = total_strings / elapsed / 1e9;
    if (print_progress) {
        fprintf(stderr, "\r%80s\r", "");
        fprintf(stderr,
            "  Length %2d: %12llu strings, %d match(es), %.2fs (%.1f B/s)\n",
            str_len, (unsigned long long)total_strings, matches_at_len, elapsed, rate);
    }
    if (out_matches)
        *out_matches = matches_at_len;
    return rate;
}

static void bgpu_brute_force_length(int str_len, uint64_t batch_size)
{
    (void)bgpu_brute_force_length_run(str_len, batch_size, 1, 1, NULL);
}

/* ========================================================================= */
/* GPU meet-in-the-middle                                                    */
/* ========================================================================= */

static void bgpu_meet_in_middle_gpu(int min_len, int max_len, uint64_t batch_size)
{
    cl_int err;
    uint64_t div_magic = UINT64_MAX / (uint64_t)bgpu_charset_size + 1;

    for (int total_len = min_len; total_len <= max_len; total_len++) {
        double t0 = get_time();

        int plen, slen;
        if (bgpu_charset_size <= 27) {
            plen = (total_len <= 10) ? total_len / 2 : 6;
        } else {
            plen = (total_len <= 10) ? total_len / 2 : 5;
        }
        if (plen < 1) plen = 1;
        slen = total_len - plen;
        if (slen < 1) { slen = 1; plen = total_len - 1; }

        uint64_t prefix_count = bgpu_candidate_count_for_length(plen);
        uint64_t suffix_count = 1;
        for (int i = 0; i < slen; i++)
            suffix_count *= (uint64_t)bgpu_charset_size;

        fprintf(stderr,
            "  MITM length %2d (split %d+%d): %llu prefixes, %llu suffixes\n",
            total_len, plen, slen,
            (unsigned long long)prefix_count,
            (unsigned long long)suffix_count);

        /* Step 1: GPU prefix enumeration */
        double t1 = get_time();

        if (bgpu_buf_mitm_prefix_crcs) clReleaseMemObject(bgpu_buf_mitm_prefix_crcs);
        bgpu_buf_mitm_prefix_crcs = clCreateBuffer(bgpu_context, CL_MEM_READ_WRITE,
            prefix_count * sizeof(uint32_t), NULL, &err);
        BGPU_CL_CHECK(err);

        BGPU_CL_CHECK(clSetKernelArg(bgpu_kernel_mitm_prefix, 0,
            sizeof(cl_mem), &bgpu_buf_crc_table));
        BGPU_CL_CHECK(clSetKernelArg(bgpu_kernel_mitm_prefix, 1,
            sizeof(cl_mem), &bgpu_buf_charset));
        uint32_t cs = (uint32_t)bgpu_charset_size;
        BGPU_CL_CHECK(clSetKernelArg(bgpu_kernel_mitm_prefix, 2,
            sizeof(uint32_t), &cs));
        uint32_t fcc = (uint32_t)bgpu_variable_first_char_count();
        BGPU_CL_CHECK(clSetKernelArg(bgpu_kernel_mitm_prefix, 3,
            sizeof(uint32_t), &fcc));
        uint32_t pl = (uint32_t)plen;
        BGPU_CL_CHECK(clSetKernelArg(bgpu_kernel_mitm_prefix, 4,
            sizeof(uint32_t), &pl));
        BGPU_CL_CHECK(clSetKernelArg(bgpu_kernel_mitm_prefix, 5,
            sizeof(uint32_t), &bgpu_initial_crc));
        BGPU_CL_CHECK(clSetKernelArg(bgpu_kernel_mitm_prefix, 7,
            sizeof(cl_mem), &bgpu_buf_mitm_prefix_crcs));
        BGPU_CL_CHECK(clSetKernelArg(bgpu_kernel_mitm_prefix, 8,
            sizeof(uint64_t), &div_magic));

        size_t local_size = bgpu_brute_local_size;
        for (uint64_t offset = 0; offset < prefix_count; offset += batch_size) {
            uint64_t this_batch = batch_size;
            if (offset + this_batch > prefix_count)
                this_batch = prefix_count - offset;
            size_t global_size = ((this_batch + local_size - 1) / local_size) * local_size;
            BGPU_CL_CHECK(clSetKernelArg(bgpu_kernel_mitm_prefix, 6,
                sizeof(uint64_t), &offset));
            BGPU_CL_CHECK(clEnqueueNDRangeKernel(bgpu_queue, bgpu_kernel_mitm_prefix,
                1, NULL, &global_size, &local_size, 0, NULL, NULL));
        }
        clFinish(bgpu_queue);

        uint32_t *h_prefix_crcs = malloc(prefix_count * sizeof(uint32_t));
        BGPU_CL_CHECK(clEnqueueReadBuffer(bgpu_queue, bgpu_buf_mitm_prefix_crcs, CL_TRUE,
            0, prefix_count * sizeof(uint32_t), h_prefix_crcs, 0, NULL, NULL));

        fprintf(stderr, "    Prefix enumeration: %.2fs\n", get_time() - t1);

        /* Step 2: Sort prefix table + build buckets (CPU) */
        double t2 = get_time();

        uint64_t *sort_buf = malloc(prefix_count * sizeof(uint64_t));
        for (uint64_t i = 0; i < prefix_count; i++)
            sort_buf[i] = ((uint64_t)h_prefix_crcs[i] << 32) | i;

        qsort(sort_buf, (size_t)prefix_count, sizeof(uint64_t), bgpu_uint64_cmp);

        uint32_t *h_prefix_idxs = malloc(prefix_count * sizeof(uint32_t));
        for (uint64_t i = 0; i < prefix_count; i++) {
            h_prefix_crcs[i] = (uint32_t)(sort_buf[i] >> 32);
            h_prefix_idxs[i] = (uint32_t)(sort_buf[i] & 0xFFFFFFFF);
        }
        free(sort_buf);

        bgpu_build_prefix_buckets(h_prefix_crcs, (size_t)prefix_count);

        fprintf(stderr, "    Sort + buckets: %.2fs\n", get_time() - t2);

        /* Step 3: Upload sorted prefix data to GPU */
        double t3 = get_time();

        BGPU_CL_CHECK(clEnqueueWriteBuffer(bgpu_queue, bgpu_buf_mitm_prefix_crcs, CL_TRUE,
            0, prefix_count * sizeof(uint32_t), h_prefix_crcs, 0, NULL, NULL));

        if (bgpu_buf_mitm_prefix_buckets) clReleaseMemObject(bgpu_buf_mitm_prefix_buckets);
        bgpu_buf_mitm_prefix_buckets = clCreateBuffer(bgpu_context,
            CL_MEM_READ_ONLY | CL_MEM_COPY_HOST_PTR,
            BGPU_NUM_BUCKETS * sizeof(uint32_t), bgpu_prefix_bucket_starts, &err);
        BGPU_CL_CHECK(err);

        /* Step 4: Precompute g_tables + adjusted targets, upload */
        bgpu_build_g_tables(slen);
        bgpu_compute_adjusted_targets(slen);

        if (bgpu_buf_mitm_g_tables) clReleaseMemObject(bgpu_buf_mitm_g_tables);
        bgpu_buf_mitm_g_tables = clCreateBuffer(bgpu_context,
            CL_MEM_READ_ONLY | CL_MEM_COPY_HOST_PTR,
            slen * 256 * sizeof(uint32_t), bgpu_g_tables, &err);
        BGPU_CL_CHECK(err);

        if (bgpu_buf_mitm_adj_targets) clReleaseMemObject(bgpu_buf_mitm_adj_targets);
        bgpu_buf_mitm_adj_targets = clCreateBuffer(bgpu_context,
            CL_MEM_READ_ONLY | CL_MEM_COPY_HOST_PTR,
            bgpu_sorted_target_count * sizeof(uint32_t), bgpu_adj_targets_buf, &err);
        BGPU_CL_CHECK(err);

        fprintf(stderr, "    Upload: %.2fs\n", get_time() - t3);

        /* Step 5: GPU G(suffix) precomputation */
        double t4 = get_time();

        if (bgpu_buf_mitm_g_values) clReleaseMemObject(bgpu_buf_mitm_g_values);
        bgpu_buf_mitm_g_values = clCreateBuffer(bgpu_context, CL_MEM_READ_WRITE,
            suffix_count * sizeof(uint32_t), NULL, &err);
        BGPU_CL_CHECK(err);

        BGPU_CL_CHECK(clSetKernelArg(bgpu_kernel_mitm_g_suffix, 0,
            sizeof(cl_mem), &bgpu_buf_mitm_g_tables));
        BGPU_CL_CHECK(clSetKernelArg(bgpu_kernel_mitm_g_suffix, 1,
            sizeof(cl_mem), &bgpu_buf_charset));
        BGPU_CL_CHECK(clSetKernelArg(bgpu_kernel_mitm_g_suffix, 2,
            sizeof(uint32_t), &cs));
        uint32_t sl = (uint32_t)slen;
        BGPU_CL_CHECK(clSetKernelArg(bgpu_kernel_mitm_g_suffix, 3,
            sizeof(uint32_t), &sl));
        BGPU_CL_CHECK(clSetKernelArg(bgpu_kernel_mitm_g_suffix, 5,
            sizeof(cl_mem), &bgpu_buf_mitm_g_values));
        BGPU_CL_CHECK(clSetKernelArg(bgpu_kernel_mitm_g_suffix, 6,
            sizeof(uint64_t), &div_magic));

        for (uint64_t offset = 0; offset < suffix_count; offset += batch_size) {
            uint64_t this_batch = batch_size;
            if (offset + this_batch > suffix_count)
                this_batch = suffix_count - offset;
            size_t global_size = ((this_batch + local_size - 1) / local_size) * local_size;
            BGPU_CL_CHECK(clSetKernelArg(bgpu_kernel_mitm_g_suffix, 4,
                sizeof(uint64_t), &offset));
            BGPU_CL_CHECK(clEnqueueNDRangeKernel(bgpu_queue, bgpu_kernel_mitm_g_suffix,
                1, NULL, &global_size, &local_size, 0, NULL, NULL));
        }
        clFinish(bgpu_queue);

        fprintf(stderr, "    G-suffix precompute: %.2fs\n", get_time() - t4);

        /* Step 6: GPU 2D match kernel */
        double t5 = get_time();

        BGPU_CL_CHECK(clSetKernelArg(bgpu_kernel_mitm_match, 0,
            sizeof(cl_mem), &bgpu_buf_mitm_g_values));
        BGPU_CL_CHECK(clSetKernelArg(bgpu_kernel_mitm_match, 1,
            sizeof(cl_mem), &bgpu_buf_mitm_adj_targets));
        uint32_t tc2 = (uint32_t)bgpu_sorted_target_count;
        BGPU_CL_CHECK(clSetKernelArg(bgpu_kernel_mitm_match, 2,
            sizeof(uint32_t), &tc2));
        BGPU_CL_CHECK(clSetKernelArg(bgpu_kernel_mitm_match, 3,
            sizeof(cl_mem), &bgpu_buf_mitm_prefix_crcs));
        BGPU_CL_CHECK(clSetKernelArg(bgpu_kernel_mitm_match, 4,
            sizeof(cl_mem), &bgpu_buf_mitm_prefix_buckets));
        BGPU_CL_CHECK(clSetKernelArg(bgpu_kernel_mitm_match, 6,
            sizeof(cl_mem), &bgpu_buf_mitm_match_target_ids));
        BGPU_CL_CHECK(clSetKernelArg(bgpu_kernel_mitm_match, 7,
            sizeof(cl_mem), &bgpu_buf_mitm_match_prefix_ids));
        BGPU_CL_CHECK(clSetKernelArg(bgpu_kernel_mitm_match, 8,
            sizeof(cl_mem), &bgpu_buf_mitm_match_suffix_ids));
        BGPU_CL_CHECK(clSetKernelArg(bgpu_kernel_mitm_match, 9,
            sizeof(cl_mem), &bgpu_buf_mitm_match_count));
        uint32_t mitm_max = BGPU_MITM_MAX_MATCHES;
        BGPU_CL_CHECK(clSetKernelArg(bgpu_kernel_mitm_match, 10,
            sizeof(uint32_t), &mitm_max));

        uint64_t suffix_batch = 256 * 1024;
        size_t local_2d[2] = {bgpu_brute_local_size, 1};
        size_t target_global =
            ((bgpu_sorted_target_count + local_2d[1] - 1) / local_2d[1]) * local_2d[1];

        int matches_at_len = 0;
        uint64_t suffixes_done = 0;

        for (uint64_t soff = 0; soff < suffix_count; soff += suffix_batch) {
            uint64_t this_suffix_batch = suffix_batch;
            if (soff + this_suffix_batch > suffix_count)
                this_suffix_batch = suffix_count - soff;

            size_t suffix_global =
                ((this_suffix_batch + local_2d[0] - 1) / local_2d[0]) * local_2d[0];
            size_t global_2d[2] = {suffix_global, target_global};

            uint32_t zero = 0;
            BGPU_CL_CHECK(clEnqueueWriteBuffer(bgpu_queue, bgpu_buf_mitm_match_count,
                CL_FALSE, 0, sizeof(uint32_t), &zero, 0, NULL, NULL));

            BGPU_CL_CHECK(clSetKernelArg(bgpu_kernel_mitm_match, 5,
                sizeof(uint64_t), &soff));

            BGPU_CL_CHECK(clEnqueueNDRangeKernel(bgpu_queue, bgpu_kernel_mitm_match,
                2, NULL, global_2d, local_2d, 0, NULL, NULL));

            uint32_t num_matches;
            BGPU_CL_CHECK(clEnqueueReadBuffer(bgpu_queue, bgpu_buf_mitm_match_count,
                CL_TRUE, 0, sizeof(uint32_t), &num_matches, 0, NULL, NULL));

            if (num_matches > 0) {
                if (num_matches > BGPU_MITM_MAX_MATCHES) {
                    fprintf(stderr,
                        "\n  WARNING: %u matches exceeded buffer (%d), %u lost\n",
                        num_matches, BGPU_MITM_MAX_MATCHES,
                        num_matches - BGPU_MITM_MAX_MATCHES);
                    num_matches = BGPU_MITM_MAX_MATCHES;
                }

                uint32_t *m_target_ids = malloc(num_matches * sizeof(uint32_t));
                uint32_t *m_prefix_ids = malloc(num_matches * sizeof(uint32_t));
                uint32_t *m_suffix_ids = malloc(num_matches * sizeof(uint32_t));
                BGPU_CL_CHECK(clEnqueueReadBuffer(bgpu_queue,
                    bgpu_buf_mitm_match_target_ids, CL_TRUE,
                    0, num_matches * sizeof(uint32_t), m_target_ids, 0, NULL, NULL));
                BGPU_CL_CHECK(clEnqueueReadBuffer(bgpu_queue,
                    bgpu_buf_mitm_match_prefix_ids, CL_TRUE,
                    0, num_matches * sizeof(uint32_t), m_prefix_ids, 0, NULL, NULL));
                BGPU_CL_CHECK(clEnqueueReadBuffer(bgpu_queue,
                    bgpu_buf_mitm_match_suffix_ids, CL_TRUE,
                    0, num_matches * sizeof(uint32_t), m_suffix_ids, 0, NULL, NULL));

                for (uint32_t i = 0; i < num_matches; i++) {
                    uint32_t adjusted_target = bgpu_sorted_targets[m_target_ids[i]];
                    int target_idx = bgpu_find_target_index_by_adjusted(adjusted_target);
                    uint32_t original_prefix_idx = h_prefix_idxs[m_prefix_ids[i]];
                    uint64_t suffix_idx = soff + (uint64_t)m_suffix_ids[i];

                    char prefix_str[MAX_NAME_LEN], suffix_str[MAX_NAME_LEN], full[MAX_NAME_LEN];
                    bgpu_index_to_string(original_prefix_idx, plen, prefix_str);
                    bgpu_index_to_string_suffix(suffix_idx, slen, suffix_str);
                    snprintf(full, sizeof(full), "%s%s", prefix_str, suffix_str);
                    char full_with_suffix[MAX_NAME_LEN];
                    bgpu_build_full_name(full_with_suffix, sizeof(full_with_suffix), full);

                    uint32_t verify = bgpu_compute_crc32_with_suffix(full);
                    if (target_idx >= 0 && verify == bgpu_targets[target_idx]) {
                        printf("0x%08X : %s\n", bgpu_targets[target_idx], full_with_suffix);
                        fflush(stdout);
                        bgpu_mark_target_found_index(target_idx);
                        matches_at_len++;
                        bgpu_total_matches++;
                    } else {
                        fprintf(stderr,
                            "  WARNING: MITM match verification failed for '%s' "
                            "(expected 0x%08X, got 0x%08X)\n",
                            full_with_suffix,
                            target_idx >= 0 ? bgpu_targets[target_idx] : adjusted_target,
                            verify);
                    }
                }

                free(m_target_ids);
                free(m_prefix_ids);
                free(m_suffix_ids);
            }

            suffixes_done += this_suffix_batch;
            double elapsed = get_time() - t5;
            double rate = (double)suffixes_done * bgpu_sorted_target_count / elapsed / 1e9;
            fprintf(stderr,
                "\r    Match: %12llu / %12llu suffixes (%.1f%%), "
                "%.1f B pairs/s, %d match(es), %.1fs",
                (unsigned long long)suffixes_done,
                (unsigned long long)suffix_count,
                100.0 * suffixes_done / suffix_count,
                rate, matches_at_len, elapsed);
        }

        double match_elapsed = get_time() - t5;
        fprintf(stderr, "\r%80s\r", "");
        fprintf(stderr, "    Match: %.2fs, %d match(es)\n",
                match_elapsed, matches_at_len);

        free(h_prefix_crcs);
        free(h_prefix_idxs);

        double total_elapsed = get_time() - t0;
        fprintf(stderr, "  MITM length %2d: total %.2fs, %d match(es)\n\n",
                total_len, total_elapsed, matches_at_len);
    }
}

/* ========================================================================= */
/* Entry point                                                               */
/* ========================================================================= */

static int cmd_brute_gpu(int argc, char **argv)
{
    int min_brute = 1;
    int max_brute = 10;
    int max_mitm = 0;
    uint64_t batch_size = 16 * 1024 * 1024;
    const char *hash_file = NULL;
    const char *fixed_prefix = NULL;
    const char *fixed_suffix = NULL;
    size_t requested_local_size = 256;
    uint32_t requested_items_per_work_item = 1;

    int argi = 1;
    while (argi < argc && argv[argi][0] == '-') {
        if (strcmp(argv[argi], "-h") == 0 || strcmp(argv[argi], "--help") == 0) {
            fprintf(stderr,
                "Usage: qbkey_pipeline brute-gpu [options] hash1 [hash2 ...]\n"
                "\n"
                "GPU-accelerated brute-force reversal of Neversoft CRC-32 hashes.\n"
                "Algorithm: reflected CRC-32, init 0xFFFFFFFF, no final XOR, lowercase.\n"
                "\n"
                "Options:\n"
                "  -f FILE   Read target hashes from file (one hex per line)\n"
                "  -l N      Min variable length to search (default: 1)\n"
                "  -m N      Max brute-force length (default: 10)\n"
                "  -M N      Max MITM length (default: 0 = disabled)\n"
                "  -b N      Batch size in millions (default: 16)\n"
                "  -w N      Local work-group size (default: 256)\n"
                "  -n N      Candidates per work-item (default: 1)\n"
                "  -d        Include digits [0-9] in charset (default: [a-z_] only)\n"
                "  -p TEXT   Prepend fixed prefix to every candidate before hashing\n"
                "  -s TEXT   Append fixed suffix to every candidate before hashing\n"
                "  -h        Show this help\n"
                "\n"
                "Examples:\n"
                "  qbkey_pipeline brute-gpu -f unmatched_qb_hashes.txt -m 9 -M 12\n"
                "  qbkey_pipeline brute-gpu -f unmatched_qb_hashes.txt -m 8 -M 10 -d\n"
                "  qbkey_pipeline brute-gpu -f hashes.txt -m 4 -M 6 -p ped_ -s .png\n"
                "  qbkey_pipeline brute-gpu -f hashes.txt -l 6 -m 6 -p ped_acl_ -s _head.png\n"
                "  qbkey_pipeline brute-gpu -f unmatched_qb_hashes.txt -m 8 -M 10 -s .png\n"
                "  qbkey_pipeline brute-gpu -f unmatched_qb_hashes.txt -m 8 -w 128 -n 4 -s .png\n");
            return 0;
        } else if (strcmp(argv[argi], "-f") == 0 && argi + 1 < argc) {
            hash_file = argv[++argi];
        } else if (strcmp(argv[argi], "-l") == 0 && argi + 1 < argc) {
            min_brute = atoi(argv[++argi]);
        } else if (strcmp(argv[argi], "-m") == 0 && argi + 1 < argc) {
            max_brute = atoi(argv[++argi]);
        } else if (strcmp(argv[argi], "-M") == 0 && argi + 1 < argc) {
            max_mitm = atoi(argv[++argi]);
        } else if (strcmp(argv[argi], "-b") == 0 && argi + 1 < argc) {
            batch_size = (uint64_t)atoi(argv[++argi]) * 1024 * 1024;
        } else if (strcmp(argv[argi], "-w") == 0 && argi + 1 < argc) {
            requested_local_size = (size_t)strtoul(argv[++argi], NULL, 10);
        } else if (strcmp(argv[argi], "-n") == 0 && argi + 1 < argc) {
            requested_items_per_work_item = (uint32_t)strtoul(argv[++argi], NULL, 10);
        } else if (strcmp(argv[argi], "-d") == 0) {
            bgpu_include_digits = 1;
        } else if (strcmp(argv[argi], "-p") == 0 && argi + 1 < argc) {
            fixed_prefix = argv[++argi];
        } else if (strcmp(argv[argi], "-s") == 0 && argi + 1 < argc) {
            fixed_suffix = argv[++argi];
        } else {
            uint32_t h;
            if (bgpu_parse_hash(argv[argi], &h) &&
                bgpu_target_count < BGPU_MAX_TARGETS) {
                bgpu_targets[bgpu_target_count] = h;
                bgpu_target_found[bgpu_target_count] = 0;
                bgpu_target_count++;
            } else {
                fprintf(stderr, "Unknown option: %s\n", argv[argi]);
                return 1;
            }
        }
        argi++;
    }

    while (argi < argc) {
        uint32_t h;
        const char *s = argv[argi];
        if (s[0] == '0' && (s[1] == 'x' || s[1] == 'X'))
            s += 2;
        if (bgpu_parse_hash(s, &h) && bgpu_target_count < BGPU_MAX_TARGETS) {
            bgpu_targets[bgpu_target_count] = h;
            bgpu_target_found[bgpu_target_count] = 0;
            bgpu_target_count++;
        } else {
            fprintf(stderr, "Cannot parse hash: %s\n", argv[argi]);
        }
        argi++;
    }

    if (hash_file) {
        if (!bgpu_load_hashes_from_file(hash_file))
            return 1;
    }

    if (bgpu_target_count == 0) {
        fprintf(stderr, "No target hashes specified.\n");
        return 1;
    }
    if (requested_local_size == 0) {
        fprintf(stderr, "Local work-group size must be > 0.\n");
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
    if (requested_items_per_work_item == 0) {
        fprintf(stderr, "Candidates per work-item must be > 0.\n");
        return 1;
    }

    /* Initialize */
    bgpu_init_crc_inv_table();
    bgpu_init_charset();
    if (fixed_prefix) {
        size_t prefix_len = strlen(fixed_prefix);
        if (prefix_len >= sizeof(bgpu_fixed_prefix)) {
            fprintf(stderr, "Fixed prefix too long (%zu >= %zu)\n",
                    prefix_len, sizeof(bgpu_fixed_prefix));
            return 1;
        }
        memcpy(bgpu_fixed_prefix, fixed_prefix, prefix_len + 1);
        bgpu_fixed_prefix_len = (int)prefix_len;
        for (int i = 0; i < bgpu_fixed_prefix_len; i++)
            bgpu_initial_crc = bgpu_crc_step(bgpu_initial_crc, (uint8_t)bgpu_fixed_prefix[i]);
    }
    if (fixed_suffix) {
        size_t suffix_len = strlen(fixed_suffix);
        if (suffix_len >= sizeof(bgpu_fixed_suffix)) {
            fprintf(stderr, "Fixed suffix too long (%zu >= %zu)\n",
                    suffix_len, sizeof(bgpu_fixed_suffix));
            return 1;
        }
        memcpy(bgpu_fixed_suffix, fixed_suffix, suffix_len + 1);
        bgpu_fixed_suffix_len = (int)suffix_len;
    }
    for (int i = 0; i < bgpu_target_count; i++)
        bgpu_match_targets[i] = bgpu_adjust_target_for_suffix(bgpu_targets[i]);
    bgpu_sort_targets();

    int need_mitm = (max_mitm > max_brute);

    fprintf(stderr, "Charset: %d characters (%s)\n",
            bgpu_charset_size,
            bgpu_include_digits ? "a-z, 0-9, _" : "a-z, _");
    fprintf(stderr, "Targets: %d hash(es) (%d unique)\n",
            bgpu_target_count, bgpu_sorted_target_count);
    if (bgpu_fixed_prefix_len > 0)
        fprintf(stderr, "Fixed prefix: \"%s\"\n", bgpu_fixed_prefix);
    if (bgpu_fixed_suffix_len > 0)
        fprintf(stderr, "Fixed suffix: \"%s\"\n", bgpu_fixed_suffix);
    bgpu_brute_local_size = requested_local_size;
    bgpu_brute_items_per_work_item = requested_items_per_work_item;
    fprintf(stderr, "Local size: %zu\n", bgpu_brute_local_size);
    fprintf(stderr, "Candidates/work-item: %u\n", bgpu_brute_items_per_work_item);
    fprintf(stderr, "Brute-force: variable lengths %d-%d%s%s\n", min_brute, max_brute,
            bgpu_fixed_prefix_len > 0 ? " (prefix prepended before search)" : "",
            bgpu_fixed_suffix_len > 0 ? " (suffix appended after search)" : "");
    if (need_mitm)
        fprintf(stderr, "Meet-in-the-middle: variable lengths %d-%d%s%s\n",
                (max_brute + 1) > min_brute ? (max_brute + 1) : min_brute, max_mitm,
                bgpu_fixed_prefix_len > 0 ? " (prefix prepended before search)" : "",
                bgpu_fixed_suffix_len > 0 ? " (suffix appended after search)" : "");
    fprintf(stderr, "Batch size: %llu\n\n",
            (unsigned long long)batch_size);

    bgpu_init_opencl(need_mitm);
    if (bgpu_brute_local_size > bgpu_brute_kernel_max_work_group_size) {
        fprintf(stderr,
            "Requested local size %zu exceeds kernel/device limit %zu.\n",
            bgpu_brute_local_size, bgpu_brute_kernel_max_work_group_size);
        bgpu_cleanup_opencl(need_mitm);
        return 1;
    }

    double t_start = get_time();

    if (max_brute >= min_brute) {
        fprintf(stderr, "--- Phase 1: GPU Brute-force (lengths %d-%d) ---\n",
                min_brute, max_brute);
        for (int len = min_brute; len <= max_brute; len++)
            bgpu_brute_force_length(len, batch_size);
        fprintf(stderr,
            "--- Phase 1 complete: brute-forced lengths %d-%d, "
            "%d match(es) so far ---\n\n",
            min_brute, max_brute, bgpu_total_matches);
    }

    if (need_mitm) {
        int mitm_start = max_brute + 1;
        if (mitm_start < min_brute) mitm_start = min_brute;
        if (mitm_start < 2) mitm_start = 2;
        fprintf(stderr,
            "--- Phase 2: GPU Meet-in-the-middle (lengths %d-%d) ---\n",
            mitm_start, max_mitm);
        bgpu_meet_in_middle_gpu(mitm_start, max_mitm, batch_size);
    }

    double total_elapsed = get_time() - t_start;

    fprintf(stderr, "\n--- Summary ---\n");
    fprintf(stderr, "Total matches: %d\n", bgpu_total_matches);
    fprintf(stderr, "Total time: %.2fs\n", total_elapsed);

    int unmatched = 0;
    for (int i = 0; i < bgpu_target_count; i++) {
        if (bgpu_target_found[i] == 0)
            unmatched++;
    }
    if (unmatched)
        fprintf(stderr, "%d hash(es) had no match\n", unmatched);

    bgpu_cleanup_opencl(need_mitm);
    return 0;
}

#else /* !HAS_OPENCL */

static int cmd_brute_gpu(int argc, char **argv)
{
    (void)argc; (void)argv;
    fprintf(stderr,
        "GPU brute-force not available.\n"
        "Recompile with OpenCL support:\n"
        "  clang -O3 -D_CRT_SECURE_NO_WARNINGS -DHAS_OPENCL -DCL_TARGET_OPENCL_VERSION=120 \\\n"
        "    -I\"<CUDA_PATH>/include\" -L\"<CUDA_PATH>/lib/x64\" -lOpenCL \\\n"
        "    -o qbkey_pipeline.exe qbkey_pipeline.c\n");
    return 1;
}

#endif /* HAS_OPENCL */
