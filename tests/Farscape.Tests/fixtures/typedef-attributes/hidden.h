/* A record whose field type reaches an aligned(8) typedef, and whose tag a later object-like
   macro hides (#175): the parse's probe spells `struct fx_hidden`, which the macro rewrites to
   an incomplete `struct fx_hidden_other`, so clang measures no layout of it. The record must be
   refused, never laid out from its declared fields. */
typedef int fx_wide_int __attribute__((aligned(8)));

struct fx_hidden
{
  char tag;
  fx_wide_int value;
};

void fx_hidden_use(struct fx_hidden *hidden);

#define fx_hidden fx_hidden_other
