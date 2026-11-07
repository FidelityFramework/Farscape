/* Issue #54 fixture: fx_count_t names an ordinary enum fx_kind32 (clang: unsigned 32-bit). */
enum fx_kind32 { FX_K32 = 0, FX_K32_BIG = 65536 };
typedef enum fx_kind32 fx_count_t;
