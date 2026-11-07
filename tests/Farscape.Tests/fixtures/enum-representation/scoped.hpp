/* Issue #53 fixture: C++ scoped and fixed-underlying-type enums, measured by clang. */
enum class fx_scoped : unsigned char { FX_SCOPED_A = 1, FX_SCOPED_B = 2 };
enum class fx_scoped_signed : short { FX_SCOPED_NEG = -1 };
enum class fx_scoped_default { FX_SCOPED_DEFAULT_A };
enum fx_cpp_plain { FX_CPP_PLAIN_A };
extern "C" int fx_take_scoped(fx_scoped value);
