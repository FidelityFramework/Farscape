/* Issue #54 fixture: fx_count_t names a packed enum (clang: unsigned 8-bit). */
enum __attribute__((packed)) fx_kind { FX_ZERO = 0 };
typedef enum fx_kind fx_count_t;
