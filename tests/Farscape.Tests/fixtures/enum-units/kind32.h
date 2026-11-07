/* Issues #53/#54 fixture: fx_kind is ordinary here with a value beyond 16 bits (clang: unsigned 32-bit). */
enum fx_kind { FX_ZERO = 0, FX_BIG = 65536 };
typedef enum fx_kind fx_count_t;
int fx_take32(fx_count_t value);
