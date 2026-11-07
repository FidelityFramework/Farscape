/* A record whose field type is a typedef carrying may_alias (#175): the attribute moves no byte,
   which clang's measurement of the record shows (8 bytes, alignment 4, `value` at 4), and its
   aliasing fact belongs to the field's type. */
typedef int fx_alias_int __attribute__((__may_alias__));

struct fx_aliased
{
  char tag;
  fx_alias_int value;
};

void fx_aliased_use(struct fx_aliased *aliased);
