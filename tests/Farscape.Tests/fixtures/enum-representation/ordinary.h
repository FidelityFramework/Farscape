/* Issue #53 fixture: ordinary C enums; clang chooses their compatible integer types. */
enum fx_plain { FX_PLAIN_A, FX_PLAIN_B };
enum fx_negative { FX_NEGATIVE = -1, FX_POSITIVE = 1 };
typedef enum { FX_ANON_A, FX_ANON_B } fx_anon_t;
