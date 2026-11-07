/* Issue #53 fixture: an enumerator beyond 32 bits makes clang choose a 64-bit enum. */
enum fx_wide { FX_WIDE_SMALL = 1, FX_WIDE_BIG = 0x100000000 };
struct fx_wide_rec {
    enum fx_wide kind;
    unsigned long tail;
};
