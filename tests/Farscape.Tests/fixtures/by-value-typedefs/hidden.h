/* A by-value parameter whose type reaches an aligned(4) typedef, whose name a later object-like
   macro rewrites to an undeclared one (#181): the parse's probe spells `fx_bv_hidden`, which
   the macro hides, so clang measures no size and alignment of it. Its descriptor claim must be
   refused, never assumed from the type its typedefs stand for. */
typedef int fx_bv_hidden __attribute__((aligned(4)));

void fx_bv_hidden_use(fx_bv_hidden v);

#define fx_bv_hidden fx_bv_hidden_other
