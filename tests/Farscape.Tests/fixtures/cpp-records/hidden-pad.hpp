// A C++ record carrying a zero-width unnamed bit-field, whose name a later object-like macro
// rewrites to an undeclared one (#182): the parse's probe spells `fx_cpp_hidden_pad`, so clang
// measures no layout of it. It must be refused, never laid out from its named fields.
class fx_cpp_hidden_pad
{
public:
  char a;
  int : 0;
  char b;
};

#define fx_cpp_hidden_pad fx_cpp_hidden_other
