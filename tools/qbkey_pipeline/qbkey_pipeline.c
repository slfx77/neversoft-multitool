/*
 * qbkey_pipeline.c — Unified CLI for the QBKey hash resolution pipeline.
 *
 * Unity build: this file #includes all subcommand modules.
 *
 * Compile (basic, no brute-force parallelism):
 *   clang -O3 -D_CRT_SECURE_NO_WARNINGS -o qbkey_pipeline.exe qbkey_pipeline.c
 *
 * Compile with OpenMP (CPU brute-force multithreaded):
 *   clang -O3 -D_CRT_SECURE_NO_WARNINGS -fopenmp -o qbkey_pipeline.exe qbkey_pipeline.c
 *
 * Compile with OpenCL (GPU brute-force):
 *   clang -O3 -D_CRT_SECURE_NO_WARNINGS -DHAS_OPENCL -DCL_TARGET_OPENCL_VERSION=120 ^
 *     -I"C:/Program Files/NVIDIA GPU Computing Toolkit/CUDA/v13.1/include" ^
 *     -L"C:/Program Files/NVIDIA GPU Computing Toolkit/CUDA/v13.1/lib/x64" ^
 *     -lOpenCL -o qbkey_pipeline.exe qbkey_pipeline.c
 *
 * Compile with both (recommended):
 *   clang -O3 -D_CRT_SECURE_NO_WARNINGS -fopenmp -DHAS_OPENCL -DCL_TARGET_OPENCL_VERSION=120 ^
 *     -I"C:/Program Files/NVIDIA GPU Computing Toolkit/CUDA/v13.1/include" ^
 *     -L"C:/Program Files/NVIDIA GPU Computing Toolkit/CUDA/v13.1/lib/x64" ^
 *     -lOpenCL -o qbkey_pipeline.exe qbkey_pipeline.c
 *
 * Usage:
 *   qbkey_pipeline <command> [args]
 *
 * Commands:
 *   collect-hashes [builds_path]             Stage 1: Collect hashes from PSX/THAW/HED files
 *   collect-names  [builds_path]             Collect names from archives/executables
 *   match          [builds_path]             Stage 2: Dictionary-based hash matching
 *   brute          [options] hash1 ...       Stage 3: CPU brute-force + MITM reversal
 *   brute-gpu      [options] hash1 ...       Stage 3: GPU-accelerated brute-force + MITM
 *   filter         [-m maxlen] in.txt out.txt   Filter brute-force results by name length
 *   prefilter      [-n max] in.txt out.txt      Keep N shortest candidates per hash
 *   candidates                               Generate review_candidates.json
 */

#include "common.h"
#include "cmd_collect_hashes.c"
#include "cmd_collect_names.c"
#include "cmd_match.c"
#include "cmd_brute_cpu.c"
#include "cmd_brute_gpu.c"
#include "cmd_filter.c"
#include "cmd_prefilter.c"
#include "cmd_candidates.c"

static void print_usage(void)
{
    fprintf(stderr,
        "QBKey Pipeline — Neversoft hash resolution toolkit\n"
        "\n"
        "Usage: qbkey_pipeline <command> [args]\n"
        "\n"
        "Commands:\n"
        "  collect-hashes [builds_path]               Stage 1: Collect hashes from PSX/THAW/HED\n"
        "  collect-names  [builds_path]               Collect names from archives/executables\n"
        "  match          [builds_path]               Stage 2: Dictionary hash matching\n"
        "  brute          [options] hash1 ...         CPU brute-force + MITM reversal\n"
        "  brute-gpu      [options] hash1 ...         GPU-accelerated brute-force + MITM\n"
        "  filter         [-m maxlen] in.txt out.txt  Filter brute-force by name length\n"
        "  prefilter      [-n max] in.txt out.txt     Keep N shortest candidates per hash\n"
        "  candidates                                 Generate review_candidates.json\n"
        "\n"
        "Pipeline order:\n"
        "  1. collect-hashes   -> hash_targets.json, all_hashes.txt\n"
        "  2. collect-names    -> all_names.txt\n"
        "  3. match            -> matched_hashes.json, unmatched_qb_hashes.txt\n"
        "  4. brute / brute-gpu -f unmatched_qb_hashes.txt ...\n"
        "  5. filter/prefilter -> brute_force_results_prefiltered.txt\n"
        "  6. candidates       -> review_candidates.json\n"
    );
}

int main(int argc, char **argv)
{
    if (argc < 2 || strcmp(argv[1], "--help") == 0 || strcmp(argv[1], "-h") == 0) {
        print_usage();
        return 0;
    }

    const char *cmd = argv[1];

    if (strcmp(cmd, "collect-hashes") == 0)
        return cmd_collect_hashes(argc - 1, argv + 1);
    if (strcmp(cmd, "collect-names") == 0)
        return cmd_collect_names(argc - 1, argv + 1);
    if (strcmp(cmd, "match") == 0)
        return cmd_match(argc - 1, argv + 1);
    if (strcmp(cmd, "brute") == 0)
        return cmd_brute_cpu(argc - 1, argv + 1);
    if (strcmp(cmd, "brute-gpu") == 0)
        return cmd_brute_gpu(argc - 1, argv + 1);
    if (strcmp(cmd, "filter") == 0)
        return cmd_filter(argc - 1, argv + 1);
    if (strcmp(cmd, "prefilter") == 0)
        return cmd_prefilter(argc - 1, argv + 1);
    if (strcmp(cmd, "candidates") == 0)
        return cmd_candidates(argc - 1, argv + 1);

    fprintf(stderr, "Unknown command: %s\n\n", cmd);
    print_usage();
    return 1;
}
