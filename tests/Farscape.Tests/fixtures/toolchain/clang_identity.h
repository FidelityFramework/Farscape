/* The clang that evaluates a header's macros names itself (#200): its release and the version
   string its build records. The pinned toolchain's clang records its llvm-project commit. */
#define PIN_CLANG_MAJOR __clang_major__
#define PIN_CLANG_MINOR __clang_minor__
#define PIN_CLANG_PATCHLEVEL __clang_patchlevel__
#define PIN_CLANG_VERSION __clang_version__
