/* may_alias together with an attribute that has no classified layout contract. */
struct __attribute__((__may_alias__, __deprecated__)) fx_old_addr
{
  unsigned short family;
  char data[14];
};

void fx_old_addr_use(struct fx_old_addr *addr);
