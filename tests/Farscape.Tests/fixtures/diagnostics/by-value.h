/* Structured diagnostics (#184): two functions return a record by value that the pilot declares
   no value type for (FS9119). Clang locates the first at its name; the second is declared
   through a macro, so clang locates it inside a macro expansion and its diagnostic carries the
   reason instead of a line. */
struct fx_pair { int first; int second; };

struct fx_pair fx_pair_make(int first, int second);

#define FX_DECLARE_PAIR(name) struct fx_pair name(void)
FX_DECLARE_PAIR(fx_pair_zero);
