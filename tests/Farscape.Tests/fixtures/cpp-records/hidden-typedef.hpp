// A C++ record whose field type reaches an aligned(8) typedef, and whose name a later
// object-like macro rewrites to an undeclared one (#182): clang measures no layout of it, so it
// must be refused, never laid out from its declared fields.
typedef int fx_cpp_hidden_int __attribute__((aligned(8)));

struct fx_cpp_hidden_wide
{
  char tag;
  fx_cpp_hidden_int value;
};

#define fx_cpp_hidden_wide fx_cpp_hidden_other
