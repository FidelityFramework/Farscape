/* Issue #54 fixture: fx_count_t names an ordinary enum of the same tag (clang: unsigned 32-bit). */
enum fx_kind { FX_ZERO = 0, FX_BIG = 65536 };
typedef enum fx_kind fx_count_t;
