/* Issue #54 fixture: fx_count_t names a packed enum fx_kind8 (clang: unsigned 8-bit). */
enum __attribute__((packed)) fx_kind8 { FX_K8 = 0 };
typedef enum fx_kind8 fx_count_t;
