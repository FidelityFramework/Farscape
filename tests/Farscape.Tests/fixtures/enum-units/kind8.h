/* Issues #53/#54 fixture: fx_kind is packed here (clang: unsigned 8-bit). */
enum __attribute__((packed)) fx_kind { FX_ZERO = 0 };
typedef enum fx_kind fx_count_t;
int fx_take8(fx_count_t value);
