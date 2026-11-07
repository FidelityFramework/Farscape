/* A may_alias record with a C11 anonymous member: the member has no C name, so no copy of the
   record without the attribute can read its type through `__typeof__`. */
struct __attribute__((__may_alias__)) fx_alias_member
{
  int tag;
  union { int whole; float real; };
};

void fx_alias_member_use(struct fx_alias_member *alias);
